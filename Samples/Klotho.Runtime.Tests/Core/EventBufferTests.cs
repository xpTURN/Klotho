using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Locks in the <see cref="EventBuffer"/> semantics that callers depend on:
    /// AddEvent is append (no dedup), ClearTick/ClearRange/ClearAll wipe specific slots,
    /// and the ring-buffer slot mapping collides at lag == capacity.
    /// </summary>
    [TestFixture]
    public class EventBufferTests
    {
        private sealed class TestEvent : SimulationEvent
        {
            public override int EventTypeId => 9_999_001;
        }

        [Test]
        public void AddEvent_AppendsWithoutDedup_SameTickSameContent()
        {
            var buffer = new EventBuffer(8);
            var evt1 = new TestEvent { Tick = 3 };
            var evt2 = new TestEvent { Tick = 3 };

            buffer.AddEvent(3, evt1);
            buffer.AddEvent(3, evt2);

            var events = buffer.GetEvents(3);
            Assert.AreEqual(2, events.Count, "AddEvent must append even when called twice for the same tick");
            Assert.AreSame(evt1, events[0]);
            Assert.AreSame(evt2, events[1]);
        }

        [Test]
        public void ClearTick_RemovesOnlyTargetSlot()
        {
            var buffer = new EventBuffer(8);
            buffer.AddEvent(2, new TestEvent { Tick = 2 });
            buffer.AddEvent(5, new TestEvent { Tick = 5 });

            buffer.ClearTick(2, returnToPool: false);

            Assert.AreEqual(0, buffer.GetEvents(2).Count, "ClearTick(2) must wipe slot 2");
            Assert.AreEqual(1, buffer.GetEvents(5).Count, "ClearTick(2) must not touch slot 5");
        }

        [Test]
        public void ClearRange_WipesHalfOpenRange()
        {
            var buffer = new EventBuffer(16);
            for (int t = 0; t < 6; t++)
                buffer.AddEvent(t, new TestEvent { Tick = t });

            // Half-open: [2, 5) clears ticks 2, 3, 4 only.
            buffer.ClearRange(2, 5, returnToPool: false);

            Assert.AreEqual(1, buffer.GetEvents(0).Count);
            Assert.AreEqual(1, buffer.GetEvents(1).Count);
            Assert.AreEqual(0, buffer.GetEvents(2).Count);
            Assert.AreEqual(0, buffer.GetEvents(3).Count);
            Assert.AreEqual(0, buffer.GetEvents(4).Count);
            Assert.AreEqual(1, buffer.GetEvents(5).Count, "ClearRange upper bound is exclusive");
        }

        [Test]
        public void ClearAll_WipesEverySlot()
        {
            var buffer = new EventBuffer(8);
            for (int t = 0; t < 8; t++)
                buffer.AddEvent(t, new TestEvent { Tick = t });

            buffer.ClearAll();

            for (int t = 0; t < 8; t++)
                Assert.AreEqual(0, buffer.GetEvents(t).Count, $"slot {t} should be empty after ClearAll");
        }

        [Test]
        public void RingWrap_ClearTickOnLaterTick_WipesEarlierTickInSameSlot()
        {
            // capacity = 4 → ticks T and T+4 map to the same slot.
            const int capacity = 4;
            var buffer = new EventBuffer(capacity);
            var earlier = new TestEvent { Tick = 2 };
            buffer.AddEvent(2, earlier);

            // tick 6 lives in slot (6 % 4) == 2 — same slot as tick 2.
            // ClearTick(6) wipes the slot before adding tick 6's own event.
            buffer.ClearTick(6, returnToPool: false);

            // Slot 2 is now empty. GetEvents(2) and GetEvents(6) both index the same slot.
            Assert.AreEqual(0, buffer.GetEvents(2).Count,
                "Ring-wrap: ClearTick(6) silently wipes tick 2's entries (same slot)");
            Assert.AreEqual(0, buffer.GetEvents(6).Count);

            // Sanity: after the wipe, adding for tick 6 only shows tick 6's event.
            var later = new TestEvent { Tick = 6 };
            buffer.AddEvent(6, later);
            Assert.AreEqual(1, buffer.GetEvents(6).Count);
            Assert.AreSame(later, buffer.GetEvents(6)[0]);
        }

        [Test]
        public void GetEvents_ReturnsLiveListReference()
        {
            // GetEvents returns the underlying List<SimulationEvent> by reference.
            // Holders that cache the reference observe subsequent AddEvent / ClearTick mutations.
            var buffer = new EventBuffer(8);
            var listBefore = buffer.GetEvents(1);
            Assert.AreEqual(0, listBefore.Count);

            buffer.AddEvent(1, new TestEvent { Tick = 1 });
            Assert.AreEqual(1, listBefore.Count, "GetEvents returns a live reference, not a snapshot");

            buffer.ClearTick(1, returnToPool: false);
            Assert.AreEqual(0, listBefore.Count, "ClearTick mutates the same list the caller may still hold");
        }
    }
}
