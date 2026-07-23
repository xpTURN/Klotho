using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// ServerInputCollector reliable inbox: authoritative tick placement, per-player high-water
    /// sequence dedup, peer↔player validation (past-tick check not applicable — no client tick).
    /// </summary>
    [TestFixture]
    public class ServerInputCollectorReliableTests
    {
        // Minimal reliable-command double (CommandBase so the collector can stamp commit Tick;
        // PlayerId/Tick inherited, SerializeData/DeserializeData are the only abstract members).
        private sealed class FakeReliable : CommandBase, IReliableCommand
        {
            public override int CommandTypeId => 4242;
            public int OrderKey => 0;
            public int SequenceNumber { get; set; }
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }

        private static ServerInputCollector NewCollector(int peerId, int playerId)
        {
            var collector = new ServerInputCollector();
            collector.Configure(new Dictionary<int, int> { { peerId, playerId } });
            collector.AddPlayer(playerId);
            return collector;
        }

        [Test]
        public void TryAcceptReliable_PeerMismatch_Rejected()
        {
            var c = NewCollector(peerId: 0, playerId: 0);
            var cmd = new FakeReliable { PlayerId = 0, SequenceNumber = 1 };
            // peerId 9 is not mapped to playerId 0 → spoof/unregistered.
            Assert.IsFalse(c.TryAcceptReliable(peerId: 9, playerId: 0, sequenceNumber: 1, cmd),
                "PeerMismatch must reject reliable submit (security)");
        }

        [Test]
        public void TryAcceptReliable_DuplicateSeq_Ignored()
        {
            var c = NewCollector(0, 0);
            Assert.IsTrue(c.TryAcceptReliable(0, 0, 1, new FakeReliable { PlayerId = 0, SequenceNumber = 1 }),
                "first seq accepted");
            Assert.IsFalse(c.TryAcceptReliable(0, 0, 1, new FakeReliable { PlayerId = 0, SequenceNumber = 1 }),
                "seq <= high-water is a duplicate resend → ignored");
            Assert.IsTrue(c.TryAcceptReliable(0, 0, 2, new FakeReliable { PlayerId = 0, SequenceNumber = 2 }),
                "higher seq accepted");
        }

        [Test]
        public void CollectTickInputs_PlacesReliableAtExecutedTick()
        {
            var c = NewCollector(0, 0);
            var rel = new FakeReliable { PlayerId = 0, SequenceNumber = 1 };
            c.TryAcceptReliable(0, 0, 1, rel);

            var batch = c.CollectTickInputs(tick: 7);

            Assert.Contains(rel, batch, "drained reliable must be placed into the executed tick's batch");
            Assert.AreEqual(7, rel.Tick, "commit tick must be stamped onto the reliable command");

            // Inbox drained — next tick has no leftover reliable (only the empty player slot).
            var next = c.CollectTickInputs(tick: 8);
            Assert.IsFalse(next.Contains(rel), "reliable must not be re-placed on a later tick (drained once)");
        }
    }
}
