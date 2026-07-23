using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Core
{
    /// <summary>
    /// InputBuffer reliable-channel contracts:
    ///   - client per-(playerId, seq) high-water dedup in AddSystemCommand (a verified-tick
    ///     reprocess must not double-add a reliable command).
    ///   - ClearAfter PRESERVES committed reliable commands (a rollback must not wipe them; P2P
    ///     has no re-delivery), while non-reliable system commands are still wiped.
    ///   - ClearBefore still cleans reliable normally (forward cleanup), and Clear resets dedup.
    /// </summary>
    [TestFixture]
    public class InputBufferReliableTests
    {
        private sealed class FakeReliable : CommandBase, IReliableCommand
        {
            public override int CommandTypeId => 700;
            public int OrderKey => 0;
            public int SequenceNumber { get; set; }
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }

        // Non-reliable system command (e.g. PlayerJoin-like) — wiped by ClearAfter, not preserved.
        // CommandBase-derived so it can be rented from CommandPool (ownership-contract compliant).
        private sealed class FakeSystem : CommandBase, ISystemCommand
        {
            public override int CommandTypeId => 701;
            public int OrderKey => 1;
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }

        // Rent test doubles from the pool so the buffer's cleanup Return() sees pool-owned instances
        // (game-side `new` would trip CommandPool's ownership-violation diagnostic).
        private static FakeReliable Reliable(int tick, int playerId, int seq)
        {
            var c = CommandPool.Get<FakeReliable>();
            c.Tick = tick;
            c.PlayerId = playerId;
            c.SequenceNumber = seq;
            return c;
        }

        private static FakeSystem System(int tick, int playerId)
        {
            var c = CommandPool.Get<FakeSystem>();
            c.Tick = tick;
            c.PlayerId = playerId;
            return c;
        }

        [TearDown]
        public void TearDown() => CommandPool.ClearAll();

        private static int CountAt(InputBuffer buf, int tick)
        {
            int n = 0;
            foreach (var c in buf.GetCommandList(tick))
                if (c is IReliableCommand) n++;
            return n;
        }

        [Test]
        public void AddSystemCommand_DedupsByPlayerSeqHighWater()
        {
            var buf = new InputBuffer();
            buf.AddCommand(Reliable(tick: 5, playerId: 0, seq: 1));
            buf.AddCommand(Reliable(tick: 5, playerId: 0, seq: 1));   // duplicate seq → ignored
            Assert.AreEqual(1, CountAt(buf, 5), "duplicate (playerId,seq) must not double-add");

            buf.AddCommand(Reliable(tick: 6, playerId: 0, seq: 2));   // higher seq → accepted
            Assert.AreEqual(1, CountAt(buf, 6), "higher seq accepted");

            buf.AddCommand(Reliable(tick: 7, playerId: 1, seq: 1));   // other player, independent seq
            Assert.AreEqual(1, CountAt(buf, 7), "per-player high-water is independent");
        }

        [Test]
        public void ClearAfter_PreservesReliable_WipesNonReliableSystem()
        {
            var buf = new InputBuffer();
            buf.AddCommand(Reliable(tick: 10, playerId: 0, seq: 1));      // reliable at future tick
            buf.AddCommand(System(tick: 10, playerId: 0));               // non-reliable system at same tick

            buf.ClearAfter(tick: 5);   // rollback to 5 — wipe > 5

            Assert.AreEqual(1, CountAt(buf, 10), "reliable command must survive ClearAfter");
            bool hasNonReliableSystem = false;
            foreach (var c in buf.GetCommandList(10))
                if (c is FakeSystem) hasNonReliableSystem = true;
            Assert.IsFalse(hasNonReliableSystem, "non-reliable system command must be wiped by ClearAfter");
        }

        [Test]
        public void ClearBefore_CleansReliable()
        {
            var buf = new InputBuffer();
            buf.AddCommand(Reliable(tick: 3, playerId: 0, seq: 1));
            buf.ClearBefore(tick: 5);   // forward cleanup past tick 3
            Assert.AreEqual(0, CountAt(buf, 3), "ClearBefore must clean reliable normally (no leak)");
        }

        [Test]
        public void Clear_ResetsHighWater()
        {
            var buf = new InputBuffer();
            buf.AddCommand(Reliable(tick: 5, playerId: 0, seq: 3));
            buf.Clear();   // match reset
            // After reset, the same seq is admissible again (fresh sequence).
            buf.AddCommand(Reliable(tick: 5, playerId: 0, seq: 3));
            Assert.AreEqual(1, CountAt(buf, 5), "Clear resets high-water — same seq admissible in a new match");
        }
    }
}
