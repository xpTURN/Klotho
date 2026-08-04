using NUnit.Framework;

using xpTURN.Klotho.Serialization; // SpanWriter / SpanReader

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Regression guard: PlayerJoinCommand must sort BEFORE same-tick gameplay reliable commands
    /// (OrderKey 0) in CommandOrdering, so a joining player's world setup (OnPlayerJoinedWorld) runs
    /// before that player's own same-tick commands. A non-negative PlayerJoinCommand.OrderKey (e.g.
    /// JoinedPlayerId) would let the join sort after gameplay of equal/lower OrderKey.
    /// </summary>
    public sealed class CommandOrderingContractTests
    {
        // Minimal gameplay reliable double — Brawler's SpawnCharacterCommand/UseConsumableCommand use OrderKey 0.
        private sealed class FakeGameplayReliable : IReliableCommand
        {
            public int CommandTypeId { get; set; }
            public int PlayerId { get; set; }
            public int Tick { get; set; }
            public int OrderKey => 0;
            public int SequenceNumber { get; set; }
            public void Serialize(ref SpanWriter writer) { }
            public void Deserialize(ref SpanReader reader) { }
            public int GetSerializedSize() => 0;
        }

        [Test]
        public void PlayerJoin_SortsBefore_GameplayReliable()
        {
            var join = new PlayerJoinCommand { JoinedPlayerId = 2 };
            var gameplay = new FakeGameplayReliable { CommandTypeId = 200, PlayerId = 2, SequenceNumber = 0 };

            Assert.IsTrue(CommandOrdering.Compare(join, gameplay) < 0,
                "PlayerJoin must sort before same-tick gameplay reliable (seed precedes spawn)");
            Assert.IsTrue(CommandOrdering.Compare(gameplay, join) > 0, "antisymmetric");
        }

        [Test]
        public void PlayerJoins_StableOrderByJoinedPlayerId()
        {
            var joinLow = new PlayerJoinCommand { JoinedPlayerId = 1 };
            var joinHigh = new PlayerJoinCommand { JoinedPlayerId = 3 };

            Assert.IsTrue(CommandOrdering.Compare(joinLow, joinHigh) < 0,
                "lower JoinedPlayerId sorts first (stable tiebreak among simultaneous joins)");
            Assert.IsTrue(CommandOrdering.Compare(joinHigh, joinLow) > 0, "antisymmetric");
        }
    }
}
