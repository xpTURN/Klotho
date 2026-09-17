using System.Collections.Generic;
using xpTURN.Klotho.Logging;

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

#if DEBUG || UNITY_EDITOR
        // Dev guard: last tick written to each slot. Clearing a slot whose occupant
        // is NEWER than the nominal tick destroys recent live data — the ring-wrap misuse pattern
        // (a nominal-tick range clear on a reused slot). Occupant OLDER than nominal is normal
        // wrap reuse and must not trigger.
        private readonly int[] _slotTick;
        private readonly IKLogger _logger;
#endif

        public EventBuffer(int capacity, IKLogger logger = null)
        {
            _capacity = capacity;
            _ring = new List<SimulationEvent>[capacity];
            for (int i = 0; i < capacity; i++)
                _ring[i] = new List<SimulationEvent>();

#if DEBUG || UNITY_EDITOR
            _slotTick = new int[capacity];
            for (int i = 0; i < capacity; i++)
                _slotTick[i] = -1;
            _logger = logger;
#endif
        }

#if DEBUG || UNITY_EDITOR
        /// <summary>
        /// Dev-only: the tick last written to the slot that <paramref name="tick"/> maps to,
        /// or -1 when the slot is empty. Lets the engine detect slot reuse that destroys a
        /// previous occupant's still-pending events.
        /// </summary>
        public int GetSlotOccupantTick(int tick) => _slotTick[tick % _capacity];
#endif

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
#if DEBUG || UNITY_EDITOR
            _slotTick[tick % _capacity] = tick;
#endif
        }

        public void ClearTick(int tick, bool returnToPool = true)
        {
#if DEBUG || UNITY_EDITOR
            if (_slotTick[tick % _capacity] > tick)
                _logger?.KError($"[EventBuffer] ClearTick({tick}) destroys a NEWER occupant (tick {_slotTick[tick % _capacity]}) — nominal-tick clear on a ring-wrapped slot.");
            _slotTick[tick % _capacity] = -1;
#endif
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
            // Full reset (full-state apply / teardown) — clears slots directly instead of
            // going through ClearTick: slot indices are not nominal ticks, and the dev-only
            // newer-occupant guard must not fire on a legitimate whole-buffer reset.
            for (int i = 0; i < _capacity; i++)
            {
                var list = _ring[i];
                for (int e = 0; e < list.Count; e++)
                    EventPool.Return(list[e]);
                list.Clear();
#if DEBUG || UNITY_EDITOR
                _slotTick[i] = -1;
#endif
            }
        }
    }
}
