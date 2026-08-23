using System;
using System.Collections.Generic;
using System.Diagnostics;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    public class SystemRunner : IComponentSignalSink
    {
        private struct SystemEntry
        {
            public object System;
            public SystemPhase Phase;
            public int Order;
            // Registration-time group label for the perf report, or null. DIAGNOSTIC ONLY: it is not a
            // sort key (EnsureSorted compares Phase then Order and nothing else), never reaches the
            // frame/state hash, and — unlike a [KlothoCleanup] mode — is deliberately NOT folded into
            // LayoutFingerprint: peers labelling their systems differently must still play together.
            public string Group;
        }

        private readonly List<SystemEntry> _entries = new List<SystemEntry>();
        private SystemEntry[] _sorted;
        private bool _dirty = true;
        private int _nextOrder;

        // --- Component-signal gate (see ISignal.cs) ---
        // Handed to the live Frame once by EcsSimulation.Initialize and mutated in place from then on, so
        // the frame's reference never goes stale. Bits are OR-ed in at AddSystem time: the entry list is
        // append-only, so the system axis is monotone and needs no invalidation. The registry generation
        // is the one axis that can move under us, and that is checked at tick boundaries (SyncSignalMasks
        // from Init / RunUpdateSystems) rather than on the Add path, which must stay a single check.
        private readonly ComponentSignalMasks _signalMasks = new ComponentSignalMasks();
        private int _signalMaskGeneration = -1;

        internal ComponentSignalMasks SignalMasks => _signalMasks;

        // --- Per-system perf monitor (opt-in, off by default; determinism-neutral, zero-cost when off) ---
        // Index convention: stat[0..2] are the builtin engine passes (SavePrevTransforms ahead of the
        // systems, then the two [KlothoCleanup] passes after them) and stat[3..] are the ISystem entries
        // in _sorted order. RebindPerf and the instrumentation loop both walk _sorted with the same
        // `is ISystem` filter, so these consts are the single source of that mapping and keep the two
        // loops from drifting out of alignment. Adding a builtin means bumping FirstSystemPerfIndex and
        // RebindPerf's `need` seed together.
        private const int BuiltinPrevXformIndex     = 0;
        private const int BuiltinCleanupClearIndex  = 1;
        private const int BuiltinCleanupDestroyIndex = 2;
        private const int BuiltinPerfSlotCount      = 3;
        private const int FirstSystemPerfIndex       = BuiltinPerfSlotCount;
        // Implicit group for the builtin passes. Their NAMES are untouched; this only makes them a
        // group in the report's totals so "sum of groups == measured total" holds.
        private const string BuiltinPerfGroup       = Diagnostics.SystemPerfMonitor.BuiltinGroup;

        private Diagnostics.SystemPerfMonitor _perfMonitor;      // null = off (hard gate)
        private bool _perfBindDirty;                             // rebind on next RunUpdateSystems
        private string[] _perfNames = Array.Empty<string>();    // RebindPerf reuse buffer
        private string[] _perfGroups = Array.Empty<string>();   // parallel to _perfNames (group labels)

        // Collect buffer for the CleanupMode.DestroyEntity pass. Instance state (one runner per
        // simulation), never static: a server process runs several matches at once. Grown once to
        // maxEntities on first use and reused, so the steady-state pass allocates nothing.
        private EntityRef[] _cleanupDestroyBuffer = Array.Empty<EntityRef>();

        /// <param name="group">
        /// Optional label that groups this system in the per-system perf report (e.g. "combat").
        /// Diagnostic only — it does not affect execution order, state, or any fingerprint. Whitespace
        /// and empty strings mean "no label"; nothing beyond trimming is validated, so two spellings
        /// ("combat" vs "Combat") are two groups.
        /// </param>
        public void AddSystem(object system, SystemPhase phase, string group = null)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            // The perf report only measures ISystem entries, so a label on anything else is silently
            // dropped. Dev builds say so rather than leaving the author guessing why the group is absent.
            Debug.Assert(group == null || system is ISystem,
                $"AddSystem group '{group}' is ignored: {system.GetType().Name} does not implement ISystem, " +
                "so it never appears in the perf report.");

            _entries.Add(new SystemEntry
            {
                System = system,
                Phase = phase,
                Order = _nextOrder++,
                Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            });
            _dirty = true;

            // A rebuild already walks every entry including this one; otherwise merge just the new system.
            if (!SyncSignalMasks())
                MergeSignalMasks(system);
        }

        // Rebuilds the signal masks when the component registry has been recomputed since the last build
        // (typeIds may have moved). Returns true when a rebuild happened. Cheap no-op otherwise: one int
        // comparison. Called from AddSystem and from the tick-boundary entry points.
        private bool SyncSignalMasks()
        {
            int generation = ComponentStorageRegistry.Generation;
            if (_signalMaskGeneration == generation)
                return false;

            _signalMaskGeneration = generation;
            _signalMasks.Reset(ComponentStorageRegistry.MaxTypeId + 1);
            for (int i = 0; i < _entries.Count; i++)
                MergeSignalMasks(_entries[i].System);
            return true;
        }

        // Reads the interfaces the system already declares — no MakeGenericType, so nothing here asks the
        // runtime to materialize a new generic instantiation (AOT-safe). A listener for a component type
        // that carries no [KlothoComponent] is skipped: test doubles do that, and it must not fail
        // registration.
        private void MergeSignalMasks(object system)
        {
            var interfaces = system.GetType().GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var iface = interfaces[i];
                if (!iface.IsGenericType) continue;

                var definition = iface.GetGenericTypeDefinition();
                bool isAdded = definition == typeof(ISignalOnComponentAdded<>);
                if (!isAdded && definition != typeof(ISignalOnComponentRemoved<>)) continue;

                if (!ComponentStorageRegistry.TryGetTypeId(iface.GetGenericArguments()[0], out int typeId))
                    continue;
                if ((uint)typeId >= (uint)_signalMasks.Added.Length)
                    continue;

                if (isAdded) _signalMasks.Added[typeId] = true;
                else         _signalMasks.Removed[typeId] = true;
                _signalMasks.Any = true;
            }
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
            SyncSignalMasks();
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
            SyncSignalMasks();
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
                RunCleanupPasses(ref frame);
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

            // Cleanup is the tail-end mirror of SaveAllPreviousTransforms: same builtin treatment, same
            // hard gate. The two modes get separate slots because their cost profiles are unrelated —
            // clear is O(sparse) per type, destroy is entities × RemoveAllComponents.
            if (ComponentStorageRegistry.CleanupClearTypeIds.Length > 0)
            {
                long cm0 = GC.GetAllocatedBytesForCurrentThread();
                long ct0 = Stopwatch.GetTimestamp();
                frame.RunCleanupClear();
                long ct1 = Stopwatch.GetTimestamp();
                _perfMonitor.Record(BuiltinCleanupClearIndex,
                    ct1 - ct0, GC.GetAllocatedBytesForCurrentThread() - cm0);
            }

            if (ComponentStorageRegistry.CleanupDestroyTypeIds.Length > 0)
            {
                long dm0 = GC.GetAllocatedBytesForCurrentThread();
                long dt0 = Stopwatch.GetTimestamp();
                frame.RunCleanupDestroy(EnsureCleanupDestroyBuffer(ref frame));
                long dt1 = Stopwatch.GetTimestamp();
                _perfMonitor.Record(BuiltinCleanupDestroyIndex,
                    dt1 - dt0, GC.GetAllocatedBytesForCurrentThread() - dm0);
            }
        }

        // Built-in tick-end passes. Skipped entirely when nothing declares [KlothoCleanup], so a game
        // that does not use the feature pays nothing (the arrays are empty at freeze).
        private void RunCleanupPasses(ref Frame frame)
        {
            if (ComponentStorageRegistry.CleanupClearTypeIds.Length > 0)
                frame.RunCleanupClear();

            if (ComponentStorageRegistry.CleanupDestroyTypeIds.Length > 0)
                frame.RunCleanupDestroy(EnsureCleanupDestroyBuffer(ref frame));
        }

        // maxEntities is the hard upper bound on how many entities the destroy pass can collect, so one
        // growth to that size makes every later pass allocation-free.
        private EntityRef[] EnsureCleanupDestroyBuffer(ref Frame frame)
        {
            int need = frame.Entities.Capacity;
            if (_cleanupDestroyBuffer.Length < need)
                _cleanupDestroyBuffer = new EntityRef[need];
            return _cleanupDestroyBuffer;
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
            int need = BuiltinPerfSlotCount;
            for (int i = 0; i < _sorted.Length; i++)
                if (_sorted[i].System is ISystem) need++;
            if (_perfNames.Length < need) _perfNames = new string[need];
            if (_perfGroups.Length < need) _perfGroups = new string[need];

            // Builtin names are left exactly as they were (tests assert them verbatim), but they DO get
            // a group so every measured row belongs to one — otherwise the group totals would never add
            // up to the report's own total.
            _perfNames[BuiltinPrevXformIndex]      = "(builtin) SavePrevTransforms";
            _perfNames[BuiltinCleanupClearIndex]   = "(builtin) CleanupClear";
            _perfNames[BuiltinCleanupDestroyIndex] = "(builtin) CleanupDestroy";
            for (int i = 0; i < BuiltinPerfSlotCount; i++) _perfGroups[i] = BuiltinPerfGroup;

            int slot = FirstSystemPerfIndex;
            for (int i = 0; i < _sorted.Length; i++)
                if (_sorted[i].System is ISystem s)
                {
                    _perfGroups[slot] = _sorted[i].Group;
                    _perfNames[slot++] = s.GetType().Name;
                }

            _perfMonitor.Bind(new ReadOnlySpan<string>(_perfNames, 0, slot),
                              new ReadOnlySpan<string>(_perfGroups, 0, slot));
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

        /// <summary>
        /// Broadcasts to every registered system implementing <typeparamref name="TSignal"/>, in
        /// execution order, invoking it through the caller-supplied delegate.
        /// </summary>
        /// <remarks>
        /// A general-purpose extension point for a game's own <see cref="ISignal"/>-derived interfaces.
        /// It is <b>not</b> how component signals are delivered: <c>ISignalOnComponentAdded/Removed</c> do
        /// not derive from <see cref="ISignal"/> and are called directly from <c>Frame.Add</c>/<c>Remove</c>
        /// behind a per-typeId gate. Two consequences — this cannot dispatch them, and the
        /// <paramref name="invoke"/> delegate typically allocates per call, so keep this off per-tick paths.
        /// </remarks>
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
