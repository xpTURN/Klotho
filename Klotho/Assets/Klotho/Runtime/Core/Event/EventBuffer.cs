using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-tick event ring buffer (GC-free). Owned by KlothoEngine.
    ///
    /// Storage layout: <c>_ring[tick % _capacity]</c>. Slots are <see cref="List{T}"/> of
    /// <see cref="SimulationEvent"/>, so a single slot may hold multiple events for the same tick.
    /// Capacity invariant: <c>_capacity == MaxRollbackTicks + 2</c> (set by KlothoEngine at construction).
    ///
    /// Ring-wrap risk: ticks T and T + _capacity map to the same slot. Callers that need to preserve
    /// events past a stall window (e.g. predicted ticks awaiting verification) must ensure the
    /// CurrentTick lag stays below _capacity, otherwise a subsequent <see cref="ClearTick"/> on the
    /// later tick silently wipes the earlier tick's entries.
    ///
    /// Pool coupling: <see cref="ClearTick"/> returns each cleared event to <see cref="EventPool"/>
    /// for reuse. Holders of stale event references must not assume the payload remains stable after
    /// the slot is cleared — the underlying object may be reissued for a different tick.
    /// </summary>
    public class EventBuffer
    {
        private readonly int _capacity;
        private readonly List<SimulationEvent>[] _ring;

        public EventBuffer(int capacity)
        {
            _capacity = capacity;
            _ring = new List<SimulationEvent>[capacity];
            for (int i = 0; i < capacity; i++)
                _ring[i] = new List<SimulationEvent>();
        }

        public List<SimulationEvent> GetEvents(int tick)
        {
            return _ring[tick % _capacity];
        }

        /// <summary>
        /// Append <paramref name="evt"/> to slot <c>tick % _capacity</c>. No dedup by type, payload,
        /// or content hash — callers raising the same event twice for the same tick produce two
        /// entries (and two dispatches downstream). Dedup is the responsibility of the simulation
        /// raising the event, not this buffer or <see cref="EventCollector"/>.
        /// </summary>
        public void AddEvent(int tick, SimulationEvent evt)
        {
            _ring[tick % _capacity].Add(evt);
        }

        public void ClearTick(int tick, bool returnToPool = true)
        {
            var list = _ring[tick % _capacity];
            if (returnToPool)
            {
                for (int i = 0; i < list.Count; i++)
                    EventPool.Return(list[i]);
            }
            list.Clear();
        }

        public void ClearRange(int fromTick, int toTickExclusive, bool returnToPool = true)
        {
            for (int t = fromTick; t < toTickExclusive; t++)
                ClearTick(t, returnToPool);
        }

        public void ClearAll()
        {
            for (int i = 0; i < _capacity; i++)
                ClearTick(i);
        }
    }
}
