using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Regression: a desync-triggered FullStateResync must not leave the guest's verified
    /// chain permanently stalled at the resync tick.
    ///
    /// After a resync the guest clears its input buffer and seeds _lastVerifiedTick = resyncTick-1,
    /// so it needs resyncTick's other-player inputs to advance. Unlike reconnect, the
    /// desync-resync host serve path historically registered no input catchup and the guest left
    /// _lateJoinState unchanged — so the host's catchup batch (if any) was silent-dropped and the
    /// chain starved forever.
    ///
    /// The fix (KlothoNetworkService.FullStateResync.cs): the host registers a _lateJoinCatchups
    /// entry in HandleFullStateRequest, and the guest flips _lateJoinState=Active in
    /// HandleFullStateResponse (gated on _expectingResyncFullState) so the catchup is ingested.
    ///
    /// This MUST run on the real KlothoNetworkService + TestTransport (KlothoTestHarness) with >=2
    /// active players — a single-player / mock setup cannot observe the multi-player HasAllCommands
    /// starvation (mock extension would be a false GREEN).
    /// </summary>
    [TestFixture]
    public class FullStateResyncStarvationTests
    {
        private IKLogger _logger;

        // RollbackTooFar clamps to the window edge (ResolveRollbackTick) instead of escalating, so
        // it cannot trigger a resync with a stateless sim. Invoke the production resync entry point
        // directly — it sets _resyncState=Requested and broadcasts the FullStateRequest, faithfully
        // reproducing a desync-escalation-driven resync (the escalation itself is tested elsewhere).
        private static readonly MethodInfo _requestFullStateResyncMethod = typeof(KlothoEngine)
            .GetMethod("RequestFullStateResync", BindingFlags.NonPublic | BindingFlags.Instance);

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("FullStateResyncStarvationTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void DesyncResync_VerifiedChainResumesPastResyncTick()
        {
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                MaxRollbackTicks = 50,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                // 3 players (host + 2 guests): resyncTick's HasAllCommands genuinely requires
                // another player's input to be re-delivered via catchup. A single player would
                // hide the starvation.
                harness.CreateHost(4);
                var g0 = harness.AddGuest();
                harness.AddGuest();
                harness.StartPlaying();

                // Steady pacing — the brief resync pause must not let the timesync throttle slow
                // peers below the loop budget. Orthogonal to the catchup recovery path.
                foreach (var p in harness.AllPeers)
                    p.Engine.DisableTimeSync();

                // Advance well past MaxRollbackTicks so a deep rollback request escalates to resync.
                harness.AdvanceAllToTick(60);

                // Capture the resync tick the instant the guest completes the FullStateResync
                // (fires only on hash match — guaranteed here, both sides share the same state hash).
                int resyncTick = -1;
                g0.Engine.OnResyncCompleted += t => resyncTick = t;

                // Trigger a resync on g0: RequestFullStateResync sets _resyncState=Requested and
                // broadcasts a FullStateRequest. This drives the real host round-trip (request →
                // host serves snapshot + registers catchup → guest applies resync), exercising the
                // network catchup machinery the fix lives in.
                _requestFullStateResyncMethod.Invoke(g0.Engine, null);

                // Pump the round-trip until the resync completes.
                for (int i = 0; i < 200 && resyncTick < 0; i++)
                    harness.StepOnce();

                Assert.GreaterOrEqual(resyncTick, 0,
                    "FullStateResync must complete (hash match) — guest never reached resync completion.");

                // Post-resync the guest seeds _lastVerifiedTick = resyncTick-1 with an empty buffer.
                // Without the fix the chain is starved here forever.
                int verifiedAtResync = g0.Engine.LastVerifiedTick;
                Assert.LessOrEqual(verifiedAtResync, resyncTick,
                    $"Sanity: post-resync verified ({verifiedAtResync}) seeds at/below resyncTick ({resyncTick}).");

                // Drive forward: host catchup delivers resyncTick's inputs, the guest ingests them
                // (requires _lateJoinState=Active), and the verified chain resumes PAST resyncTick.
                for (int i = 0; i < 400 && g0.Engine.LastVerifiedTick <= resyncTick; i++)
                    harness.StepOnce();

                Assert.Greater(g0.Engine.LastVerifiedTick, resyncTick,
                    $"Verified chain must resume past the resync tick ({resyncTick}); stuck at " +
                    $"{g0.Engine.LastVerifiedTick} reproduces the starvation regression.");

                // Guest half of the fix: the catchup-ingest guard (HandleCatchupInputMessage)
                // requires _lateJoinState in {CatchingUp, Active}; the resync path must set Active.
                Assert.AreEqual("Active", harness.GetLateJoinState(g0).ToString(),
                    "Guest _lateJoinState must be Active after a desync-resync so the host's catchup batch is ingested (Q4).");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
