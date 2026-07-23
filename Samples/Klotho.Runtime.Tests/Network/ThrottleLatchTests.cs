using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Throttle permanent-latch fix (one-shot wait budget + frozen-means tracking).
    ///   (a) liveness: with the remote fully starved (production shape) the host keeps
    ///       forcing ≥1 tick per budget cycle instead of freezing forever
    ///   (b) unit semantics: at dt = interval/2 the wait budget is consumed in tick-time
    ///       units (tickSkipped), not update counts — pins the fps-invariant unit choice
    ///   (c) NotifyPlayerLeft discards the departed player's _remoteTicks entry
    /// QuorumMissDropTicks is disabled (int.MaxValue) so the quorum-miss watchdog cannot
    /// move the starved peer into _disconnectedPlayerIds — that would route the scenario
    /// through the exclusion guard instead of exercising the latch.
    /// </summary>
    [TestFixture]
    public class ThrottleLatchTests
    {
        private static readonly FieldInfo _remoteTicksField = typeof(KlothoEngine)
            .GetField("_remoteTicks", BindingFlags.NonPublic | BindingFlags.Instance);

        private LogCapture _log;
        private KlothoTestHarness _harness;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _log = new LogCapture();
            _harness = new KlothoTestHarness(_log)
                .WithSimulationConfig(new SimulationConfig { QuorumMissDropTicks = int.MaxValue });
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();
            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // Drives the host at 2 tick-times and the guest at 1 tick-time per iteration until
        // the host's throttle engages. subUpdatesPerTickTime = interval / dt.
        private bool EngageThrottle(float dt, int subUpdatesPerTickTime, int maxIterations = 100)
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];

            for (int i = 0; i < maxIterations; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));

                host.NetworkService.Update();
                guest.NetworkService.Update();

                for (int s = 0; s < subUpdatesPerTickTime * 2; s++)
                    host.Engine.Update(dt);
                for (int s = 0; s < subUpdatesPerTickTime; s++)
                    guest.Engine.Update(dt);

                if (host.Engine.TimeSyncService.RecommendWaitFrames(requireIdleInput: true) > 0)
                    return true;
            }
            return false;
        }

        // ── (a) Liveness under full piggyback starvation ──────────────────────

        [Test]
        public void Throttle_ForcesProgress_WhenRemoteFullyStarved()
        {
            var host = _harness.Host;
            const float dt = 0.025f; // = TickIntervalMs(25) — 1 Update = 1 tick-time

            Assert.IsTrue(EngageThrottle(dt, subUpdatesPerTickTime: 1),
                "Setup precondition — throttle must engage from the skew phase");

            // Starve the guest completely (no Update, no sends) — production shape: the
            // host's view of the remote freezes, so the recommendation stays > 0 and only
            // the one-shot budget can force progress. Iteration cap: with the
            // guest frozen, the host's verified chain is pinned, so prediction allows only
            // ~MaxRollbackTicks(50) of runway — 240 updates ≈ ≤24 forced ticks stays safely
            // inside it (an uncapped run would hit the prediction bound and fail for a
            // non-throttle reason).
            int sinceProgress = 0;
            for (int i = 0; i < 240; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                host.NetworkService.Update();

                int before = host.CurrentTick;
                host.Engine.Update(dt);

                if (host.CurrentTick > before)
                    sinceProgress = 0;
                else
                    sinceProgress++;

                // Liveness guarantee: ≥1 tick per 10 tick-times (budget ≤ 9 + 1 forced tick).
                // Harness dt == interval, so 1 update = 1 tick-time; 20 = 2x margin.
                Assert.LessOrEqual(sinceProgress, 20,
                    $"Liveness violated at iteration {i}: host frozen for {sinceProgress} updates " +
                    $"(tick={host.CurrentTick}) — the throttle latch is back");
            }
        }

        // ── (b) Budget unit semantics at sub-interval dt ──────────────────────

        [Test]
        public void Throttle_WaitsInTickTimeUnits_AtSubIntervalDt()
        {
            var host = _harness.Host;
            const float dt = 0.0125f; // = interval / 2 — 2 updates per tick-time

            Assert.IsTrue(EngageThrottle(dt, subUpdatesPerTickTime: 2),
                "Setup precondition — throttle must engage from the skew phase");

            // Starve the guest; the recommendation grows toward MAX_FRAME_ADVANTAGE(9) as
            // the gap widens. Budget is consumed per SKIPPED TICK, so a W-frame wait spans
            // ~2W sub-updates here. The buggy per-update decrement would consume the budget
            // in W sub-updates (= W/2 tick-times): its longest stall is ≤ 10 sub-updates,
            // while the correct unit reaches 2*(W+1) = 20. Cap 300 sub-updates ≈ ≤15 forced
            // ticks — inside the prediction runway (~47 ticks, see liveness test).
            int sinceProgress = 0;
            int stallMax = 0;
            for (int i = 0; i < 300; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                host.NetworkService.Update();

                int before = host.CurrentTick;
                host.Engine.Update(dt);

                if (host.CurrentTick > before)
                {
                    if (sinceProgress > stallMax) stallMax = sinceProgress;
                    sinceProgress = 0;
                }
                else
                {
                    sinceProgress++;
                }

                // Liveness at sub-interval dt: 10 tick-times = 20 sub-updates (+4 margin).
                Assert.LessOrEqual(sinceProgress, 24,
                    $"Liveness violated at sub-update {i} (tick={host.CurrentTick})");
            }

            Assert.GreaterOrEqual(stallMax, 12,
                $"Longest stall was {stallMax} sub-updates — waits are being consumed per " +
                "UPDATE instead of per skipped tick (fps-dependent under-wait)");
            Assert.LessOrEqual(stallMax, 24,
                $"Longest stall was {stallMax} sub-updates — exceeds the 10 tick-time liveness bound");
        }

        // ── (c) NotifyPlayerLeft discards the timing vote ─────────────────────

        [Test]
        public void NotifyPlayerLeft_RemovesRemoteTickEntry()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];

            // Let one guest command arrive so the host holds a timing entry for it.
            guest.NetworkService.SendCommand(
                new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));
            _harness.PumpMessages();

            var remoteTicks = (System.Collections.IDictionary)_remoteTicksField.GetValue(host.Engine);
            Assert.IsTrue(remoteTicks.Contains(guest.LocalPlayerId),
                "Setup precondition — host must hold a _remoteTicks entry for the guest");

            host.Engine.NotifyPlayerLeft(guest.LocalPlayerId);

            Assert.IsFalse(remoteTicks.Contains(guest.LocalPlayerId),
                "A departed player's timing vote must be discarded with them");
        }
    }
}
