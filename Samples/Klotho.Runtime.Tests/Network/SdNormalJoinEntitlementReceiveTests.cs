using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;      // FixedString64
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests; // TestTransport

namespace xpTURN.Klotho.Tests
{
    /// <summary>
    /// SD normal-join entitlement propagation, client-receive side. Verifies that a
    /// ServerDrivenClientService, given a normal-join ConnectionResult carrying roster-parallel
    /// entitlement bytes, exposes them via GetPlayerEntitlement — and that a subsequent
    /// PlayerJoinNotificationMessage sets the joining player's entitlement. The wire round-trip of the
    /// fields is covered by a separate headless test; this drives the ServerDrivenClientService receive
    /// path, which requires the Unity test harness.
    /// </summary>
    [TestFixture]
    public class SdNormalJoinEntitlementReceiveTests
    {
        private IKLogger _logger;
        private CommandFactory _commandFactory;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SdNormalJoinEntitlementReceiveTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _commandFactory = new CommandFactory();
        }

        [TearDown]
        public void TearDown() => TestTransport.Reset();

        private static RosterEntry Entry(int playerId, string account, string name) => new RosterEntry
        {
            PlayerId        = playerId,
            ConnectionState = 1,
            ReadyState      = 0,
            Account         = FixedString64.FromString(account),
            DisplayName     = FixedString64.FromString(name),
        };

        // ── InitializeFromConnection: roster-parallel entitlement → GetPlayerEntitlement ──────

        [Test]
        public void InitializeFromConnection_NormalJoin_AppliesRosterEntitlements()
        {
            // Two tick-0 players: p1 owns {1,2,3}, p2 owns {9}.
            var result = new ConnectionResult
            {
                Transport     = new TestTransport(),
                Kind          = JoinKind.Normal,
                LocalPlayerId = 1,
                Roster        = new System.Collections.Generic.List<RosterEntry> { Entry(1, "a1", "P1"), Entry(2, "a2", "P2") },
                RosterEntitlementData    = new byte[] { 1, 2, 3, 9 },
                RosterEntitlementLengths = new System.Collections.Generic.List<int> { 3, 1 },
            };

            var svc = new ServerDrivenClientService();
            svc.InitializeFromConnection(result, _commandFactory, _logger);

            Assert.AreEqual(new byte[] { 1, 2, 3 }, svc.GetPlayerEntitlement(1), "p1 entitlement");
            Assert.AreEqual(new byte[] { 9 },       svc.GetPlayerEntitlement(2), "p2 entitlement");
        }

        [Test]
        public void InitializeFromConnection_ZeroLengthEntry_StaysNull()
        {
            // p1 has no entitlement (length 0), p2 owns {5,5}.
            var result = new ConnectionResult
            {
                Transport     = new TestTransport(),
                Kind          = JoinKind.Normal,
                LocalPlayerId = 1,
                Roster        = new System.Collections.Generic.List<RosterEntry> { Entry(1, "a1", "P1"), Entry(2, "a2", "P2") },
                RosterEntitlementData    = new byte[] { 5, 5 },
                RosterEntitlementLengths = new System.Collections.Generic.List<int> { 0, 2 },
            };

            var svc = new ServerDrivenClientService();
            svc.InitializeFromConnection(result, _commandFactory, _logger);

            Assert.IsNull(svc.GetPlayerEntitlement(1), "p1 has no entitlement → null");
            Assert.AreEqual(new byte[] { 5, 5 }, svc.GetPlayerEntitlement(2), "p2 entitlement");
        }

        [Test]
        public void InitializeFromConnection_NoEntitlements_AllNull()
        {
            // No-validator shape: empty entitlement data → every player stays null (no regression).
            var result = new ConnectionResult
            {
                Transport     = new TestTransport(),
                Kind          = JoinKind.Normal,
                LocalPlayerId = 1,
                Roster        = new System.Collections.Generic.List<RosterEntry> { Entry(1, "a1", "P1") },
                RosterEntitlementData    = null,
                RosterEntitlementLengths = new System.Collections.Generic.List<int>(),
            };

            var svc = new ServerDrivenClientService();
            svc.InitializeFromConnection(result, _commandFactory, _logger);

            Assert.IsNull(svc.GetPlayerEntitlement(1));
        }

        // ── PlayerJoinNotificationMessage: sets the joining player's entitlement ──────────────

        [Test]
        public void PlayerJoinNotification_SetsEntitlement()
        {
            // Host + client pair so a server-sent notification reaches the client's dispatch (peerId 0).
            var host = new TestTransport();
            host.Listen("localhost", 7777, 10);
            var client = new TestTransport();
            client.Connect("localhost", 7777);
            int clientPeerId = client.LocalPeerId;

            var result = new ConnectionResult
            {
                Transport     = client,
                Kind          = JoinKind.Normal,
                LocalPlayerId = 1,
                Roster        = new System.Collections.Generic.List<RosterEntry> { Entry(1, "a1", "P1") },
                RosterEntitlementLengths = new System.Collections.Generic.List<int>(),
            };

            var svc = new ServerDrivenClientService();
            svc.InitializeFromConnection(result, _commandFactory, _logger);
            Assert.IsNull(svc.GetPlayerEntitlement(3), "p3 not present yet");

            var notification = new PlayerJoinNotificationMessage
            {
                PlayerId = 3, ConnectionState = 1, IsReady = true,
                Account = "a3", DisplayName = "P3", OriginalTicket = "",
                Entitlement = new byte[] { 7, 7 },
            };
            byte[] bytes = new MessageSerializer().Serialize(notification);
            host.Send(clientPeerId, bytes, DeliveryMethod.ReliableOrdered);
            client.PollEvents(); // drains → OnDataReceived(0, ...) → HandlePlayerJoinNotification

            Assert.AreEqual(new byte[] { 7, 7 }, svc.GetPlayerEntitlement(3), "p3 entitlement set from notification");
        }
    }
}
