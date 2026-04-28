using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-tick event ring buffer (GC-free).
    /// Owned by KlothoEngine.
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
