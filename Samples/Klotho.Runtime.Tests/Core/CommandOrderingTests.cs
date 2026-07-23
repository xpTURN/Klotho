using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Core
{
    /// <summary>
    /// CommandOrdering — the single source of truth for system/reliable list-channel
    /// ordering shared by InputBuffer.GetCommandList and KlothoEngine.s_commandComparer.
    /// Verifies the total-order key chain (OrderKey → CommandTypeId → PlayerId → SequenceNumber),
    /// determinism under input shuffle (List.Sort instability must be irrelevant), the seq=0
    /// fallback for non-reliable system commands, and the IReliableCommand ⊂ ISystemCommand
    /// routing premise.
    /// </summary>
    [TestFixture]
    public class CommandOrderingTests
    {
        // Minimal system-command double (no SequenceNumber → treated as 0 by CommandOrdering).
        private sealed class FakeSystem : ISystemCommand
        {
            public int CommandTypeId { get; set; }
            public int PlayerId { get; set; }
            public int Tick { get; set; }
            public int OrderKey { get; set; }
            public void Serialize(ref SpanWriter writer) { }
            public void Deserialize(ref SpanReader reader) { }
            public int GetSerializedSize() => 0;
        }

        // Minimal reliable-command double.
        private sealed class FakeReliable : IReliableCommand
        {
            public int CommandTypeId { get; set; }
            public int PlayerId { get; set; }
            public int Tick { get; set; }
            public int OrderKey { get; set; }
            public int SequenceNumber { get; set; }
            public void Serialize(ref SpanWriter writer) { }
            public void Deserialize(ref SpanReader reader) { }
            public int GetSerializedSize() => 0;
        }

        [Test]
        public void ReliableCommand_IsSystemCommand()
        {
            // Routing premise: IReliableCommand is an ISystemCommand subtype, so the
            // `is ISystemCommand` branch in both comparers/AddCommandChecked auto-captures it.
            ICommand reliable = new FakeReliable();
            Assert.IsTrue(reliable is ISystemCommand,
                "IReliableCommand must be an ISystemCommand subtype (shared channel + ordering)");
        }

        [Test]
        public void Compare_OrderKey_IsPrimaryKey()
        {
            // Lower OrderKey sorts first regardless of every lower-priority field.
            var a = new FakeReliable { OrderKey = 1, CommandTypeId = 999, PlayerId = 9, SequenceNumber = 9 };
            var b = new FakeReliable { OrderKey = 2, CommandTypeId = 0,   PlayerId = 0, SequenceNumber = 0 };
            Assert.Less(CommandOrdering.Compare(a, b), 0, "OrderKey 1 < 2 dominates lower keys");
            Assert.Greater(CommandOrdering.Compare(b, a), 0, "antisymmetric");
        }

        [Test]
        public void Compare_TieBreaks_TypeId_then_PlayerId_then_Seq()
        {
            // Equal OrderKey → CommandTypeId.
            var t1 = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 5, SequenceNumber = 5 };
            var t2 = new FakeReliable { OrderKey = 0, CommandTypeId = 11, PlayerId = 0, SequenceNumber = 0 };
            Assert.Less(CommandOrdering.Compare(t1, t2), 0, "tie on OrderKey → CommandTypeId");

            // Equal OrderKey+TypeId → PlayerId.
            var p1 = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 9 };
            var p2 = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 2, SequenceNumber = 0 };
            Assert.Less(CommandOrdering.Compare(p1, p2), 0, "tie on OrderKey+TypeId → PlayerId");

            // Equal OrderKey+TypeId+PlayerId → SequenceNumber.
            var s1 = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 7 };
            var s2 = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 8 };
            Assert.Less(CommandOrdering.Compare(s1, s2), 0, "tie on OrderKey+TypeId+PlayerId → SequenceNumber");
        }

        [Test]
        public void Compare_FullyEqualKeys_IsZero()
        {
            var a = new FakeReliable { OrderKey = 3, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 7 };
            var b = new FakeReliable { OrderKey = 3, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 7 };
            Assert.AreEqual(0, CommandOrdering.Compare(a, b), "identical key tuple → equal");
        }

        [Test]
        public void NonReliableSystem_SeqFallsBackToZero()
        {
            // Pure ISystemCommand (no seq) vs reliable with seq>0, all higher keys equal:
            // system is treated as seq=0 and sorts before the reliable.
            var sys = new FakeSystem   { OrderKey = 0, CommandTypeId = 10, PlayerId = 1 };
            var rel = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 1 };
            Assert.Less(CommandOrdering.Compare(sys, rel), 0,
                "non-reliable system command's missing seq is treated as 0 (sorts before seq=1)");
        }

        [Test]
        public void Compare_ProducesSameOrder_RegardlessOfInputShuffle()
        {
            // Total order ⇒ List.Sort (unstable) yields identical sequence from any input order.
            ICommand a = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 2, SequenceNumber = 1 };
            ICommand b = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 2, SequenceNumber = 2 };
            ICommand c = new FakeReliable { OrderKey = 0, CommandTypeId = 10, PlayerId = 1, SequenceNumber = 9 };
            ICommand d = new FakeSystem   { OrderKey = 5, CommandTypeId = 99, PlayerId = 0 };

            var order1 = new List<ICommand> { a, b, c, d };
            var order2 = new List<ICommand> { d, c, b, a };
            var order3 = new List<ICommand> { b, d, a, c };
            order1.Sort(CommandOrdering.Compare);
            order2.Sort(CommandOrdering.Compare);
            order3.Sort(CommandOrdering.Compare);

            // Expected: c (pid1) < a (pid2,seq1) < b (pid2,seq2) < d (OrderKey5).
            CollectionAssert.AreEqual(new List<ICommand> { c, a, b, d }, order1, "key-chain order");
            CollectionAssert.AreEqual(order1, order2, "shuffle-invariant (total order)");
            CollectionAssert.AreEqual(order1, order3, "shuffle-invariant (total order)");
        }
    }
}
