using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation event object pool (GC-free).
    /// </summary>
    public static class EventPool
    {
        private static readonly Dictionary<int, Stack<SimulationEvent>> _pools = new Dictionary<int, Stack<SimulationEvent>>();
        private const int MAX_POOL_SIZE = 64;

        public static T Get<T>() where T : SimulationEvent, new()
        {
            var typeId = EventPoolTypeCache<T>.TypeId;
            if (_pools.TryGetValue(typeId, out var stack) && stack.Count > 0)
            {
                var evt = (T)stack.Pop();
                evt.Reset();
                return evt;
            }
            return new T();
        }

        public static void Return(SimulationEvent evt)
        {
            if (evt == null) return;
            if (!_pools.TryGetValue(evt.EventTypeId, out var stack))
            {
                stack = new Stack<SimulationEvent>();
                _pools[evt.EventTypeId] = stack;
            }
            if (stack.Count < MAX_POOL_SIZE)
            {
                evt.Reset();
                stack.Push(evt);
            }
        }

        public static void ClearAll()
        {
            foreach (var stack in _pools.Values)
                stack.Clear();
            _pools.Clear();
        }

        private static class EventPoolTypeCache<T> where T : SimulationEvent, new()
        {
            public static readonly int TypeId = new T().EventTypeId;
        }
    }
}
