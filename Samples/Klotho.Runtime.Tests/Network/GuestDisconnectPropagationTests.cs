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
    /// Guest-side disconnect awareness propagation. The host broadcasts confirmed
    /// roster transitions (disconnect / reconnect / leave); guests mirror them into the engine
    /// roster sets (_disconnectedPlayerIds / _activePlayerIds) so a departed peer is excluded
    /// from the timing-advantage vote, and into the _players surface so
    /// PlayerCount / connection state stay consistent with the host.
    ///   - Disconnected mirrors to _disconnectedPlayerIds + ConnectionState, but NOT _activePlayerIds
    ///     (partiality: timing exclusion only; chain-advance fill is unaffected).
    ///   - Reconnected clears the disconnected mark + ConnectionState.
    ///   - Left removes from _activePlayerIds + _players (full roster removal).
    ///   - A guest-originated (forged) notification reaching the host is rejected (IsHost guard).
    ///   - End-to-end: a transport disconnect on the host broadcasts Disconnected and the other
    ///     guest's engine excludes the dropped peer.
    /// </summary>
    [TestFixture]
    internal class GuestDisconnectPropagationTests
    {
        private static readonly FieldInfo _disconnectedPlayerIdsField = typeof(KlothoEngine)
            .GetField("_disconnectedPlayerIds", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _activePlayerIdsField = typeof(KlothoEngine)
            .GetField("_activePlayerIds", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _handlePlayerStateMethod = typeof(KlothoNetworkService)
            .GetMethod("HandlePlayerStateNotification", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("GuestDisconnectPropagationTests");
        }

        [SetUp]
        public void SetUp()
        {
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        // ── Reflection / surface helpers ──

        private static bool EngineDisconnectedContains(TestPeer peer, int playerId)
        {
            var set = (HashSet<int>)_disconnectedPlayerIdsField.GetValue(peer.Engine);
            return set.Contains(playerId);
        }

        private static bool EngineActiveContains(TestPeer peer, int playerId)
        {
            var list = (List<int>)_activePlayerIdsField.GetValue(peer.Engine);
            return list.Contains(playerId);
        }

        // Invoke the guest handler directly, bypassing transport — deterministically exercises
        // the guest mirroring logic without the proxy-fill / inject timing of a live disconnect.
        private static void Deliver(TestPeer peer, int playerId, PlayerStateChange state)
        {
            var msg = new PlayerStateNotificationMessage { PlayerId = playerId, State = (byte)state };
            _handlePlayerStateMethod.Invoke(peer.NetworkService, new object[] { msg });
        }

        private static bool PlayersContains(TestPeer peer, int playerId)
        {
            foreach (var p in peer.NetworkService.Players)
                if (p.PlayerId == playerId) return true;
            return false;
        }

        private static PlayerConnectionState ConnState(TestPeer peer, int playerId)
        {
            foreach (var p in peer.NetworkService.Players)
                if (p.PlayerId == playerId) return p.ConnectionState;
            return (PlayerConnectionState)(-1); // not found sentinel
        }

        private (TestPeer guestA, TestPeer guestB) SetUpThreePeerSession()
        {
            _harness.CreateHost();
            var guestA = _harness.AddGuest();
            var guestB = _harness.AddGuest();
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(10);
            return (guestA, guestB);
        }

        // ── Tests ──

        [Test]
        public void Guest_Disconnect_ExcludesFromTimingButKeepsActive()
        {
            var (guestA, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            // Before propagation: B is active and not disconnected on guestA.
            Assert.IsTrue(EngineActiveContains(guestA, bId), "precondition: B active on guestA");
            Assert.IsFalse(EngineDisconnectedContains(guestA, bId), "precondition: B not disconnected on guestA");

            Deliver(guestA, bId, PlayerStateChange.Disconnected);

            Assert.IsTrue(EngineDisconnectedContains(guestA, bId),
                "Disconnected must add B to guestA's _disconnectedPlayerIds (timing-vote exclusion)");
            Assert.IsTrue(EngineActiveContains(guestA, bId),
                "Disconnected must NOT remove B from _activePlayerIds (partiality — chain fill unaffected)");
            Assert.AreEqual(PlayerConnectionState.Disconnected, ConnState(guestA, bId),
                "_players surface should reflect Disconnected");
        }

        [Test]
        public void Guest_Reconnect_ClearsDisconnectedMark()
        {
            var (guestA, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            Deliver(guestA, bId, PlayerStateChange.Disconnected);
            Assert.IsTrue(EngineDisconnectedContains(guestA, bId));

            Deliver(guestA, bId, PlayerStateChange.Reconnected);

            Assert.IsFalse(EngineDisconnectedContains(guestA, bId),
                "Reconnected must clear B from _disconnectedPlayerIds");
            Assert.IsTrue(EngineActiveContains(guestA, bId), "B stays active across reconnect");
            Assert.AreEqual(PlayerConnectionState.Connected, ConnState(guestA, bId),
                "_players surface should reflect Connected");
        }

        [Test]
        public void Guest_Left_RemovesFromActiveAndPlayers()
        {
            var (guestA, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            Assert.IsTrue(EngineActiveContains(guestA, bId));
            Assert.IsTrue(PlayersContains(guestA, bId));

            Deliver(guestA, bId, PlayerStateChange.Left);

            Assert.IsFalse(EngineActiveContains(guestA, bId),
                "Left must remove B from _activePlayerIds (HasAllCommands no longer requires B)");
            Assert.IsFalse(EngineDisconnectedContains(guestA, bId),
                "Left removal also clears any disconnected mark");
            Assert.IsFalse(PlayersContains(guestA, bId),
                "Left must remove B from the _players surface");
        }

        [Test]
        public void Guest_Notifications_AreIdempotent()
        {
            var (guestA, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            // Duplicate Disconnected + an out-of-order Disconnected-after-Left must not throw or corrupt.
            Deliver(guestA, bId, PlayerStateChange.Disconnected);
            Deliver(guestA, bId, PlayerStateChange.Disconnected);
            Assert.IsTrue(EngineDisconnectedContains(guestA, bId));

            Deliver(guestA, bId, PlayerStateChange.Left);
            Assert.IsFalse(EngineActiveContains(guestA, bId));

            Assert.DoesNotThrow(() => Deliver(guestA, bId, PlayerStateChange.Disconnected),
                "stale Disconnected after Left must be harmless");
            Assert.IsFalse(PlayersContains(guestA, bId), "B stays removed");
        }

        [Test]
        public void Host_RejectsForgedNotification()
        {
            var (_, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            // A guest-originated notification reaching the host must be ignored — host owns its roster.
            Deliver(_harness.Host, bId, PlayerStateChange.Left);

            Assert.IsTrue(EngineActiveContains(_harness.Host, bId),
                "host must ignore a guest-originated state notification (IsHost guard)");
        }

        [Test]
        public void HostBroadcast_OnTransportDisconnect_GuestEngineExcludesPeer()
        {
            var (guestA, guestB) = SetUpThreePeerSession();
            int bId = guestB.LocalPlayerId;

            Assert.IsFalse(EngineDisconnectedContains(guestA, bId));

            _harness.DisconnectPeer(guestB);
            _harness.PumpMessages(); // host processes the disconnect + broadcasts; guestA receives

            Assert.IsTrue(EngineDisconnectedContains(_harness.Host, bId),
                "host should mark B disconnected on transport loss");
            Assert.IsTrue(EngineDisconnectedContains(guestA, bId),
                "guestA should receive the host's Disconnected broadcast and exclude B from the timing vote");
        }
    }
}
