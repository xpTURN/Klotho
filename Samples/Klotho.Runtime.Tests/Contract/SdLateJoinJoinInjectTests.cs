using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD late-join participation guards: the server-authored PlayerJoinCommand is scheduled into
    /// the input collector's system lane (drained at exactly its joinTick, entering the executed /
    /// recorded / broadcast list), and the joiner's reliable commands (e.g. spawn) are held by the
    /// per-player placement floor until that tick so the join-time seed always precedes them.
    /// Wire tests guard the entitlement propagation fields appended to the late-join / reconnect
    /// messages (append-only tail convention).
    /// </summary>
    public sealed class SdLateJoinJoinInjectTests
    {
        // ── system-command lane ───────────────────────────────────────

        [Test] // scheduled command surfaces at exactly its tick — not before, and consumed after
        public void SystemCommand_DrainsAtScheduledTickOnly()
        {
            var collector = new ServerInputCollector();
            var join = new PlayerJoinCommand { Tick = 5, JoinedPlayerId = 2 };
            collector.AddSystemCommand(5, join);

            XAssert.DoesNotContain(collector.CollectTickInputs(4), c => c is PlayerJoinCommand);
            XAssert.Contains(collector.CollectTickInputs(5), c => c is PlayerJoinCommand && c.Tick == 5);
            XAssert.DoesNotContain(collector.CollectTickInputs(6), c => c is PlayerJoinCommand);
        }

        // ── reliable placement floor ──────────────────────────────────

        [Test] // reliable held while tick < floor, placed (stamped to the drain tick) once reached
        public void PlacementFloor_HoldsReliableUntilFloorTick()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 1);
            collector.SetReliablePlacementFloor(1, 10);
            Assert.IsTrue(collector.TryAcceptReliable(5, 1, sequenceNumber: 1, new PlayerJoinCommand()));

            XAssert.DoesNotContain(collector.CollectTickInputs(8), c => c is PlayerJoinCommand);
            XAssert.DoesNotContain(collector.CollectTickInputs(9), c => c is PlayerJoinCommand);
            var placed = collector.CollectTickInputs(10).OfType<PlayerJoinCommand>().ToList();
            XAssert.Single(placed);
            Assert.AreEqual(10, placed[0].Tick);
            Assert.AreEqual(1, placed[0].PlayerId);
        }

        [Test] // a same-seq resubmit while held is deduped by the high-water — no double placement
        public void PlacementFloor_SameSeqResubmitWhileHeld_Deduped()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 1);
            collector.SetReliablePlacementFloor(1, 10);
            Assert.IsTrue(collector.TryAcceptReliable(5, 1, 1, new PlayerJoinCommand()));
            Assert.IsFalse(collector.TryAcceptReliable(5, 1, 1, new PlayerJoinCommand()));

            collector.CollectTickInputs(9);
            XAssert.Single(collector.CollectTickInputs(10).OfType<PlayerJoinCommand>());
        }

        [Test] // no floor set → reliable places at its arrival tick (existing behavior preserved)
        public void NoFloor_ReliablePlacesAtArrivalTick()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 1);
            Assert.IsTrue(collector.TryAcceptReliable(5, 1, 1, new PlayerJoinCommand()));

            XAssert.Single(collector.CollectTickInputs(3).OfType<PlayerJoinCommand>());
        }

        [Test] // permanent leave drops the held inbox and the floor — nothing surfaces later
        public void RemovePlayer_DropsHeldInboxAndFloor()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 1);
            collector.SetReliablePlacementFloor(1, 10);
            Assert.IsTrue(collector.TryAcceptReliable(5, 1, 1, new PlayerJoinCommand()));

            collector.RemovePlayer(1);

            Assert.IsEmpty(collector.CollectTickInputs(10));
        }

        [Test] // permanent leave before joinTick purges the pending PlayerJoinCommand — no ghost participant
        public void RemovePlayer_PurgesPendingJoinCommand()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 2);
            collector.AddSystemCommand(10, new PlayerJoinCommand { Tick = 10, JoinedPlayerId = 2 });

            collector.RemovePlayer(2);

            // Without the purge the join would still drain here and create a slot for the departed player.
            XAssert.DoesNotContain(collector.CollectTickInputs(10), c => c is PlayerJoinCommand);
        }

        [Test] // only the leaving player's join is purged — another player's same-tick join survives
        public void RemovePlayer_PurgesOnlyLeavingPlayersJoin()
        {
            var collector = NewCollectorWithPlayer(peerId: 5, playerId: 2);
            collector.AddPlayer(3);
            collector.AddSystemCommand(10, new PlayerJoinCommand { Tick = 10, JoinedPlayerId = 2 });
            collector.AddSystemCommand(10, new PlayerJoinCommand { Tick = 10, JoinedPlayerId = 3 });

            collector.RemovePlayer(2);

            var joins = collector.CollectTickInputs(10).OfType<PlayerJoinCommand>().ToList();
            XAssert.Single(joins);
            Assert.AreEqual(3, joins[0].JoinedPlayerId);
        }

        // ── entitlement propagation wire fields (append-only tail) ────

        [Test] // notification carries the late-joiner's entitlement bytes; empty round-trips as null
        public void LateJoinNotification_Entitlement_RoundTripsOnWire()
        {
            var back = RoundTrip(new LateJoinNotificationMessage
            {
                PlayerId = 2,
                JoinTick = 110,
                Account = "acc",
                DisplayName = "name",
                OriginalTicket = "",
                Entitlement = new byte[] { 1, 2, 3 },
            });
            Assert.AreEqual(new byte[] { 1, 2, 3 }, back.Entitlement);

            var backEmpty = RoundTrip(new LateJoinNotificationMessage { PlayerId = 2 });
            Assert.IsNull(backEmpty.Entitlement);
        }

        [Test] // late-join accept: roster-parallel entitlements (concat+lengths) after JoinTick/RosterTickets
        public void LateJoinAccept_RosterEntitlements_RoundTripOnWire()
        {
            var back = RoundTrip(new LateJoinAcceptMessage
            {
                JoinTick = 110,
                RosterTickets = { "t0", "t1" },
                RosterEntitlementData = new byte[] { 9, 9, 7 },
                RosterEntitlementLengths = { 2, 0, 1 },
            });
            Assert.AreEqual(110, back.JoinTick);
            Assert.AreEqual(new[] { "t0", "t1" }, back.RosterTickets);
            Assert.AreEqual(new byte[] { 9, 9, 7 }, back.RosterEntitlementData);
            Assert.AreEqual(new[] { 2, 0, 1 }, back.RosterEntitlementLengths);
        }

        [Test] // reconnect accept: same roster-parallel entitlement tail
        public void ReconnectAccept_RosterEntitlements_RoundTripOnWire()
        {
            var back = RoundTrip(new ReconnectAcceptMessage
            {
                PlayerId = 1,
                RosterTickets = { "t0" },
                RosterEntitlementData = new byte[] { 4, 2 },
                RosterEntitlementLengths = { 2 },
            });
            Assert.AreEqual(new byte[] { 4, 2 }, back.RosterEntitlementData);
            Assert.AreEqual(new[] { 2 }, back.RosterEntitlementLengths);
        }

        // ── harness ───────────────────────────────────────────────────

        private static ServerInputCollector NewCollectorWithPlayer(int peerId, int playerId)
        {
            var collector = new ServerInputCollector();
            collector.Configure(new Dictionary<int, int> { [peerId] = playerId });
            collector.AddPlayer(playerId);
            return collector;
        }

        private static T RoundTrip<T>(T msg) where T : NetworkMessageBase
        {
            var ser = new MessageSerializer();
            byte[] bytes = ser.Serialize(msg);
            var back = ser.Deserialize(bytes, bytes.Length) as T;
            Assert.IsNotNull(back);
            return back;
        }
    }
}
