using System;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// The full recovery ladder traversed end-to-end over the
    /// in-memory transport:
    ///
    ///   hash gate (detect) -> rung 1 (rollback to anchor) -> rung 2 (full-state resync)
    ///   -> rung 3 (corrective reset, host budget) -> rung 4 (MatchAbort broadcast).
    ///
    /// Divergence injection: the guest's TestSimulation.StateHash is flipped after a clean
    /// baseline (stateless mode — every exchanged hash mismatches from then on, and a full-state
    /// restore cannot repair it, which is exactly the "divergence in the state representation"
    /// scenario rungs 3-4 exist for).
    ///
    /// Wall-clock decoupling: the host's corrective-reset cooldown stamp is zeroed each pump via
    /// reflection — editmode runs advance ticks far faster than real time, so the 5s cooldown
    /// would otherwise dominate the test. Attempt decay stays inert for the same reason
    /// (the quiet period — max(2x cooldown, ResyncMaxRetries x RESYNC_TIMEOUT_MS + cooldown)
    /// — is never reached), preserving budget accumulation.
    ///
    /// Also pins the matched-compare revival on the way: the baseline phase asserts that matched hash
    /// comparisons actually fire and promote the anchor (the old engine never compared,
    /// so this test fails on a regression to the dormant gate).
    /// </summary>
    [TestFixture]
    internal class RecoveryLadderSweepTests
    {
        private static readonly FieldInfo LastResetMsField = typeof(KlothoEngine)
            .GetField("_lastCorrectiveResetMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastMatchedSyncTickField = typeof(KlothoEngine)
            .GetField("_lastMatchedSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo AttemptsField = typeof(KlothoEngine)
            .GetField("_correctiveResetAttempts", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Error));
            _logger = factory.CreateLogger("RecoveryLadderSweepTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void PersistentDivergence_ClimbsLadder_To_MatchAbort()
        {
            var simConfig = new SimulationConfig { TickIntervalMs = 50 };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();

                var host = harness.Host;

                // ── Baseline: clean hash exchange promotes the anchor ──
                int matchedCompares = 0;
                int desyncDetections = 0;
                host.Engine.OnDesyncDetected += (l, r) => desyncDetections++;

                var hostService = (KlothoNetworkService)host.NetworkService;
                hostService.OnSyncHashCompared += (tick, peerId, matched) => { if (matched) matchedCompares++; };

                int interval = simConfig.GetEffectiveSyncCheckInterval();
                harness.AdvanceAllToTick(interval * 3 + 5);

                Assert.Greater(matchedCompares, 0,
                    "Hash gate must be alive: matched comparisons fire during a clean baseline " +
                    "(regression to the previously dormant gate if 0)");
                Assert.Greater((int)LastMatchedSyncTickField.GetValue(host.Engine), 0,
                    "Matched comparisons must promote the rollback anchor (event-based promotion)");
                Assert.AreEqual(0, desyncDetections, "Clean baseline must not report desyncs");

                // ── Ladder observation hooks ──
                int maxAttemptsObserved = 0;                       // rung 3 performed (host budget)
                AbortReason? hostAborted = null;                   // rung 4 (host local)
                AbortReason? guestAborted = null;                  // rung 4 (broadcast received)
                int guestResyncRequests = 0;                       // rung 2 attempts

                host.Engine.OnMatchAborted += r => hostAborted = r;
                guest.Engine.OnMatchAborted += r => guestAborted = r;
                var guestSim = guest.Simulation;

                int requestsBefore = guest.Engine.ResyncRequestTotalCount;

                // ── Inject persistent divergence: guest hashes never match again, and a
                //     full-state restore cannot repair it (stateless TestSimulation). ──
                guestSim.StateHash = 0xBAD;

                // Drive until the host exhausts the corrective-reset budget and aborts.
                // Safety limit generous: detect needs a sync interval, escalation needs
                // DesyncThresholdForResync distinct check ticks per round.
                int safety = 0;
                while (hostAborted == null && ++safety < 20000)
                {
                    // Wall-clock decoupling: lift the corrective-reset cooldown so the budget
                    // can be consumed at editmode speed (attempts logic itself is untouched).
                    LastResetMsField.SetValue(host.Engine, 0L);
                    harness.StepOnce();

                    int attempts = (int)AttemptsField.GetValue(host.Engine);
                    if (attempts > maxAttemptsObserved) maxAttemptsObserved = attempts;
                }

                guestResyncRequests = guest.Engine.ResyncRequestTotalCount - requestsBefore;

                Assert.Greater(desyncDetections, 0, "Hash gate must detect the injected divergence");
                Assert.Greater(guestResyncRequests, 0,
                    "Rung 2: the guest must escalate persistent desync to full-state resync requests");
                Assert.GreaterOrEqual(maxAttemptsObserved, 1,
                    "Rung 3: guest failure reports must consume corrective-reset attempts on the host");
                Assert.AreEqual(AbortReason.StateDivergence, hostAborted,
                    "Rung 4: exhausting the corrective-reset budget must abort the match on the host");
                Assert.AreEqual(KlothoState.Aborted, host.Engine.State);

                // The abort must reach the guest via the MatchAbort broadcast.
                for (int i = 0; i < 50 && guestAborted == null; i++)
                    harness.PumpMessages(1);
                Assert.AreEqual(AbortReason.StateDivergence, guestAborted,
                    "Rung 4: guests must receive the MatchAbort broadcast and abort with the same reason");
                Assert.AreEqual(KlothoState.Aborted, guest.Engine.State);
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
