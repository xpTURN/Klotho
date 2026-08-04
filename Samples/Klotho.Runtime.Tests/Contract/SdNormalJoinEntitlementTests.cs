using NUnit.Framework;

using xpTURN.Klotho.Network; // messages, MessageSerializer

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Headless wire round-trip tests for the SD normal-join entitlement propagation fields:
    /// SyncCompleteMessage's roster-parallel RosterEntitlementData/Lengths and
    /// PlayerJoinNotificationMessage's single Entitlement, incl. the empty/default (P2P / no-validator)
    /// shape — which also exercises the inline-init of RosterEntitlementLengths (a null list would throw
    /// in the generated (de)serializer). The client-receive behaviour (InitializeFromConnection /
    /// GetPlayerEntitlement) is driven separately in the Unity test harness, where the
    /// ServerDrivenClientService receive path can be exercised.
    /// </summary>
    public sealed class SdNormalJoinEntitlementTests
    {
        private static T RoundTrip<T>(T msg) where T : class, INetworkMessage
        {
            var ser = new MessageSerializer();
            byte[] bytes = ser.Serialize(msg);
            return ser.Deserialize(bytes, bytes.Length) as T;
        }

        // ── SyncCompleteMessage: roster-parallel RosterEntitlementData + Lengths ───────────────

        [Test] // multi-player entitlement blobs round-trip concat+lengths in order (index-parallel to Roster)
        public void SyncComplete_RosterEntitlements_RoundTripInOrder()
        {
            var msg = new SyncCompleteMessage { Magic = 7, PlayerId = 2 };
            // Two players: p0 = {1,2,3}, p1 = {9} — concat with per-entry lengths.
            msg.RosterEntitlementData = new byte[] { 1, 2, 3, 9 };
            msg.RosterEntitlementLengths.Add(3);
            msg.RosterEntitlementLengths.Add(1);

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            Assert.AreEqual(new byte[] { 1, 2, 3, 9 }, back.RosterEntitlementData);
            Assert.AreEqual(2, back.RosterEntitlementLengths.Count);
            Assert.AreEqual(3, back.RosterEntitlementLengths[0]);
            Assert.AreEqual(1, back.RosterEntitlementLengths[1]);
        }

        [Test] // per-entry length 0 (a player with no entitlement) round-trips within a mixed roster
        public void SyncComplete_RosterEntitlements_ZeroLengthEntry_RoundTrips()
        {
            var msg = new SyncCompleteMessage { Magic = 1, PlayerId = 1 };
            msg.RosterEntitlementData = new byte[] { 5, 5 };
            msg.RosterEntitlementLengths.Add(0); // p0 unowned
            msg.RosterEntitlementLengths.Add(2); // p1 owns {5,5}

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            Assert.AreEqual(new byte[] { 5, 5 }, back.RosterEntitlementData);
            Assert.AreEqual(0, back.RosterEntitlementLengths[0]);
            Assert.AreEqual(2, back.RosterEntitlementLengths[1]);
        }

        [Test] // P2P / no-validator shape: default (null data, empty Lengths) round-trips safely — this
                // exercises the RosterEntitlementLengths inline-init (a null list would throw on .Count/.Clear()).
        public void SyncComplete_EmptyRosterEntitlements_RoundTrips()
        {
            var back = RoundTrip(new SyncCompleteMessage { Magic = 7, PlayerId = 2 });

            Assert.IsNotNull(back);
            Assert.IsNotNull(back.RosterEntitlementLengths);
            Assert.IsEmpty(back.RosterEntitlementLengths);
            // null byte[] coalesces to empty on the wire and round-trips back to null.
            Assert.IsTrue(back.RosterEntitlementData == null || back.RosterEntitlementData.Length == 0);
        }

        [Test] // RosterEntitlement coexists with the sibling RosterTickets field (both trailing, distinct)
        public void SyncComplete_RosterEntitlements_CoexistWithRosterTickets()
        {
            var msg = new SyncCompleteMessage { Magic = 3, PlayerId = 1 };
            msg.RosterTickets.Add("tok0");
            msg.RosterEntitlementData = new byte[] { 7 };
            msg.RosterEntitlementLengths.Add(1);

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            XAssert.Single(back.RosterTickets);
            Assert.AreEqual("tok0", back.RosterTickets[0]);
            Assert.AreEqual(new byte[] { 7 }, back.RosterEntitlementData);
            XAssert.Single(back.RosterEntitlementLengths);
        }

        // ── PlayerJoinNotificationMessage: single Entitlement trailing field ──────────────────

        [Test] // a populated entitlement round-trips alongside the identity + OriginalTicket fields
        public void PlayerJoinNotification_Entitlement_RoundTrips()
        {
            var back = RoundTrip(new PlayerJoinNotificationMessage
            {
                PlayerId = 3, ConnectionState = 1, IsReady = true,
                Account = "acct", DisplayName = "Name", OriginalTicket = "tok",
                Entitlement = new byte[] { 4, 2 },
            });

            Assert.IsNotNull(back);
            Assert.AreEqual(3, back.PlayerId);
            Assert.AreEqual("tok", back.OriginalTicket);
            Assert.AreEqual(new byte[] { 4, 2 }, back.Entitlement);
        }

        [Test] // no-validator shape: null Entitlement round-trips (back to null) with no regression
        public void PlayerJoinNotification_NullEntitlement_RoundTrips()
        {
            var back = RoundTrip(new PlayerJoinNotificationMessage
            {
                PlayerId = 1, Account = "a", DisplayName = "n", OriginalTicket = "",
            });

            Assert.IsNotNull(back);
            Assert.IsTrue(back.Entitlement == null || back.Entitlement.Length == 0);
        }
    }
}
