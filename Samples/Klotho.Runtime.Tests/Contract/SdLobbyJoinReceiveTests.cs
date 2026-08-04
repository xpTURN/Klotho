using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Regression tests for the SD client lobby-roster receive path (existing peer sees a new joiner via
    /// PlayerJoinNotificationMessage). Guards two properties the dedicated-message design is meant to hold:
    ///  (1) no blank name — the roster entry carries the authority-propagated DisplayName, never a fabricated
    ///      or empty one; and
    ///  (2) no double event — the join surfaces OnPlayerJoined / OnPlayerCountChanged exactly once, and a
    ///      duplicate (reliable resend) notification is dropped by the dup-guard.
    /// The receive path is driven headlessly by feeding a serialized message through the FakeTransport, so
    /// no Unity harness is required.
    /// </summary>
    public sealed class SdLobbyJoinReceiveTests
    {
        [Test] // join notification adds the player with the propagated (non-empty) name, firing each surface once
        public void PlayerJoinNotification_AddsPlayerWithPropagatedName_SingleEvent()
        {
            var (svc, tx, ser) = NewClient();

            int joinedCount = 0;
            IPlayerInfo joined = null;
            svc.OnPlayerJoined += p => { joinedCount++; joined = p; };
            int countChanges = 0;
            svc.OnPlayerCountChanged += _ => countChanges++;

            Feed(tx, ser, peerId: 0, new PlayerJoinNotificationMessage
            {
                PlayerId = 2,
                ConnectionState = (byte)PlayerConnectionState.Connected,
                IsReady = false,
                Account = "acct",
                DisplayName = "Alice",
                OriginalTicket = "",
            });

            Assert.AreEqual(1, svc.PlayerCount);
            Assert.AreEqual(2, svc.Players[0].PlayerId);
            Assert.AreEqual("Alice", svc.Players[0].DisplayName);   // authority name, not blank/fabricated
            Assert.AreEqual("acct", svc.Players[0].Account);
            Assert.AreEqual(1, joinedCount);                        // OnPlayerJoined fired exactly once
            Assert.AreEqual(2, joined?.PlayerId);
            Assert.AreEqual(1, countChanges);                       // OnPlayerCountChanged fired exactly once
        }

        [Test] // a duplicate notification (same PlayerId) is dropped by the dup-guard — no second add or event
        public void PlayerJoinNotification_Duplicate_IgnoredNoDoubleEvent()
        {
            var (svc, tx, ser) = NewClient();

            int joinedCount = 0;
            svc.OnPlayerJoined += _ => joinedCount++;

            var msg = new PlayerJoinNotificationMessage
            {
                PlayerId = 2,
                ConnectionState = (byte)PlayerConnectionState.Connected,
                IsReady = false,
                Account = "acct",
                DisplayName = "Alice",
                OriginalTicket = "",
            };
            Feed(tx, ser, peerId: 0, msg);
            Feed(tx, ser, peerId: 0, msg);   // reliable resend / re-delivery of the same join

            Assert.AreEqual(1, svc.PlayerCount);
            Assert.AreEqual(1, joinedCount);
        }

        [Test] // a join notification from a non-server peer (peerId != 0) is ignored (server is the only authority)
        public void PlayerJoinNotification_FromNonServerPeer_Ignored()
        {
            var (svc, tx, ser) = NewClient();

            int joinedCount = 0;
            svc.OnPlayerJoined += _ => joinedCount++;

            Feed(tx, ser, peerId: 1, new PlayerJoinNotificationMessage
            {
                PlayerId = 2,
                ConnectionState = (byte)PlayerConnectionState.Connected,
                DisplayName = "Alice",
                Account = "acct",
                OriginalTicket = "",
            });

            Assert.AreEqual(0, svc.PlayerCount);
            Assert.AreEqual(0, joinedCount);
        }

        // ── harness ───────────────────────────────────────────────────

        // A guest client wired to a FakeTransport, starting from an empty lobby roster (Normal join, no
        // roster snapshot) so each test observes only the injected join.
        private static (ServerDrivenClientService svc, FakeTransport tx, MessageSerializer ser) NewClient()
        {
            var tx = new FakeTransport();
            var svc = new ServerDrivenClientService();
            svc.InitializeFromConnection(new ConnectionResult { Transport = tx, Kind = JoinKind.Normal }, null, null);
            return (svc, tx, new MessageSerializer());
        }

        private static void Feed(FakeTransport tx, MessageSerializer ser, int peerId, NetworkMessageBase msg)
        {
            byte[] bytes = ser.Serialize(msg);
            tx.RaiseData(peerId, bytes, bytes.Length);
        }
    }
}
