using System;
using System.Collections.Generic;
using System.Diagnostics;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    public class SystemRunner
    {
        private struct SystemEntry
        {
            public object System;
            public SystemPhase Phase;
            public int Order;
        }

        private readonly List<SystemEntry> _entries = new List<SystemEntry>();
        private SystemEntry[] _sorted;
        private bool _dirty = true;
        private int _nextOrder;

        // --- Per-system perf monitor (opt-in, off by default; determinism-neutral, zero-cost when off) ---
        // Index convention: stat[0] is the builtin SavePrevTransforms pseudo-entry and stat[1..] are the
        // ISystem entries in _sorted order. RebindPerf and the instrumentation loop both walk _sorted
        // with the same `is ISystem` filter, so these consts are the single source of that mapping and
        // keep the two loops from drifting out of alignment.
        private const int BuiltinPrevXformIndex = 0;
        private const int FirstSystemPerfIndex  = 1;

        private Diagnostics.SystemPerfMonitor _perfMonitor;      // null = off (hard gate)
        private bool _perfBindDirty;                             // rebind on next RunUpdateSystems
        private string[] _perfNames = Array.Empty<string>();    // RebindPerf reuse buffer

        public void AddSystem(object system, SystemPhase phase)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            _entries.Add(new SystemEntry
            {
                System = system,
                Phase = phase,
                Order = _nextOrder++
            });
            _dirty = true;
        }

        private void EnsureSorted()
        {
            if (!_dirty) return;

            _sorted = _entries.ToArray();
            Array.Sort(_sorted, (a, b) =>
            {
                int cmp = ((int)a.Phase).CompareTo((int)b.Phase);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            _dirty = false;
            // AddSystem sets _dirty only; the perf monitor must rebind after a re-sort or its
            // index-parallel stats misalign. This is the only re-sort site.
            if (_perfMonitor != null) _perfBindDirty = true;
        }

        /// <summary>
        /// Returns the first registered system instance assignable to <typeparamref name="T"/>,
        /// or <c>null</c> if none. Type parameter must be a reference type (class or interface).
        /// Traversal order matches registration order.
        /// Lookup is O(N) over registered systems; cache the result if called on a hot path.
        /// </summary>
        public T Find<T>() where T : class
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].System is T match)
                    return match;
            }
            return null;
        }

        /// <summary>
        /// Appends all registered system instances assignable to <typeparamref name="T"/>
        /// into <paramref name="buffer"/>. Returns the count appended.
        /// Caller owns the buffer (alloc-free for the lookup itself).
        /// </summary>
        public int FindAll<T>(List<T> buffer) where T : class
        {
            int initial = buffer.Count;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].System is T match)
                    buffer.Add(match);
            }
            return buffer.Count - initial;
        }

        public void Init(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IInitSystem init)
                    init.OnInit(ref frame);
            }
        }

        public void Destroy(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IDestroySystem destroy)
                    destroy.OnDestroy(ref frame);
            }
        }

        public void RunUpdateSystems(ref Frame frame)
        {
            EnsureSorted();
            // Built-in: capture previous transform before any PreUpdate system runs.
            // Relies on SystemPhase.PreUpdate == 0 so this placement is equivalent to
            // running ahead of the first PreUpdate-phase ISystem after EnsureSorted ordering.
            Debug.Assert((int)SystemPhase.PreUpdate == 0,
                "SystemPhase.PreUpdate must remain the first enum value (0). " +
                "If the enum order changes, move SaveAllPreviousTransforms accordingly.");

            if (_perfMonitor == null)
            {
                // Default path — no measurement, no Stopwatch/GC calls at all (hard gate).
                SaveAllPreviousTransforms(ref frame);
                for (int i = 0; i < _sorted.Length; i++)
                {
                    if (_sorted[i].System is ISystem sys)
                        sys.Update(ref frame);
                }
                return;
            }

            // Instrumented path. Capture mem first, then time, so the GetAllocatedBytesForCurrentThread
            // call cost is not counted in the timing window (GetTimestamp does not allocate).
            if (_perfBindDirty) RebindPerf();

            long m0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            SaveAllPreviousTransforms(ref frame);
            long t1 = Stopwatch.GetTimestamp();
            _perfMonitor.Record(BuiltinPrevXformIndex,
                t1 - t0, GC.GetAllocatedBytesForCurrentThread() - m0);

            int slot = FirstSystemPerfIndex;
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISystem sys)
                {
                    long sm0 = GC.GetAllocatedBytesForCurrentThread();
                    long st0 = Stopwatch.GetTimestamp();
                    sys.Update(ref frame);
                    long st1 = Stopwatch.GetTimestamp();
                    _perfMonitor.Record(slot++, st1 - st0,
                        GC.GetAllocatedBytesForCurrentThread() - sm0);
                }
            }
        }

        /// <summary>
        /// Enables the per-system perf monitor (opt-in diagnostic). Idempotent within a session:
        /// the first RunUpdateSystems binds the sorted-system name list. Determinism-neutral —
        /// records only into the monitor's own struct array.
        /// </summary>
        public void EnablePerfMonitor(int warmupExecutions = 0)
        {
            _perfMonitor ??= new Diagnostics.SystemPerfMonitor();
            _perfMonitor.WarmupExecutions = warmupExecutions;
            _perfBindDirty = true;
        }

        /// <summary>The perf monitor, or null when the diagnostic is off.</summary>
        public Diagnostics.SystemPerfMonitor PerfMonitor => _perfMonitor;

        // Rebuild the monitor's name list from the current _sorted order (builtin entry first, then
        // ISystem entries). Called on enable and after every re-sort. The _perfNames buffer is reused;
        // Type.Name still allocates, so this is a rare-path (not steady-state) allocation.
        private void RebindPerf()
        {
            int need = 1;   // builtin
            for (int i = 0; i < _sorted.Length; i++)
                if (_sorted[i].System is ISystem) need++;
            if (_perfNames.Length < need) _perfNames = new string[need];

            _perfNames[BuiltinPrevXformIndex] = "(builtin) SavePrevTransforms";
            int slot = FirstSystemPerfIndex;
            for (int i = 0; i < _sorted.Length; i++)
                if (_sorted[i].System is ISystem s) _perfNames[slot++] = s.GetType().Name;

            _perfMonitor.Bind(new ReadOnlySpan<string>(_perfNames, 0, slot));
            _perfBindDirty = false;
        }

        private static void SaveAllPreviousTransforms(ref Frame frame)
        {
            var filter = frame.Filter<TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref var t = ref frame.Get<TransformComponent>(entity);
                t.PreviousPosition = t.Position;
                t.PreviousRotation = t.Rotation;
                t.PreviousInitialized = true;
            }
        }

        public void RunCommandSystems(ref Frame frame, ICommand command)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ICommandSystem cmdSys)
                    cmdSys.OnCommand(ref frame, command);
            }
        }

        public void OnComponentAdded<T>(ref Frame frame, EntityRef entity, ref T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentAdded<T> sys)
                    sys.OnAdded(ref frame, entity, ref component);
            }
        }

        public void OnComponentRemoved<T>(ref Frame frame, EntityRef entity, T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentRemoved<T> sys)
                    sys.OnRemoved(ref frame, entity, component);
            }
        }

        public void OnEntityCreated(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityCreatedSystem sys)
                    sys.OnEntityCreated(ref frame, entity);
            }
        }

        public void OnEntityDestroyed(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityDestroyedSystem sys)
                    sys.OnEntityDestroyed(ref frame, entity);
            }
        }

        public void EmitSyncEvents(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISyncEventSystem syncSys)
                    syncSys.EmitSyncEvents(ref frame);
            }
        }

        public void Signal<TSignal>(ref Frame frame, SignalInvoker<TSignal> invoke)
            where TSignal : class, ISignal
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is TSignal sys)
                    invoke(sys, ref frame);
            }
        }
    }
}
