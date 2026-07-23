using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Late-join/reconnect guest timesync activation (self-throttle asymmetry).
    /// EnableTimeSync's sole caller is HandleGameStart (P2P), which late-join/reconnect guests
    /// never pass (fresh engine + full-state seed), so they ran with _timeSyncEnabled false
    /// forever and never self-throttled — an asymmetry vs start-join guests.
    /// The fix calls EnableTimeSync at the end of ApplyP2PLateJoinFullState, the P2P-only merge
    /// point of both seed paths.
    ///
    ///   (1) reconnect seed enables timesync — masking closed via DisableTimeSync before reconnect
    ///       (the harness reuses the engine, so the start-join flag would otherwise survive and
    ///       make the assertion vacuous — "detection-blind" class).
    ///   (2) late-join seed enables timesync — driven test-locally because AddLateJoinGuest's
    ///       message handshake routes the FullState through the Resync branch and never reaches
    ///       ApplyP2PLateJoinFullState (the seed call is production's KlothoSession entry).
    ///   (3) behavior: an activated guest, once genuinely ahead, ACTS on its own recommendation
    ///       (progresses slower than the remote) — the asymmetry-removal payoff. With the flag
    ///       off (pre-fix) the guest would ignore the recommendation and not slow down.
    /// </summary>
    [TestFixture]
    public class LateJoinReconnectTimeSyncActivationTests
    {
        private LogCapture _log;
        private KlothoTestHarness _harness;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
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
            _harness = new KlothoTestHarness(_log);
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

        // ── (1) Reconnect seed path enables timesync ─────────────────────────

        [Test]
        public void ReconnectGuest_SeedPath_EnablesTimeSync()
        {
            var guest = _harness.Guests[0];

            _harness.AdvanceAllToTick(30);
            _harness.DisconnectPeer(guest);
            _harness.AdvanceWithStalledPeer(60, guest.LocalPlayerId);

            // Close the harness masking: ReconnectPeer reuses the engine (Stop->Initialize, neither
            // resets the flag), so the start-join EnableTimeSync would otherwise survive and the
            // assertion below would pass vacuously. DisableTimeSync survives Stop/Initialize, so the
            // flag is false right up to SeedReconnectFullState — only the seed re-enables it.
            guest.Engine.DisableTimeSync();
            Assert.IsFalse(guest.Engine.IsTimeSyncEnabled, "Setup — masking closed before reconnect seed");

            _harness.ReconnectPeer(guest); // -> SeedReconnectFullState -> ApplyP2PLateJoinFullState -> EnableTimeSync

            Assert.IsTrue(guest.Engine.IsTimeSyncEnabled,
                "Reconnect seed path must enable timesync — pre-fix the flag stayed false " +
                "(ApplyP2PLateJoinFullState never called EnableTimeSync on the reused engine)");
        }

        // ── (2) Late-join seed path enables timesync ─────────────────────────

        [Test]
        public void LateJoinGuest_SeedPath_EnablesTimeSync()
        {
            _harness.AdvanceAllToTick(30);

            var lateJoin = _harness.AddLateJoinGuest();
            _harness.PumpMessages(); // join handshake -> RandomSeed; fresh engine, no HandleGameStart

            // Fresh engine + game already started => never passed HandleGameStart => flag false.
            Assert.IsFalse(lateJoin.Engine.IsTimeSyncEnabled,
                "Setup — a late-join guest is a fresh engine that never passed HandleGameStart");

            // Drive the production seed path (KlothoSession -> SeedLateJoinFullState). The message
            // handshake above does NOT reach it (Resync branch), so this is the only way to exercise
            // ApplyP2PLateJoinFullState in the harness.
            _harness.SeedLateJoinFullStateFromHost(lateJoin);

            Assert.IsTrue(lateJoin.Engine.IsTimeSyncEnabled,
                "Late-join seed path must enable timesync — the message handshake's Resync " +
                "branch never reaches ApplyP2PLateJoinFullState");
        }

        // ── (3) Activated guest acts on its own throttle recommendation ──────

        [Test]
        public void ReconnectedGuest_OnceAhead_SelfThrottles()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            const float dt = 0.025f; // = TickIntervalMs(25)

            _harness.AdvanceAllToTick(30);
            _harness.DisconnectPeer(guest);
            _harness.AdvanceWithStalledPeer(60, guest.LocalPlayerId);

            guest.Engine.DisableTimeSync();
            _harness.ReconnectPeer(guest); // re-enables timesync via the seed path
            Assert.IsTrue(guest.Engine.IsTimeSyncEnabled, "Setup — guest must be timesync-enabled after reconnect");

            // Let the guest finish catching up so it is a normal live peer before we skew it ahead.
            _harness.AdvanceAllToTick(host.CurrentTick + 10);

            // Skew the GUEST ahead (two guest ticks per host tick), commands flowing both ways so the
            // window fills and means stay fresh — mirror of FrameAdvantageEchoTests' fast-peer drive,
            // but the reconnected guest is the fast peer here.
            int waitFrames = 0;
            for (int i = 0; i < 150 && waitFrames == 0; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));

                host.NetworkService.Update();
                guest.NetworkService.Update();

                host.Engine.Update(dt);
                guest.Engine.Update(dt);
                guest.Engine.Update(dt);

                waitFrames = guest.Engine.TimeSyncService.RecommendWaitFrames(requireIdleInput: true);
            }

            Assert.Greater(waitFrames, 0,
                $"Activated guest must reach RecommendWaitFrames > 0 once ahead (guest tick={guest.CurrentTick}, " +
                $"host tick={host.CurrentTick}) — advantage flow must be alive for the reconnected guest");

            // Acts on it: under equal-rate driving the guest (now ahead + throttle on) must progress
            // slower than the host. Pre-fix (flag off) the guest would ignore the recommendation and
            // keep full pace — this assertion is the self-throttle gate.
            int guestStart = guest.CurrentTick;
            int hostStart = host.CurrentTick;
            for (int i = 0; i < 60; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));

                host.NetworkService.Update();
                guest.NetworkService.Update();

                host.Engine.Update(dt);
                guest.Engine.Update(dt);
            }

            Assert.Less(guest.CurrentTick - guestStart, host.CurrentTick - hostStart,
                $"Activated guest must progress slower than the host while throttled " +
                $"(guest +{guest.CurrentTick - guestStart}, host +{host.CurrentTick - hostStart}) — " +
                "it must ACT on its own recommendation (self-throttle), not just compute it");
        }
    }
}
