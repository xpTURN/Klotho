using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Immediate-pattern ring wrap under-fire test + 8-tick window invariant sweep.
    ///
    /// Verifies that when the verified chain stalls and CurrentTick advances past
    /// SnapshotCapacity, ExecuteTickWithPrediction's ClearTick wipes the slot for the
    /// ring-wrapped older tick — silently dropping any pending Synced event there
    /// (under-fire).
    ///
    /// The 8-tick window is invariant against MaxRollbackTicks (sweep cases 10/50/100):
    ///   first under-fire lag = SnapshotCapacity + 1 = MaxRollbackTicks + 3
    ///   cap activation lag   = MaxRollbackTicks + CLEANUP_MARGIN_TICKS + 1 = MaxRollbackTicks + 11
    ///   window               = 8 ticks (constant)
    /// </summary>
    [TestFixture]
    public class EventBufferRingWrapTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_501;
            public override EventMode Mode => EventMode.Synced;
        }

        // Mirrors KlothoEngine.CLEANUP_MARGIN_TICKS (constant, not configurable).
        private const int CleanupMarginTicks = 10;

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineEventBufferField = typeof(KlothoEngine)
            .GetField("_eventBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static EventBuffer ReadEventBuffer(KlothoEngine engine)
            => (EventBuffer)_engineEventBufferField.GetValue(engine);

        private static int SnapshotCapacityOf(SimulationConfig cfg)
            => cfg.MaxRollbackTicks + 2;

        private static void WireEventRaiserFromEngine(TestPeer peer)
        {
            var collector = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(peer.Engine);
            peer.Simulation.EventRaiser = collector;
        }

        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("EventBufferRingWrapTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [TestCase(10)]
        [TestCase(50)]
        [TestCase(100)]
        public void ImmediatePattern_RingWrap_SyncedUnderfire_InvariantWindow(int maxRollbackTicks)
        {
            // 8-tick window invariant — compute first to lock in the formula. This holds
            // regardless of whether the test reaches ring wrap, so verify up front.
            int capacity = maxRollbackTicks + 2;
            int firstUnderfireLag = capacity + 1;                        // = MaxRollbackTicks + 3
            int capActivationLag = maxRollbackTicks + CleanupMarginTicks + 1;  // = MaxRollbackTicks + 11
            Assert.AreEqual(8, capActivationLag - firstUnderfireLag,
                $"Under-fire window must be 8 ticks regardless of MaxRollbackTicks. " +
                $"Got firstUnderfireLag={firstUnderfireLag}, capActivationLag={capActivationLag}.");

            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = int.MaxValue,  // disable watchdog
                MaxRollbackTicks = maxRollbackTicks,
                // SyncCheckInterval must be in [1, MaxRollbackTicks]. Default (30) breaks
                // for maxRollbackTicks=10 sweep case — clamp to fit the parameter range.
                SyncCheckInterval = System.Math.Min(maxRollbackTicks, 30),
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(2);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                WireEventRaiserFromEngine(harness.Host);

                int dispatchedCount = 0;
                harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
                {
                    if (evt is TestSyncedEvent) dispatchedCount++;
                };

                // (1) Verified-phase Synced event — fires once via DispatchTickEvents(Verified).
                const int firstRaiseTick = 10;
                harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
                {
                    if (tick == firstRaiseTick + 1 && raiser != null)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                };
                harness.AdvanceAllToTick(firstRaiseTick + 5);
                Assert.AreEqual(1, dispatchedCount,
                    "Initial pass: verified Synced fires exactly once.");

                int verifiedBeforeStall = harness.Host.Engine.LastVerifiedTick;
                int currentBeforeStall = harness.Host.CurrentTick;

                // (2) Choose a predictedSyncedTick BEYOND the InputDelay pre-buffer window.
                // After stall begins, the chain will advance by up to InputDelay ticks consuming
                // pre-buffered commands. Pick a tick past that boundary so the Synced event
                // lands in genuine prediction state.
                int inputDelay = simConfig.InputDelayTicks;
                int predictedSyncedTick = currentBeforeStall + inputDelay + 1;

                // Re-arm hook to raise at predictedSyncedTick. TestSim's OnAfterTickRaise fires
                // with TestSim.CurrentTick AFTER its internal increment, so the engine-level
                // tick at BeginTick is (simTick - 1). Match on (predictedSyncedTick + 1).
                harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
                {
                    if (tick == predictedSyncedTick + 1 && raiser != null)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 2 });
                };

                // (3) Advance until ClearTick(predictedSyncedTick + capacity) executes — this
                // wipes the slot holding predictedSyncedTick's event (ring wrap erase).
                int targetCurrentTick = predictedSyncedTick + capacity + 2;
                harness.AdvanceWithFrozenVerifiedTick(targetCurrentTick, guest.LocalPlayerId);

                // (4) Direct buffer inspection — slot wiped by ring-wrapped ClearTick. GetEvents
                // returns the whole slot (no evt.Tick filter), so filter explicitly: any event
                // present at the slot must NOT be from predictedSyncedTick.
                var eventBuffer = ReadEventBuffer(harness.Host.Engine);
                var bufferedAtPredicted = eventBuffer.GetEvents(predictedSyncedTick);
                int realPredictedCount = 0;
                for (int i = 0; i < bufferedAtPredicted.Count; i++)
                    if (bufferedAtPredicted[i] is TestSyncedEvent && bufferedAtPredicted[i].Tick == predictedSyncedTick)
                        realPredictedCount++;
                Assert.AreEqual(0, realPredictedCount,
                    $"Under-fire (MaxRollbackTicks={maxRollbackTicks}): predicted Synced at " +
                    $"tick {predictedSyncedTick} must have been wiped by ExecuteTickWithPrediction's " +
                    $"ClearTick({predictedSyncedTick + capacity}) ring-wrap.");

                // (5) Total dispatch count: only the initial verified-phase Synced. The under-fired
                // event at predictedSyncedTick never fires (silent loss confirmed).
                Assert.AreEqual(1, dispatchedCount,
                    $"Under-fire (MaxRollbackTicks={maxRollbackTicks}): only initial Synced may " +
                    $"have dispatched. Under-fired predicted-tick Synced is silently lost.");

                // (6) Lag must exceed SnapshotCapacity to confirm ring wrap territory was reached.
                int verifiedAfterStall = harness.Host.Engine.LastVerifiedTick;
                int lag = harness.Host.CurrentTick - verifiedAfterStall;
                Assert.Greater(lag, capacity,
                    $"Stalled lag ({lag}) must exceed SnapshotCapacity ({capacity}) to enter ring wrap.");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
