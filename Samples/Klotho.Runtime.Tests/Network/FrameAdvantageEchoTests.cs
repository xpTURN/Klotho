using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// P2P TimeSync self-tick decontamination.
    ///   (a) SendCommand local echo (own player id) -> OnFrameAdvantageReceived NOT raised
    ///   (b) SendCommand local echo with another player's id (host proxy-fill shape,
    ///       Reconnect/Catchup paths) -> NOT raised — PlayerId-based guards cannot catch this
    ///   (c) remote-origin command -> raised (positive control)
    ///   (d) 2-peer integration: fast peer reaches RecommendWaitFrames > 0 once ahead of the
    ///       remote — structurally impossible before the fix (self entry collapsed the median
    ///       to the local tick, advantage stuck at ~1 < MIN_FRAME_ADVANTAGE). Also asserts
    ///       _remoteTicks never contains the local player (engine defense filter).
    /// </summary>
    [TestFixture]
    public class FrameAdvantageEchoTests
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

        // ── (a) Local echo with own player id ────────────────────────────────

        [Test]
        public void SendCommand_DoesNotRaiseFrameAdvantageForLocalEcho()
        {
            var host = _harness.Host;
            int fireCount = 0;
            host.NetworkService.OnFrameAdvantageReceived += (playerId, senderTick, senderAdvantage) => fireCount++;

            // The echo is dispatched synchronously inside SendCommand (fromPeerId == -1).
            host.NetworkService.SendCommand(
                new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));

            Assert.AreEqual(0, fireCount,
                "Local echo of own command must not raise OnFrameAdvantageReceived");
        }

        // ── (b) Local echo with another player's id (host proxy-fill shape) ──

        [Test]
        public void SendCommand_DoesNotRaiseFrameAdvantageForProxyFillEcho()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int fireCount = 0;
            host.NetworkService.OnFrameAdvantageReceived += (playerId, senderTick, senderAdvantage) => fireCount++;

            // Same shape as host proxy-fills (PredictDisconnectedPlayerInputs /
            // InjectCatchupPlayerInputs / FillAndBroadcastDisconnectedRange): the command
            // carries ANOTHER player's id but our own SenderTick. A PlayerId-based guard
            // would let this echo through.
            host.NetworkService.SendCommand(
                new EmptyCommand(guest.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));

            Assert.AreEqual(0, fireCount,
                "Local echo of a proxy-fill command (other player's id) must not raise OnFrameAdvantageReceived");
        }

        // ── (c) Remote-origin command fires (positive control) ───────────────

        [Test]
        public void RemoteCommand_RaisesFrameAdvantage()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            var fired = new List<(int playerId, int senderTick)>();
            host.NetworkService.OnFrameAdvantageReceived += (playerId, senderTick, senderAdvantage) => fired.Add((playerId, senderTick));

            guest.NetworkService.SendCommand(
                new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));
            _harness.PumpMessages();

            Assert.IsTrue(fired.Exists(f => f.playerId == guest.LocalPlayerId),
                "A command arriving over the network must raise OnFrameAdvantageReceived for the sender");
            Assert.IsFalse(fired.Exists(f => f.playerId == host.LocalPlayerId),
                "No flow may raise OnFrameAdvantageReceived with the local player's id");
        }

        // ── (d) Integration: fast peer recovers throttle once ahead ──────────

        [Test]
        public void FastPeer_ReachesPositiveRecommendWaitFrames_WhenAheadOfRemote()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            const float dt = 0.025f; // = TickIntervalMs(25)

            // Gates to pass (TimeSyncService): >= MIN_UNIQUE_FRAMES(10) samples after game
            // start, 40-frame window MEAN >= MIN_FRAME_ADVANTAGE(3) — sustained gap, and
            // idle input (EmptyCommand counts as idle). Host runs two ticks per guest tick,
            // so the gap grows ~1 tick per iteration while commands keep flowing both ways
            // (the guest's piggybacked SenderTick keeps _remoteTicks fresh).
            int waitFrames = 0;
            for (int i = 0; i < 100 && waitFrames == 0; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));

                host.NetworkService.Update();
                guest.NetworkService.Update();

                host.Engine.Update(dt);
                host.Engine.Update(dt);
                guest.Engine.Update(dt);

                waitFrames = host.Engine.TimeSyncService.RecommendWaitFrames(requireIdleInput: true);
            }

            Assert.Greater(waitFrames, 0,
                $"Fast peer must reach RecommendWaitFrames > 0 (host tick={host.CurrentTick}, guest tick={guest.CurrentTick}) " +
                "— before the fix this was structurally impossible (self entry pinned advantage at ~1)");

            // _remoteTicks must never contain the local player (engine defense filter,
            // indirect surface — the dictionary is private).
            var remoteTicks = (System.Collections.IDictionary)_remoteTicksField.GetValue(host.Engine);
            Assert.IsFalse(remoteTicks.Contains(host.LocalPlayerId),
                "_remoteTicks must not contain the local player's id");
            Assert.IsTrue(remoteTicks.Contains(guest.LocalPlayerId),
                "_remoteTicks must contain the remote peer's id (sanity — advantage flow alive)");

            // Resume assertion: stop the skew (equal-rate driving from here). The
            // throttled host must fall behind the guest's pace (throttle strength), the gap
            // must converge, and the recommendation must return to 0 with the host ticking
            // again — engage → release → resume, the full cycle. Before the fix the throttle
            // was a permanent latch (means only updated inside tick execution) and never
            // released once engaged.
            int hostAtEngage = host.CurrentTick;
            int guestAtEngage = guest.CurrentTick;
            int gapAtEngage = hostAtEngage - guestAtEngage;
            bool released = false;
            for (int i = 0; i < 300 && !released; i++)
            {
                host.NetworkService.SendCommand(
                    new EmptyCommand(host.LocalPlayerId, host.CurrentTick + host.Engine.InputDelay));
                guest.NetworkService.SendCommand(
                    new EmptyCommand(guest.LocalPlayerId, guest.CurrentTick + guest.Engine.InputDelay));

                host.NetworkService.Update();
                guest.NetworkService.Update();

                host.Engine.Update(dt);
                guest.Engine.Update(dt);

                released = host.Engine.TimeSyncService.RecommendWaitFrames(requireIdleInput: true) == 0
                           && host.CurrentTick > hostAtEngage;
            }

            Assert.IsTrue(released,
                $"Throttle must release once the remote catches up (host={host.CurrentTick}, guest={guest.CurrentTick}) " +
                "— a sustained recommendation here means the latch regressed");
            Assert.Less(host.CurrentTick - guest.CurrentTick, gapAtEngage,
                "Gap must converge while the host is throttled");
            // Throttle strength: while engaged, the host advanced fewer ticks than the guest
            // under equal driving — the budgeted trim actually slows the fast peer.
            Assert.Less(host.CurrentTick - hostAtEngage, guest.CurrentTick - guestAtEngage,
                "Host must progress slower than the guest while throttled (strength preserved)");
        }
    }
}
