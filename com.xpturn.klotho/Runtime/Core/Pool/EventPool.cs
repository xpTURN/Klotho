using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation event object pool (shared, lock-guarded, GC-free).
    /// </summary>
    public static class EventPool
    {
        // Shared across all threads, guarded by _gate. The SD dedicated
        // server runs each room's Update on rotating ThreadPool workers; with MaxRooms>1 two rooms tick
        // concurrently and both touch this single shared pool, so the Dictionary/Stack accesses must be
        // serialized. _gate is a leaf lock — never held across a call that re-enters EventPool or acquires
        // another lock (the diagnostic log is emitted after the lock is released).
        private static readonly object _gate = new object();

        private static readonly Dictionary<int, Stack<SimulationEvent>> _pools = new Dictionary<int, Stack<SimulationEvent>>();
        private const int MAX_POOL_SIZE = 64;

#if DEBUG || UNITY_EDITOR
        // Ownership-violation diagnostic (mirrors CommandPool). Tracks instances handed out
        // by Get<T>; Return uses it to detect non-pool-origin (game-side `new`) or double-Return inputs and
        // skip the pool insert (a double-Return would otherwise push the same instance twice → pool aliasing).
        // Shared + _gate-guarded, so cross-thread Get/Return is tracked correctly (no false positive).
        private static readonly HashSet<SimulationEvent> _outstanding = new HashSet<SimulationEvent>();

        // Logger slot for the ownership-violation diagnostic. Optional — when unset the diagnostic
        // is silent (the Return still skips the offending instance, preventing pool poisoning).
        private static IKLogger _diagnosticLogger;

        public static void SetDiagnosticLogger(IKLogger logger) => _diagnosticLogger = logger;
#endif

        public static T Get<T>() where T : SimulationEvent, new()
        {
            var typeId = EventPoolTypeCache<T>.TypeId;
            T evt;
            lock (_gate)
            {
                if (_pools.TryGetValue(typeId, out var stack) && stack.Count > 0)
                {
                    evt = (T)stack.Pop();
                    evt.Reset();
                }
                else
                {
                    evt = new T();
                }
#if DEBUG || UNITY_EDITOR
                _outstanding.Add(evt);
#endif
            }
            return evt;
        }

        public static void Return(SimulationEvent evt)
        {
            if (evt == null) return;
#if DEBUG || UNITY_EDITOR
            bool ownershipViolation;
#endif
            lock (_gate)
            {
#if DEBUG || UNITY_EDITOR
                // Non-pool-origin (game-side `new`) or double-Return — skip the pool insert so the offending
                // instance cannot poison the stack (a double-Return would otherwise push it twice → aliasing).
                // The diagnostic log is emitted after the lock.
                ownershipViolation = !_outstanding.Remove(evt);
                if (!ownershipViolation)
#endif
                {
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
                    // Pool cap overflow: instance is left to GC. _outstanding was already cleared above so
                    // no dangling tracking entry remains.
                }
            }
#if DEBUG || UNITY_EDITOR
            if (ownershipViolation)
            {
                // Emit outside the lock — logger I/O must not extend the critical section or risk a
                // lock-order inversion with the logger's own lock (_gate stays a leaf lock).
                var logger = _diagnosticLogger;
                if (logger != null)
                {
                    logger.KError($"[EventPool] Return called on non-pool instance or double-Return — likely game-side `new` or double-Return. Skipping pool insert. type={evt.GetType().Name}");
                }
            }
#endif
        }

        public static void ClearAll()
        {
            lock (_gate)
            {
                foreach (var stack in _pools.Values)
                    stack.Clear();
                _pools.Clear();
#if DEBUG || UNITY_EDITOR
                _outstanding.Clear();
#endif
            }
        }

        // Diagnostic — total count of pooled event instances across all typeIds (shared pool).
        public static int GetTotalPooledCount()
        {
            lock (_gate)
            {
                int total = 0;
                foreach (var stack in _pools.Values)
                    total += stack.Count;
                return total;
            }
        }

        // Diagnostic — count of distinct event typeIds currently held in the pool.
        public static int GetPooledTypeCount()
        {
            lock (_gate)
                return _pools.Count;
        }

#if DEBUG || UNITY_EDITOR
        // Diagnostic — count of currently outstanding (rented but not returned) instances.
        public static int GetOutstandingCount()
        {
            lock (_gate)
                return _outstanding.Count;
        }
#endif

        private static class EventPoolTypeCache<T> where T : SimulationEvent, new()
        {
            public static readonly int TypeId = new T().EventTypeId;
        }
    }
}
