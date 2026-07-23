using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Quorum-miss watchdog presumed-drop self-release guard.
    ///   (1) silent-but-alive peer: the watchdog stays latched (no echo self-release),
    ///       fp metric stays clean, the player stays in _disconnectedPlayerIds.
    ///   (2) while latched the host is NOT throttled — the vote-exclusion
    ///       guard finally protects the watchdog path.
    ///   (3) burst-convergence resume: recovery fires exactly once via the network
    ///       arrival at the unsealed frontier slot.
    ///   (3') frontier-ahead landing (resync-snap shape): recovery fires under the
    ///       frontier condition with the [stallTick, Tick) gap filled —
    ///       structurally red under the old exact-stallTick match.
    ///   (5a) non-roster senders get no timing vote (HandleFrameAdvantage roster guard).
    ///   (5b) presumed-drop timeout leave removes the player AND cuts the still-alive
    ///       transport (no zombie sends, no double processing).
    /// </summary>
    [TestFixture]
    internal class PresumedDropWatchdogTests
    {
        private static readonly FieldInfo _remoteTicksField = typeof(KlothoEngine)
            .GetField("_remoteTicks", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _disconnectedPlayerIdsField = typeof(KlothoEngine)
            .GetField("_disconnectedPlayerIds", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _handleFrameAdvantageMethod = typeof(KlothoEngine)
            .GetMethod("HandleFrameAdvantage", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("PresumedDropWatchdogTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        private static SimulationConfig WatchdogSimConfig() => new SimulationConfig
        {
            TickIntervalMs = 50,
            QuorumMissDropTicks = 10,
        };

        private static bool EngineSetContains(FieldInfo field, KlothoEngine engine, int playerId)
        {
            var set = (System.Collections.Generic.HashSet<int>)field.GetValue(engine);
            return set.Contains(playerId);
        }

        private static bool RemoteTicksContains(KlothoEngine engine, int playerId)
        {
            var dict = (System.Collections.IDictionary)_remoteTicksField.GetValue(engine);
            return dict.Contains(playerId);
        }

        // Drives ONLY the guest (service + engine) so it converges toward the host's frontier
        // while the host stays put — the harness equivalent of the production dt-backlog burst
        // (the harness fixed-dt loop never converges on its own: the engine has no
        // fast-forward, and while latched the host does not slow down for the silent peer).
        private static void ConvergeGuestSolo(TestPeer guest, TestPeer host, int untilGap)
        {
            int safety = 0;
            while (guest.CurrentTick < host.CurrentTick - untilGap)
            {
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));
                guest.NetworkService.Update();
                guest.Engine.Update(0.05f); // dt == TickIntervalMs — one tick per iteration
                if (++safety > 2000)
                    Assert.Fail($"ConvergeGuestSolo safety limit reached. guest={guest.CurrentTick}, host={host.CurrentTick}");
            }
        }

        // ── (1) Silent-but-alive peer: latch holds ───────────────────────────

        [Test]
        public void SilentAlivePeer_WatchdogStaysLatched_NoSelfRelease()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                // Isolate the watchdog from timesync (test 2 covers the interaction).
                harness.Host.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(50);

                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                // Silence the guest WITHOUT a transport disconnect — quorum-miss arms at lag>=10.
                harness.AdvanceWithStalledPeer(75, guest.LocalPlayerId);

                Assert.AreEqual(fpBefore, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "Activation must not self-release via its own fill echo (fp pollution).");
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "Presumed-drop entry must stay active for a silent-but-alive peer.");
                Assert.IsTrue(EngineSetContains(_disconnectedPlayerIdsField, harness.Host.Engine, guest.LocalPlayerId),
                    "Player must stay in _disconnectedPlayerIds while presumed-dropped.");

                // +30 ticks: still latched, fp still clean (pre-fix: sawtooth re-arm bumped fp per cycle).
                harness.AdvanceWithStalledPeer(105, guest.LocalPlayerId);
                Assert.AreEqual(fpBefore, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "No fp increments while the peer stays silent.");
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "Latch must persist while the peer stays silent.");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── (2) Vote-exclusion guard effective: host not throttled while latched ─────────

        [Test]
        public void SilentAlivePeer_HostNotThrottled_WhileLatched()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying(); // timesync stays ON — this test asserts the interaction
                harness.AdvanceAllToTick(50);

                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;
                harness.AdvanceWithStalledPeer(75, guest.LocalPlayerId);

                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "Presumed-drop entry must stay active (latch is the premise of this test).");
                Assert.AreEqual(fpBefore, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "No fp pollution while latched.");
                // The latched player sits in _disconnectedPlayerIds → excluded from the
                // advantage vote → its frozen tick cannot throttle the host.
                // Pre-fix: the self-release evicted it from the set and the frozen entry voted
                // (1 tick / 10 tick-times slowdown until staleness).
                Assert.AreEqual(0, harness.Host.Engine.TimeSyncService.RecommendWaitFrames(requireIdleInput: true),
                    "Host must not be throttled by the presumed-dropped peer's frozen tick.");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── (3) Burst-convergence resume: recovery fires exactly once ────────

        [Test]
        public void SilentPeerResumes_BurstConvergence_RecoveryFiresExactlyOnce()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();
                guest.Engine.DisableTimeSync(); // determinism — convergence pace must not self-throttle
                harness.AdvanceAllToTick(50);

                harness.AdvanceWithStalledPeer(75, guest.LocalPlayerId);
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(), "latch precondition");

                // Converge to within InputDelay of the host frontier. The queued send sweep is
                // kept: on resume its first cmd at/beyond the dynamic stall tick fires recovery
                // (older sweep ticks are sealed by the watchdog fills and dropped at the relay
                // seal guard — expected RelaySealDropCount growth, no gate here).
                ConvergeGuestSolo(guest, harness.Host, guest.Engine.InputDelay);

                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;
                harness.AdvanceAllToTick(harness.Host.CurrentTick + 20);

                Assert.AreEqual(fpBefore + 1, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "Recovery must fire exactly once on the network arrival at the frontier.");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Pool entry must be released on recovery.");
                Assert.IsFalse(EngineSetContains(_disconnectedPlayerIdsField, harness.Host.Engine, guest.LocalPlayerId),
                    "Player must leave _disconnectedPlayerIds on recovery.");

                // > 2x threshold without re-arm: the recovered peer feeds the chain for real.
                harness.AdvanceAllToTick(harness.Host.CurrentTick + 25);
                Assert.AreEqual(fpBefore + 1, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "No repeated recovery / re-arm cycle after resume.");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Watchdog must not re-arm while the peer keeps sending.");
                harness.AssertStateHashConsistent();
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── (3') Frontier-ahead landing (resync-snap shape) ─────────

        [Test]
        public void SnapResume_FrontierAheadLanding_RecoversWithGapFill()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();
                guest.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(50);

                harness.AdvanceWithStalledPeer(75, guest.LocalPlayerId);
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(), "latch precondition");

                // Converge PAST the frontier, then discard the queued sweep: the backlog
                // contains the exact-stall-tick cmd which would mask the ahead-landing shape.
                // What remains models a resync snap — the first VISIBLE send lands at
                // guest.Current + InputDelay, strictly ahead of the dynamic stall tick.
                ConvergeGuestSolo(guest, harness.Host, 0);
                harness.Host.Transport.DrainIncomingMessages();

                int stallBefore = harness.Host.Engine.LastVerifiedTick + 1;
                int aheadTick = guest.CurrentTick + guest.Engine.InputDelay;
                Assert.Greater(aheadTick, stallBefore,
                    "Scenario precondition: the first visible send must land ahead of the stall tick.");
                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                harness.AdvanceAllToTick(harness.Host.CurrentTick + 20);

                // Old exact-stallTick match: structurally red here — the ahead landing never
                // equals the stall tick and both advance in parallel (permanent latch).
                Assert.AreEqual(fpBefore + 1, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "Frontier-ahead real input must fire recovery exactly once (D2(b')).");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Pool entry must be released on frontier-ahead recovery.");
                for (int t = stallBefore; t < aheadTick; t++)
                {
                    Assert.IsTrue(harness.Host.Engine.HasCommand(t, guest.LocalPlayerId),
                        $"Gap fill must cover [stallTick, Tick): missing command at tick {t}.");
                }

                // No chain stall / watchdog re-arm after release (gap was closed before release).
                harness.AdvanceAllToTick(harness.Host.CurrentTick + 25);
                Assert.AreEqual(fpBefore + 1, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "No repeated recovery after the gap-filled release.");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Watchdog must not re-arm after the gap-filled release.");
                harness.AssertStateHashConsistent();
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── (5a) Non-roster senders get no timing vote ───────────────────────

        [Test]
        public void NonRosterFrameAdvantage_IsIgnored()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.AdvanceAllToTick(20);

                var hostEngine = harness.Host.Engine;

                // Non-roster sender (e.g. a timeout-removed player whose transport survived):
                // must not (re-)insert a _remoteTicks entry.
                _handleFrameAdvantageMethod.Invoke(hostEngine, new object[] { 999, harness.Host.CurrentTick, 0 });
                Assert.IsFalse(RemoteTicksContains(hostEngine, 999),
                    "Non-roster sender must not get a timing vote (roster guard).");

                // Positive control: a roster remote peer still gets its entry.
                _handleFrameAdvantageMethod.Invoke(hostEngine, new object[] { guest.LocalPlayerId, harness.Host.CurrentTick, 0 });
                Assert.IsTrue(RemoteTicksContains(hostEngine, guest.LocalPlayerId),
                    "Roster remote peer must keep its timing vote (no regression).");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── (5b) Timeout leave: player removed AND transport cut ─────────────

        [Test]
        public void PresumedDropTimeout_RemovesPlayer_AndCutsTransport()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                // Short wall-clock timeout so the test reaches the leave branch.
                ((SessionConfig)harness.Host.Engine.SessionConfig).ReconnectTimeoutMs = 300;
                harness.Host.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(30);

                harness.AdvanceWithStalledPeer(55, guest.LocalPlayerId);
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "latch precondition (pre-fix the self-release made this timeout unreachable)");

                int playersBefore = harness.Host.NetworkService.PlayerCount;
                int fpAtLatch = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                System.Threading.Thread.Sleep(400);
                harness.AdvanceWithStalledPeer(harness.Host.CurrentTick + 5, guest.LocalPlayerId);

                Assert.AreEqual(playersBefore - 1, harness.Host.NetworkService.PlayerCount,
                    "Timeout must remove the silent player from the match.");
                Assert.IsFalse(guest.Transport.IsConnected,
                    "Timeout leave must cut the still-alive transport — " +
                    "otherwise the removed player keeps sending into timing/input paths.");
                // The cut's disconnect event lands AFTER the leave (player gone, pool entry
                // reset, mappings cleared) — the empty-room phase reset must not drop a
                // Playing host to Lobby (found red on first run: the harness stops driving
                // a non-Playing host, so the session silently froze).
                Assert.AreEqual(SessionPhase.Playing, harness.Host.Phase,
                    "Host must stay Playing after the last peer's timeout-leave cut.");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Pool entry must be reset by the timeout leave.");
                Assert.AreEqual(fpAtLatch, harness.Host.NetworkService.PresumedDropFalsePositiveCount,
                    "A timeout leave is not a false-positive recovery.");
                Assert.IsFalse(RemoteTicksContains(harness.Host.Engine, guest.LocalPlayerId),
                    "Departed player's timing entry must stay discarded.");
                Assert.IsFalse(EngineSetContains(_disconnectedPlayerIdsField, harness.Host.Engine, guest.LocalPlayerId),
                    "NotifyPlayerLeft must clear the disconnected-set entry.");

                // No double processing from the re-entrant HandlePeerDisconnected (cut event):
                // the player is already gone, so it only cleans peer mappings.
                harness.AdvanceWithStalledPeer(harness.Host.CurrentTick + 10, guest.LocalPlayerId);
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Cut event must not re-add a disconnected entry for the removed player.");
                Assert.AreEqual(playersBefore - 1, harness.Host.NetworkService.PlayerCount,
                    "Cut event must not double-process the leave.");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── Adaptive threshold ────────────────────────────────
        //
        // The watchdog threshold is now max(QuorumMissDropTicks floor, min(InputDelay +
        // RecommendedExtraDelay + margin, MaxRollbackTicks)). With extraDelay raised, normal
        // lag that the OLD fixed floor (QuorumMissDropTicks) would have presumed-dropped must
        // NOT arm while it sits below the adaptive threshold — that was the high-RTT false
        // positive (delayed-but-not-dropped player empty-sealed into a permanent desync).
        // Genuine drops (lag beyond the adaptive threshold) must still arm.
        [Test]
        public void AdaptiveThreshold_HighExtraDelay_SuppressesFixedThresholdFalsePositive()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(50);

                // Raise extraDelay so the adaptive threshold = InputDelay(4) + 20 + margin(8) = 32,
                // well above the fixed floor (QuorumMissDropTicks = 10) and below MaxRollbackTicks (50).
                harness.Host.Engine.ApplyExtraDelay(20, ExtraDelaySource.DynamicPush);
                Assert.AreEqual(20, harness.Host.Engine.RecommendedExtraDelay,
                    "Precondition: extraDelay must hold at 20 — a reactive push would invalidate the band.");

                // Lag ~20: above the old fixed floor (10) but below the adaptive threshold (32).
                // Pre-fix this armed a presumed-drop on a merely-delayed peer; post-fix it must not.
                harness.AdvanceWithStalledPeer(70, guest.LocalPlayerId);
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Adaptive threshold must NOT arm in the old fixed-threshold band (false-positive suppression).");

                // Lag beyond the adaptive threshold (~40 > 32): the watchdog must still arm —
                // genuine-drop detection (transport-timeout-anticipating) value is preserved.
                harness.AdvanceWithStalledPeer(90, guest.LocalPlayerId);
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "Watchdog must still arm once lag exceeds the adaptive threshold (true-drop value preserved).");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── Window collapse → disable ───────────────────────
        //
        // When extraDelay escalates so the adaptive threshold (InputDelay + extraDelay + margin)
        // reaches/exceeds MaxRollbackTicks, the useful firing window [threshold, MaxRollbackTicks)
        // is empty: the host freezes at that lag anyway, so the threshold (clamped to the ceiling)
        // can only ever fire AT the freeze point. Pre-fix it clamped-and-fired there, mis-firing on
        // the resync/rollback churn that drives lag to the ceiling (observed fp #2). Post-fix the
        // collapsed window disables the watchdog for that frame; a genuine drop falls back to the
        // transport DisconnectTimeout. WatchdogSimConfig leaves MaxRollbackTicks at its default (50).
        [Test]
        public void WindowCollapse_AdaptiveThresholdAtCeiling_DisablesWatchdog()
        {
            // Effective extraDelay now clamps to MaxRollbackTicks/2, so the
            // window-collapse case (adaptive threshold >= MaxRollbackTicks) is only reachable for a
            // small MaxRollbackTicks. With MaxRollbackTicks=24 the cap is 12 and adaptive =
            // InputDelay(4) + 12 + margin(12) = 28 >= 24 → the useful firing window [28,24) is empty.
            const int MaxRollbackTicks = 24;
            var cfg = WatchdogSimConfig();
            cfg.MaxRollbackTicks = MaxRollbackTicks;
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(cfg);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(50);

                // Push past the clamp; effective holds at the ceiling MaxRollbackTicks/2 = 12, which
                // still collapses the adaptive threshold (4 + 12 + 12 = 28 >= 24) against the ceiling.
                harness.Host.Engine.ApplyExtraDelay(40, ExtraDelaySource.DynamicPush);
                Assert.AreEqual(MaxRollbackTicks / 2, harness.Host.Engine.RecommendedExtraDelay,
                    "Precondition: effective extraDelay clamps to MaxRollbackTicks/2 (T-F5).");

                // Drive lag well past MaxRollbackTicks. Pre-fix the threshold clamped to 50 and armed
                // here (fp #2); post-fix the collapsed window must keep the watchdog disabled.
                harness.AdvanceWithStalledPeer(115, guest.LocalPlayerId);
                Assert.GreaterOrEqual(harness.Host.CurrentTick - harness.Host.Engine.LastVerifiedTick, MaxRollbackTicks,
                    "Scenario precondition: lag must reach the clamped threshold so the no-arm result is "
                    + "attributable to window-collapse disable, not an unmet threshold.");
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Window collapse (adaptive >= MaxRollbackTicks) must disable the watchdog — no false-positive arm.");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── Margin absorbs the resync-rollback transient (fp #1 guard) ──
        //
        // At its RTT baseline the host's extraDelay is low (~7). A FullStateResync rollback to the
        // last matched sync anchor (observed: desync@580 → rollback to 560) stalls the chain to a
        // lag equal to the rollback distance (~20) while extraDelay is still low. With margin 8 the
        // threshold sat AT that lag (floor 20 == lag 20), so the merely-rolling-back host presumed
        // a drop and empty-sealed real input — fp #1, reproduced across runs (11-25, 13-56). margin
        // 12 lifts the adaptive threshold (InputDelay 4 + extraDelay 7 + 12 = 23) above the
        // transient lag, so the rollback stall no longer arms; a genuine drop (lag past 23) still does.
        [Test]
        public void Margin_LowExtraDelay_AbsorbsResyncRollbackTransientLag()
        {
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(WatchdogSimConfig());
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();
                harness.AdvanceAllToTick(50);

                // extraDelay at the RTT baseline (7) → adaptive threshold = InputDelay(4) + 7 + margin(12) = 23.
                harness.Host.Engine.ApplyExtraDelay(7, ExtraDelaySource.DynamicPush);
                Assert.AreEqual(7, harness.Host.Engine.RecommendedExtraDelay,
                    "Precondition: extraDelay must hold at 7 (RTT baseline) — a reactive push would shift the band.");

                // Lag ~20: the resync-rollback transient that armed fp #1 under margin 8 (threshold 20).
                // Under margin 12 (threshold 23) it must NOT arm.
                harness.AdvanceWithStalledPeer(70, guest.LocalPlayerId);
                Assert.AreEqual(0, harness.GetDisconnectedPlayerCount(),
                    "Margin 12 must absorb the ~20-tick resync-rollback transient at low extraDelay (fp #1 guard).");

                // Lag past the adaptive threshold (~30 > 23): a genuine drop must still arm.
                harness.AdvanceWithStalledPeer(80, guest.LocalPlayerId);
                Assert.AreEqual(1, harness.GetDisconnectedPlayerCount(),
                    "Watchdog must still arm once lag exceeds the adaptive threshold (true-drop value preserved).");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
