using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SyncedOnlyEventCollector returns dropped Regular events to the EventPool.
    ///
    /// The SD server collector keeps only EventMode.Synced events and drops Regular ones. Because a
    /// dropped event never reaches the EventBuffer (which owns the return for collected events), the
    /// collector must return the rented instance itself — otherwise it leaks out of the pool (release:
    /// per-event new T() fallback; DEBUG: _outstanding grows unboundedly).
    /// </summary>
    [TestFixture]
    public class SyncedOnlyEventCollectorReturnTests
    {
        // Mode defaults to Regular (SimulationEvent.Mode is virtual).
        private sealed class RegularTestEvent : SimulationEvent
        {
            public override int EventTypeId => 9_900_201;
        }

        private sealed class SyncedTestEvent : SimulationEvent
        {
            public override int EventTypeId => 9_900_202;
            public override EventMode Mode => EventMode.Synced;
        }

        [SetUp]
        public void SetUp() => EventPool.ClearAll();

        // ── 1. Dropped Regular event is returned to the pool, not collected ──

        [Test]
        public void Regular_Raise_NotCollected_AndReturnedToPool()
        {
            var collector = new SyncedOnlyEventCollector();
            collector.BeginTick(0);

            int before = EventPool.GetTotalPooledCount();
            var evt = EventPool.Get<RegularTestEvent>();

            collector.RaiseEvent(evt);

            Assert.AreEqual(0, collector.Count,
                "Regular event must not be collected by the SD server collector");
            Assert.AreEqual(before + 1, EventPool.GetTotalPooledCount(),
                "dropped Regular event must be returned to the pool");
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            Assert.AreEqual(0, EventPool.GetOutstandingCount(),
                "returned Regular event must clear the outstanding set (no leak)");
#endif
        }

        // ── 2. Synced event is collected and NOT returned by the collector (buffer owns its return) ──

        [Test]
        public void Synced_Raise_IsCollected_AndNotReturnedByCollector()
        {
            var collector = new SyncedOnlyEventCollector();
            collector.BeginTick(0);

            int before = EventPool.GetTotalPooledCount();
            var evt = EventPool.Get<SyncedTestEvent>();

            collector.RaiseEvent(evt);

            Assert.AreEqual(1, collector.Count, "Synced event must be collected");
            Assert.AreSame(evt, collector.Collected[0]);
            Assert.AreEqual(before, EventPool.GetTotalPooledCount(),
                "collected Synced event must NOT be returned by the collector — the buffer owns its return");
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            Assert.AreEqual(1, EventPool.GetOutstandingCount(),
                "Synced event stays outstanding until the buffer returns it");
#endif
        }

        // ── 3. Soak: sustained Regular raises (the SD server hot path) never accumulate ──
        //
        // Mirrors a server running many ticks where each tick raises several Regular events that the
        // collector drops. The leak signature is outstanding growing ~ticks×events; the fix keeps it
        // at 0 (every drop is returned in lockstep) and the pool bounded by the per-type cap. The SD
        // integration suite (ServerDrivenIntegrationTests) uses a mock TestSimulation that raises no
        // events, so this unit soak is where the no-accumulation invariant is exercised with teeth.

        [Test]
        public void Soak_SustainedRegularRaises_DoNotAccumulate()
        {
            var collector = new SyncedOnlyEventCollector();
            const int ticks = 500;
            const int eventsPerTick = 8;

            for (int t = 0; t < ticks; t++)
            {
                collector.BeginTick(t);
                for (int e = 0; e < eventsPerTick; e++)
                    collector.RaiseEvent(EventPool.Get<RegularTestEvent>());
            }

            Assert.AreEqual(0, collector.Count, "no Regular event is ever collected by the SD server collector");
            Assert.LessOrEqual(EventPool.GetTotalPooledCount(), 64,
                "pooled count stays within the per-type cap (64) regardless of tick count");
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            Assert.AreEqual(0, EventPool.GetOutstandingCount(),
                $"sustained drop+return over {ticks * eventsPerTick} events must leave outstanding at 0 — "
                + "a leak would grow it ~proportionally to the raise count");
#endif
        }
    }
}
