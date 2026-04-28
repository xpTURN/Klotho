using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Integration.Tests
{
    [TestFixture]
    public class LateJoinIntegrationTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("LateJoinIntegrationTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        #region 38. Concurrent Late Join — 2 or more players

        [Test]
        public void ConcurrentLateJoin_TwoPlayers_AllPeersConsistent()
        {
            // 1. Host + Guest1 → Playing (2 players)
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();

            // 2. Advance the game
            _harness.AdvanceAllToTick(50);

            // 3-4. Two Late Join Guests connecting concurrently
            var lateJoinGuest2 = _harness.AddLateJoinGuest();
            var lateJoinGuest3 = _harness.AddLateJoinGuest();

            // 5. Handshake completed
            _harness.PumpMessages(20);

            // 6. Execute PlayerJoinCommand + catchup completed
            _harness.AdvanceAllToTick(100);

            // 7. Verify
            _harness.AssertPlayerCountConsistent(4);
            _harness.AssertActivePlayerIdsContains(0, 1, 2, 3);
            _harness.AssertStateHashConsistent();

            Assert.AreEqual(SessionPhase.Playing, lateJoinGuest2.Phase,
                "LateJoinGuest2 should be Playing");
            Assert.AreEqual(SessionPhase.Playing, lateJoinGuest3.Phase,
                "LateJoinGuest3 should be Playing");

            Assert.IsFalse(_harness.IsCatchingUp(lateJoinGuest2),
                "LateJoinGuest2 should not be catching up");
            Assert.IsFalse(_harness.IsCatchingUp(lateJoinGuest3),
                "LateJoinGuest3 should not be catching up");
        }

        #endregion

        #region 40. Disconnect during CatchingUp → Reconnect

        [Test]
        public void LateJoinPlayer_Disconnect_CleansUpAndReconnects()
        {
            // 1. Host + Guest1 → Playing (2 players)
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();

            // 2. Advance the game
            _harness.AdvanceAllToTick(50);

            // 3. Late Join Guest connects
            var lateJoinGuest2 = _harness.AddLateJoinGuest();

            // 4. Handshake + catchup completed (until Active transition)
            _harness.PumpMessages(20);
            _harness.AdvanceAllToTick(80);

            Assert.AreEqual(SessionPhase.Playing, lateJoinGuest2.Phase,
                "LateJoinGuest2 should be Playing");
            Assert.IsFalse(_harness.IsCatchingUp(lateJoinGuest2),
                "LateJoinGuest2 should have completed catchup");

            // 5. Disconnect
            _harness.DisconnectPeer(lateJoinGuest2);

            // 6. Process on the Host side
            _harness.Tick();

            // 7. Verify — disconnect handled
            Assert.AreEqual(0, _harness.GetLateJoinCatchupsCount(),
                "_lateJoinCatchups should be cleared after disconnect");
            Assert.AreEqual(1, _harness.GetDisconnectedPlayerCount(),
                "_disconnectedPlayerCount should be 1");

            // PlayerJoinCommand has already been broadcast
            _harness.AssertPlayerCountConsistent(3);

            // 8. Reconnect
            _harness.ReconnectPeer(lateJoinGuest2);

            // 9. Handshake completed
            _harness.PumpMessages(20);

            // 10. Verify — restored
            Assert.AreEqual(0, _harness.GetDisconnectedPlayerCount(),
                "_disconnectedPlayerCount should be 0 after reconnect");
        }

        #endregion

        #region 41. One handshake fails during concurrent Late Join

        [Test]
        public void ConcurrentLateJoin_OneHandshakeFails_OtherSucceeds()
        {
            // 1. Host + Guest1 → Playing (2 players)
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();

            // 2. Advance the game
            _harness.AdvanceAllToTick(50);

            // 3-4. Two Late Join Guests connecting concurrently
            var lateJoinGuest2 = _harness.AddLateJoinGuest();
            var lateJoinGuest3 = _harness.AddLateJoinGuest();

            // 5. Handshake in progress (before completion)
            _harness.PumpMessages(5);

            // 6. Disconnect Guest3 during handshake
            _harness.DisconnectPeer(lateJoinGuest3);

            // 7. Guest2 handshake completed
            _harness.PumpMessages(20);

            // 8. Guest2 catchup + Active transition
            _harness.AdvanceAllToTick(100);

            // 9. Verify
            _harness.AssertPlayerCountConsistent(3);

            Assert.AreEqual(SessionPhase.Playing, lateJoinGuest2.Phase,
                "LateJoinGuest2 should be Playing");
            Assert.IsFalse(_harness.IsCatchingUp(lateJoinGuest2),
                "LateJoinGuest2 should have completed catchup");

            // Verify that Guest3's sync state on the Host has been cleaned up
            Assert.IsFalse(_harness.HasPeerSyncState(lateJoinGuest3.Transport.LocalPeerId),
                "Host should not have peerSyncState for disconnected Guest3");

            Assert.AreEqual(0, _harness.GetLateJoinCatchupsCount(),
                "Host should not have lateJoinCatchups for disconnected Guest3");
        }

        #endregion
    }
}
