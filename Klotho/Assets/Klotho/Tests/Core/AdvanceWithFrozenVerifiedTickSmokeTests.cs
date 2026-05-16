using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Smoke tests for AdvanceWithFrozenVerifiedTick harness helper:
    ///   1. Helper drives CurrentTick forward while _lastVerifiedTick stays frozen
    ///   2. QuorumMissDropTicks = int.MaxValue prevents auto-seal recovery
    ///   3. EcsSimulation.maxRollbackTicks is independent of SimulationConfig.MaxRollbackTicks —
    ///      ECS snapshot retention can outlast the stalled phase regardless of SnapshotCapacity
    ///      (ring wrap parameter)
    /// </summary>
    [TestFixture]
    public class AdvanceWithFrozenVerifiedTickSmokeTests
    {
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("AdvanceWithFrozenVerifiedTickSmokeTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void Helper_AdvancesCurrentTick_WhileVerifiedChainFrozen()
        {
            // Setup: QuorumMissDropTicks = int.MaxValue disables the CheckQuorumMissPresumedDrop
            // watchdog. Combined with NOT disconnecting the peer (which would add to
            // _disconnectedPlayerIds and trigger OnDisconnectedInputNeeded auto-fill in
            // CanAdvanceTick), the chain genuinely stalls — only ExecuteTickWithPrediction
            // advances CurrentTick.
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = int.MaxValue,
                MaxRollbackTicks = 50,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(2);
                var guest = harness.AddGuest();
                harness.StartPlaying();

                const int baselineTick = 20;
                harness.AdvanceAllToTick(baselineTick);

                int verifiedBeforeStall = harness.Host.Engine.LastVerifiedTick;
                int currentBeforeStall = harness.Host.CurrentTick;
                Assert.GreaterOrEqual(verifiedBeforeStall, baselineTick - 5,
                    $"Pre-stall sanity: _lastVerifiedTick ({verifiedBeforeStall}) should be near baseline ({baselineTick})");

                // Freeze verified chain via stall sim only — NO DisconnectPeer.
                // Transport disconnect triggers _disconnectedPlayerIds population, which fires
                // the OnDisconnectedInputNeeded auto-fill in CanAdvanceTick and advances the chain.
                int targetCurrentTick = currentBeforeStall + 60;
                harness.AdvanceWithFrozenVerifiedTick(targetCurrentTick, guest.LocalPlayerId);

                // CurrentTick must have advanced to target (via prediction).
                Assert.GreaterOrEqual(harness.Host.CurrentTick, targetCurrentTick,
                    $"CurrentTick must reach target ({targetCurrentTick}) — got {harness.Host.CurrentTick}");

                // _lastVerifiedTick may advance up to InputDelay ticks beyond baseline due to
                // pre-buffered commands from the input-delay window (Start() line 604-613 pre-fills
                // ticks 0..InputDelay-1, and each baseline tick advance adds a command at +InputDelay).
                // After exhausting that buffer, the chain MUST stall (no fresh guest input).
                int verifiedAfterStall = harness.Host.Engine.LastVerifiedTick;
                int inputDelay = simConfig.InputDelayTicks;
                Assert.LessOrEqual(verifiedAfterStall, verifiedBeforeStall + inputDelay + 1,
                    $"_lastVerifiedTick can only advance by at most InputDelay ({inputDelay}) ticks beyond " +
                    $"baseline due to pre-buffered commands. Got {verifiedAfterStall - verifiedBeforeStall} ticks. " +
                    "Further advancement indicates a chain-advance path leak (DisconnectPeer or watchdog).");

                // Lag must exceed SnapshotCapacity (52 for MaxRollbackTicks=50) — ring wrap territory.
                // This is the key invariant: the stall actually enters ring wrap range.
                int lag = harness.Host.CurrentTick - verifiedAfterStall;
                int snapshotCapacity = simConfig.MaxRollbackTicks + 2;
                Assert.Greater(lag, snapshotCapacity,
                    $"Stalled lag ({lag}) must exceed SnapshotCapacity ({snapshotCapacity}) to enter ring wrap.");
            }
            finally
            {
                harness.Reset();
            }
        }

        [Test]
        public void EcsSimMaxRollbackTicks_IsIndependentOf_SimConfigMaxRollbackTicks()
        {
            // ECS snapshot ring buffer's maxRollbackTicks parameter is accepted at EcsSimulation
            // construction time and stored independently. Allows tests to use a large value
            // (e.g., 150) for SD-Client resim scenarios while SimulationConfig keeps the
            // production-realistic MaxRollbackTicks (50).
            //
            // Confirms:
            //   - EcsSimulation accepts maxRollbackTicks parameter
            //   - The chosen value is honored (snapshot at tick T retrievable when CurrentTick - T
            //     is within maxRollbackTicks)

            const int simMaxRollback = 50;       // ring wrap at lag 53
            const int ecsMaxRollback = 150;      // ECS snapshot retention covers up to 150-tick stall

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: ecsMaxRollback, deltaTimeMs: 50);
            sim.Initialize();

            // Save snapshot at frame.Tick=0, advance frame.Tick beyond simMaxRollback but within
            // ecsMaxRollback, then query nearest snapshot. Should find tick 0.
            sim.SaveSnapshot();
            for (int i = 0; i < simMaxRollback + 20; i++)  // 70 ticks of advancement
                sim.Tick(new System.Collections.Generic.List<ICommand>());

            int nearestSnapshot = sim.GetNearestSnapshotTick(0);
            Assert.AreEqual(0, nearestSnapshot,
                $"EcsSim with maxRollbackTicks={ecsMaxRollback} must retain snapshot at tick 0 " +
                $"after advancing {simMaxRollback + 20} ticks (independent of any SimulationConfig.MaxRollbackTicks={simMaxRollback}).");
        }

        [Test]
        public void EcsSimMaxRollbackTicks_RetentionLimit_DiscardsOldestSnapshot()
        {
            // Negative case: confirm that with insufficient maxRollbackTicks, old snapshots are
            // discarded. Validates the assumption that SD-Client batch resim scenarios spanning
            // long stalls need a deliberately large ecsMaxRollback to keep the resim feasible.

            const int ecsMaxRollback = 8;  // default-like small value

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: ecsMaxRollback, deltaTimeMs: 50);
            sim.Initialize();

            sim.SaveSnapshot();  // tick 0
            for (int i = 0; i < ecsMaxRollback + 5; i++)
                sim.Tick(new System.Collections.Generic.List<ICommand>());

            int nearestSnapshot = sim.GetNearestSnapshotTick(0);
            Assert.AreNotEqual(0, nearestSnapshot,
                $"EcsSim with maxRollbackTicks={ecsMaxRollback} must NOT retain tick 0 snapshot " +
                $"after advancing {ecsMaxRollback + 5} ticks — confirms (b-4)/(b-6) needs larger ecsMaxRollback.");
        }
    }
}
