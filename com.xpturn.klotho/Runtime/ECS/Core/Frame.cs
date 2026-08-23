using System;
using xpTURN.Klotho.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    // All component storages for the frame state are laid out in a single byte[] heap.
    // CopyFrom copies the entire state with a single Buffer.BlockCopy,
    // and serialization/hashing/deserialization is performed via ComponentStorageRegistry's dispatch table.
    public class Frame
    {
        public int Tick;
        public int DeltaTimeMs;
        public ISimulationEventRaiser EventRaiser;

        public Action<EntityRef> OnEntityCreated;
        public Action<EntityRef> OnEntityDestroyed;

        // Component signals. Both are set together by EcsSimulation.Initialize on the live frame only;
        // every other Frame instance (ring slots, the sync-test buffer) is a state container that never
        // runs systems, keeps these null, and therefore fires nothing.
        internal IComponentSignalSink SignalSink;
        internal ComponentSignalMasks SignalMasks;

        public EntityManager Entities { get; private set; }
        public EntityPrototypeRegistry Prototypes { get; } = new EntityPrototypeRegistry();
        public IDataAssetRegistry AssetRegistry { get; private set; }

        private readonly IKLogger _logger;
        private readonly int _maxEntities;

        private readonly byte[] _heap;
        private readonly int _heapSize;

        private bool[] _deserializedTypeFlags;

        public IKLogger Logger => _logger;
        public int MaxEntities => _maxEntities;

        // The assetRegistry parameter accepts any implementation that inherits IDataAssetRegistry.
        public Frame(int maxEntities, IKLogger logger, IDataAssetRegistry assetRegistry = null)
        {
            _logger = logger;
            _maxEntities = maxEntities;
            AssetRegistry = assetRegistry ?? new DataAssetRegistry();
            Entities = new EntityManager(maxEntities);

            // Determine heap size using the layout provided by the Registry. On the first call, the layout is frozen at this point.
            ComponentStorageRegistry.EnsureLayoutComputed(maxEntities);
            _heapSize = ComponentStorageRegistry.GetHeapSize(maxEntities);
            _heap = new byte[_heapSize];
            // Initialize the sparse array to -1 so that entity 0 does not pass Has checks as a false positive.
            InitializeAllStorages();
        }

        public void SetRegistry(IDataAssetRegistry registry) => AssetRegistry = registry;

        // --- Asset lookup ---

        public T GetAsset<T>(int id) where T : IDataAsset
            => AssetRegistry.Get<T>(id);

        public T GetAsset<T>(DataAssetRef assetRef) where T : IDataAsset
            => AssetRegistry.Get<T>(assetRef.Id);

        public bool TryGetAsset<T>(DataAssetRef assetRef, out T result) where T : IDataAsset
            => AssetRegistry.TryGet<T>(assetRef.Id, out result);

        // --- Component access API (signature unchanged, return type replaced with ComponentStorageFlat<T>) ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComponentStorageFlat<T> GetStorage<T>() where T : unmanaged, IComponent
        {
            // Pass TypeIdCache<T>.Layout directly as an in argument — avoids intermediate struct copy
            ComponentStorageRegistry.GetTypeId<T>();   // Populate Id+Layout cache on first call
            // Reservation-pruning guard: a pruned type has a default layout (TotalSize 0) whose
            // CountOffset 0 aliases heap[0] (the first reserved type's count) — reading it would be a silent
            // misread, not a clean crash. Fail fast at this single chokepoint (covers Add/Get/Has/GetSingleton/
            // Filter). Release-active: the aliasing hazard exists in release and the check is one int compare.
            if (ComponentStorageRegistry.TypeIdCache<T>.Layout.TotalSize == 0)
                ThrowPrunedComponent<T>();
            return new ComponentStorageFlat<T>(_heap,
                in ComponentStorageRegistry.TypeIdCache<T>.Layout);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowPrunedComponent<T>() where T : unmanaged, IComponent
            => throw new InvalidOperationException(
                $"Component {typeof(T).Name}(typeId={ComponentStorageRegistry.TypeIdCache<T>.Id}) is registered " +
                $"but excluded from this simulation's reserved set (denylisted). " +
                $"Remove it from ISimulationConfig.PrunedComponentTypeIds, or drop the Add/Get/Filter access.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get<T>(EntityRef entity) where T : unmanaged, IComponent
            => ref GetStorage<T>().Get(entity.Index);

        public ref readonly T GetReadOnly<T>(EntityRef entity) where T : unmanaged, IComponent
            => ref GetStorage<T>().GetReadOnly(entity.Index);

        public bool Has<T>(EntityRef entity) where T : unmanaged, IComponent
            => GetStorage<T>().Has(entity.Index);

        // Reads the live entity count for a component type by typeId, without a generic parameter.
        // Reads the same bytes as ComponentStorageFlat<T>.Count (same CountOffset). Used by the
        // component-memory diagnostics to tally every type in a typeId loop. Read-only.
        public int GetLiveCount(int typeId)
        {
            // GetLayout has no bounds check (=> ref _layouts[typeId]); guard out-of-range before it.
            // (uint) cast folds the negative case in. This guard must precede the TotalSize check.
            if ((uint)typeId > (uint)ComponentStorageRegistry.MaxTypeId) return 0;
            ref readonly var l = ref ComponentStorageRegistry.GetLayout(typeId);
            // In-range gap (unregistered slot) has a default layout whose CountOffset (0) aliases the
            // first registered type's count — must skip, not read.
            if (l.TotalSize == 0) return 0;
            return MemoryMarshal.Cast<byte, int>(_heap.AsSpan(l.CountOffset, 4))[0];
        }

        // --- Singleton component access ---
        // Intended for components marked [KlothoSingletonComponent], whose engine-side
        // invariant guarantees exactly one entity carrier at the relevant lifecycle window.

        public ref T GetSingleton<T>() where T : unmanaged, IComponent
        {
            var storage = GetStorage<T>();
            if (storage.Count == 0)
                throw new InvalidOperationException(
                    $"GetSingleton<{typeof(T).Name}>: no entity carries this component");
            return ref storage.ComponentsSpan[0];
        }

        public ref readonly T GetReadOnlySingleton<T>() where T : unmanaged, IComponent
        {
            var storage = GetStorage<T>();
            if (storage.Count == 0)
                throw new InvalidOperationException(
                    $"GetReadOnlySingleton<{typeof(T).Name}>: no entity carries this component");
            return ref storage.ComponentsSpan[0];
        }

        public bool TryGetSingleton<T>(out EntityRef entity) where T : unmanaged, IComponent
        {
            var storage = GetStorage<T>();
            if (storage.Count == 0)
            {
                entity = default;
                return false;
            }
            int entityIndex = storage.DenseSpan[0];
            entity = new EntityRef(entityIndex, Entities.GetVersion(entityIndex));
            return true;
        }

        public void Add<T>(EntityRef entity, T component) where T : unmanaged, IComponent
        {
            var storage = GetStorage<T>();

            // Singleton guard: a component marked [KlothoSingletonComponent] may have at most one entity carrier.
            // TypeIdCache<T>.IsSingleton is populated lazily in GetTypeId<T> (called via GetStorage<T> above).
            if (ComponentStorageRegistry.TypeIdCache<T>.IsSingleton && storage.Count > 0)
                throw new InvalidOperationException(
                    $"Duplicate singleton component {typeof(T).Name} on entity {entity.Index}; existing entity is already present");

            storage.Add(entity.Index, component);

            // Built-in: auto-initialize PreviousPosition / PreviousRotation when a TransformComponent
            // is added with PreviousInitialized=false (default). Caller-side ref-set after
            // CreateEntity(prototypeId) is not reachable here — handle via RefreshPreviousTransform.
            if (typeof(T) == typeof(TransformComponent))
            {
                ref var t = ref Get<TransformComponent>(entity);
                if (!t.PreviousInitialized)
                {
                    t.PreviousPosition = t.Position;
                    t.PreviousRotation = t.Rotation;
                    t.PreviousInitialized = true;
                }
            }

            // ISignalOnComponentAdded<T>. Fires after the insert, with a ref into the storage slot, so a
            // listener can adjust what was just added — which is also why a listener must not add/remove
            // components itself (a swap-back would move the slot this ref points at).
            var masks = SignalMasks;
            if (masks != null && masks.Any)
            {
                int typeId = ComponentStorageRegistry.TypeIdCache<T>.Id;
                if ((uint)typeId < (uint)masks.Added.Length && masks.Added[typeId])
                {
                    var self = this;   // Frame is a class: `ref this` does not exist
                    SignalSink.OnComponentAdded(ref self, entity, ref Get<T>(entity));
                }
            }
        }

        public void Remove<T>(EntityRef entity) where T : unmanaged, IComponent
        {
            var storage = GetStorage<T>();

            // ISignalOnComponentRemoved<T>. Fires before the removal and by value — after Remove the slot
            // is gone (and swap-back may have overwritten it), so the listener could not read it.
            var masks = SignalMasks;
            if (masks != null && masks.Any)
            {
                int typeId = ComponentStorageRegistry.TypeIdCache<T>.Id;
                if ((uint)typeId < (uint)masks.Removed.Length && masks.Removed[typeId]
                    && storage.Has(entity.Index))
                {
                    T removed = storage.GetReadOnly(entity.Index);
                    var self = this;   // Frame is a class: `ref this` does not exist
                    SignalSink.OnComponentRemoved(ref self, entity, removed);
                }
            }

            storage.Remove(entity.Index);
        }

        // Forces PreviousPosition / PreviousRotation to sync with the current Position / Rotation
        // and marks PreviousInitialized=true. Use after caller-side ref-set of Position to suppress
        // a 1-frame interpolation from the stale Previous*. Equivalent to the manual snap
        // (PreviousPosition = Position; PreviousRotation = Rotation;) in one named call.
        public void RefreshPreviousTransform(EntityRef entity)
        {
            ref var t = ref Get<TransformComponent>(entity);
            t.PreviousPosition = t.Position;
            t.PreviousRotation = t.Rotation;
            t.PreviousInitialized = true;
        }

        // --- Entity lifecycle ---

        public EntityRef CreateEntity()
        {
            var entity = Entities.Create();
            OnEntityCreated?.Invoke(entity);
            return entity;
        }

        public EntityRef CreateEntity(int prototypeId)
        {
            return Prototypes.Create(prototypeId, this);
        }

        // Typed prototype overload — lets callers carry spawn data on the prototype struct
        // instead of post-create ref-set. EntityPrototypeRegistry is bypassed (no dictionary
        // lookup); semantics match the registered path otherwise.
        public EntityRef CreateEntity<TPrototype>(in TPrototype prototype)
            where TPrototype : struct, IEntityPrototype
        {
            var entity = CreateEntity();
            prototype.Apply(this, entity);
            return entity;
        }

        public void DestroyEntity(EntityRef entity)
        {
            // Component-removed signals first, then the entity-destroyed one: "these components are
            // going away" before "this entity is going away" is the order that reads correctly (and the
            // one Entitas uses). Entities.Destroy stays last, so IsAlive is true throughout — but Has/Get
            // are not: each component is removed right after its own OnRemoved, so a listener reads only
            // higher typeIds and IEntityDestroyedSystem reads none.
            //
            // A throwing listener propagates and leaves the entity half-destroyed. That is accepted, not
            // handled: nothing in the engine or the server catches a tick exception (the only catch is
            // the one protecting OnFrameBoundary subscribers), so there is no execution left to observe
            // the partial state. A listener that throws is a bug and the match ends there.
            RemoveAllComponents(entity);
            OnEntityDestroyed?.Invoke(entity);
            Entities.Destroy(entity);
        }

        // --- Editor debug (via IStorageReflector dispatch) ---

        /// <summary>
        /// Returns a reflector view for editor debugging of the given component type.
        /// </summary>
        /// <param name="componentType">The component type to look up</param>
        /// <param name="view">A valid view on success, <c>default</c> on failure</param>
        /// <returns><c>true</c> if the type is registered in the Registry, otherwise <c>false</c></returns>
        /// <remarks>
        /// Follows the TryGet convention — do not access out <paramref name="view"/> when the return value is <c>false</c>.
        /// Violating this causes a NullReferenceException because the internal reflector of <c>default(ReflectableView)</c> is <c>null</c>.
        /// This API is editor-only (boxing is permitted). Do not use in runtime paths.
        /// </remarks>
        public bool TryGetReflectableStorage(Type componentType, out ReflectableView view)
        {
            if (!ComponentStorageRegistry.TryGetReflector(componentType, out var reflector))
            {
                view = default;
                return false;
            }
            int typeId = ComponentStorageRegistry.GetTypeId(componentType);
            view = new ReflectableView(_heap, ComponentStorageRegistry.GetLayout(typeId), reflector);
            return true;
        }

        public int GetAllLiveEntities(EntityRef[] output)
        {
            int count = 0;
            for (int i = 0; i < Entities.UsedSlotCount; i++)
            {
                if (!Entities.IsAlive(i)) continue;
                output[count++] = new EntityRef(i, Entities.GetVersion(i));
            }
            return count;
        }

        // --- Filters (entity queries) — signature unchanged, only internal storage type replaced ---

        public Filter<T1> Filter<T1>() where T1 : unmanaged, IComponent
            => new Filter<T1>(GetStorage<T1>(), Entities);

        public Filter<T1, T2> Filter<T1, T2>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            => new Filter<T1, T2>(GetStorage<T1>(), GetStorage<T2>(), Entities);

        public Filter<T1, T2, T3> Filter<T1, T2, T3>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            => new Filter<T1, T2, T3>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), Entities);

        public Filter<T1, T2, T3, T4> Filter<T1, T2, T3, T4>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
            => new Filter<T1, T2, T3, T4>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), GetStorage<T4>(), Entities);

        public Filter<T1, T2, T3, T4, T5> Filter<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
            where T5 : unmanaged, IComponent
            => new Filter<T1, T2, T3, T4, T5>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), GetStorage<T4>(), GetStorage<T5>(), Entities);

        public FilterWithout<T1, TExclude> FilterWithout<T1, TExclude>()
            where T1 : unmanaged, IComponent
            where TExclude : unmanaged, IComponent
            => new FilterWithout<T1, TExclude>(GetStorage<T1>(), GetStorage<TExclude>(), Entities);

        public FilterWithout<T1, T2, TExclude> FilterWithout<T1, T2, TExclude>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where TExclude : unmanaged, IComponent
            => new FilterWithout<T1, T2, TExclude>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<TExclude>(), Entities);

        public FilterWithout<T1, T2, T3, TExclude> FilterWithout<T1, T2, T3, TExclude>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where TExclude : unmanaged, IComponent
            => new FilterWithout<T1, T2, T3, TExclude>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), GetStorage<TExclude>(), Entities);

        public FilterWithout<T1, T2, T3, T4, TExclude> FilterWithout<T1, T2, T3, T4, TExclude>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
            where TExclude : unmanaged, IComponent
            => new FilterWithout<T1, T2, T3, T4, TExclude>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), GetStorage<T4>(), GetStorage<TExclude>(), Entities);

        public FilterWithout<T1, T2, T3, T4, T5, TExclude> FilterWithout<T1, T2, T3, T4, T5, TExclude>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
            where T5 : unmanaged, IComponent
            where TExclude : unmanaged, IComponent
            => new FilterWithout<T1, T2, T3, T4, T5, TExclude>(GetStorage<T1>(), GetStorage<T2>(), GetStorage<T3>(), GetStorage<T4>(), GetStorage<T5>(), GetStorage<TExclude>(), Entities);

        // --- Hash (via dispatch, iterating typeId in ascending order) ---

        // === [KlothoCleanup] tick-end passes (engine-internal; called from SystemRunner) ===

        /// <summary>
        /// Clears every CleanupMode.RemoveComponent storage. No iteration: each type is emptied through
        /// its type-erased ClearDelegate (count=0 + sparse fill), so cost is O(sparse) per type and
        /// there is no registration-order dependency (the typeId list is ascending).
        /// </summary>
        internal void RunCleanupClear()
        {
            var typeIds = ComponentStorageRegistry.CleanupClearTypeIds;
            var masks = SignalMasks;
            bool anyRemovedListener = masks != null && masks.Any;

            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                // A listener on a one-tick component has to see it go, otherwise the most natural pairing
                // in the engine ([KlothoCleanup] + a reaction to it) is the one that silently does
                // nothing. The cost lands only on types someone listens to: those walk dense (O(n)) and
                // must fire BEFORE the clear, because afterwards the values are gone. Every other type
                // keeps the single clear-dispatch call and stays O(sparse).
                if (anyRemovedListener
                    && (uint)typeId < (uint)masks.Removed.Length && masks.Removed[typeId])
                {
                    var dispatch = ComponentStorageRegistry.GetRemovedSignalDispatch(typeId);
                    if (dispatch != null)
                    {
                        var dense = MemoryMarshal.Cast<byte, int>(
                            _heap.AsSpan(layout.DenseOffset, layout.SlotCapacity * 4));
                        int liveCount = MemoryMarshal.Cast<byte, int>(_heap.AsSpan(layout.CountOffset, 4))[0];

                        for (int d = 0; d < liveCount; d++)
                        {
                            int entityIndex = dense[d];
                            if (Entities.IsAlive(entityIndex))
                                dispatch(this, new EntityRef(entityIndex, Entities.GetVersion(entityIndex)));
                        }
                    }
                }

                ComponentStorageRegistry.GetClearDispatch(typeId)(_heap, in layout);
            }
        }

        /// <summary>
        /// Destroys every entity carrying a CleanupMode.DestroyEntity component. Two passes over a
        /// caller-owned buffer (no allocation): collect in dense order, then destroy. Collect-then-apply
        /// is mandatory — destroying mid-scan swap-backs the dense array under the cursor.
        /// </summary>
        /// <remarks>
        /// The IsAlive check is load-bearing, not defensive: one entity may carry two cleanup markers,
        /// and Frame.DestroyEntity only guards at its third step, so a second call would re-fire
        /// OnEntityDestroyed and re-run RemoveAllComponents.
        /// </remarks>
        internal void RunCleanupDestroy(EntityRef[] buffer)
        {
            var typeIds = ComponentStorageRegistry.CleanupDestroyTypeIds;
            int count = 0;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                var dense = MemoryMarshal.Cast<byte, int>(
                    _heap.AsSpan(layout.DenseOffset, layout.SlotCapacity * 4));
                int liveCount = MemoryMarshal.Cast<byte, int>(_heap.AsSpan(layout.CountOffset, 4))[0];

                for (int d = 0; d < liveCount && count < buffer.Length; d++)
                {
                    int entityIndex = dense[d];
                    if (Entities.IsAlive(entityIndex))
                        buffer[count++] = new EntityRef(entityIndex, Entities.GetVersion(entityIndex));
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (Entities.IsAlive(buffer[i]))
                    DestroyEntity(buffer[i]);
            }
        }

        public ulong CalculateHash() => CalculateHash(null);

        // Layered fold. Each component type is hashed from a fresh FNV seed so its per-type value is
        // independently reproducible (matches LogComponentHashes and the offline diff tool), then folded
        // into the running frame hash. Byte-hash work is identical to the previous chained fold; the only
        // extra work is one ulong fold per type (~30) and, when a breakdown is supplied, writing the
        // per-type hash/count. When breakdown is non-null the per-type hashes and counts are recorded as a
        // byproduct. This changes the scalar hash value vs the previous chain, which is safe under the
        // same-build-per-match deployment model. SerializeWithHash folds identically so that
        // GetStateHash() stays equal to the FullState-serialization hash used at resync verification.
        //
        // CONSEQUENCE — the registered type SET is hash input. A type with zero live instances still
        // folds in its fresh seed (FNV_OFFSET), so two peers that register DIFFERENT type sets cannot
        // produce equal hashes even with identical worlds. The chained fold did not have this property:
        // an empty type contributed no bytes and was invisible. "Same build per match" therefore has to
        // hold at the level of which ASSEMBLIES are loaded, not just which game code shipped — the case
        // that bit us was a Unity Editor session registering Editor-only test assemblies' components
        // against a dedicated server that has none of them (8 extra empty types, every hash divergent
        // from tick 0). Peers must either load the same set or prune the difference
        // (ISimulationConfig.SetRuntimePrunedComponentTypeIds — a pruned type leaves
        // RegisteredTypeIdsSorted and so leaves this fold). ComponentStorageRegistry.LayoutFingerprint
        // exists to make that mismatch legible: it is logged at boot on every peer and compared at join,
        // so compare it before reading the per-component breakdown below.
        public ulong CalculateHash(StateHashBreakdown breakdown)
        {
            ulong frameHash = FPHash.FNV_OFFSET;
            frameHash = FPHash.Hash(frameHash, Tick);
            frameHash = FPHash.Hash(frameHash, Entities.Count);

            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            breakdown?.EnsureComponentCapacity(typeIds.Length);
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                ulong h = FPHash.FNV_OFFSET;
                ComponentStorageRegistry.GetHashDispatch(typeId)(_heap, in layout, Entities, ref h);
                frameHash = FPHash.Hash(frameHash, h);

                if (breakdown != null)
                {
                    breakdown.ComponentHashes[i] = h;
                    breakdown.ComponentCounts[i] = MemoryMarshal.Cast<byte, int>(_heap.AsSpan(layout.CountOffset, 4))[0];
                }
            }

            if (breakdown != null)
                breakdown.FrameHash = frameHash;

            return frameHash;
        }

        // Diagnostic — per-typeId hash and count breakdown for desync investigation.
        // Default Debug level is for steady-state monitoring (filtered out in normal logs);
        // pass a higher level (e.g. Information/Error) for desync / determinism failure events.
        public void LogComponentHashes(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Debug)
        {
            if (logger == null) return;

            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            logger.KLog(logLevel, $"[CompHash][{label}] tick={Tick} entityCount={Entities.Count} ---");
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                int count = MemoryMarshal.Cast<byte, int>(_heap.AsSpan(layout.CountOffset, 4))[0];

                ulong hash = FPHash.FNV_OFFSET;
                ComponentStorageRegistry.GetHashDispatch(typeId)(_heap, in layout, Entities, ref hash);

                var typeName = ComponentStorageRegistry.GetType(typeId)?.Name ?? "?";
                logger.KLog(logLevel, $"[CompHash][{label}] typeId={typeId} type={typeName} count={count} hash=0x{hash:X16}");
            }
        }

        // --- Snapshot / Rollback ---

        public void CopyFrom(Frame source)
        {
            Tick = source.Tick;
            DeltaTimeMs = source.DeltaTimeMs;
            Entities.CopyFrom(source.Entities);
            // No sparse re-initialization — source's sparse (-1 or valid) is duplicated via BlockCopy
            Buffer.BlockCopy(source._heap, 0, _heap, 0, _heapSize);   // single memcpy
            // Not copied: EventRaiser / OnEntityCreated / OnEntityDestroyed (delegates, not shared across ring slots)
            //             SignalSink / SignalMasks (only the executing frame has them — see ISignal.cs)
            //             AssetRegistry / Prototypes (session-wide shared readonly references)
        }

        public void Clear()
        {
            Tick = 0;
            DeltaTimeMs = 0;
            Entities.Clear();
            Array.Clear(_heap, 0, _heapSize);
            InitializeAllStorages();   // Re-initialize sparse[]=-1
        }

        // Frame ctor / Clear performs sparse Fill(-1).
        // Calls each typeId's ClearDelegate to record count=0 + sparse=-1.
        private void InitializeAllStorages()
        {
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                ComponentStorageRegistry.GetClearDispatch(typeId)(_heap, in layout);
            }
        }

        // Removes all components of a specific entity — for the DestroyEntity path.
        // Performs swap-back at the heap byte level regardless of type (no T generic required).
        internal void RemoveAllComponents(EntityRef entity)
        {
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            var masks = SignalMasks;
            bool anyRemovedListener = masks != null && masks.Any;

            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];

                // ISignalOnComponentRemoved<T> for the types someone listens to. Before the removal,
                // because the delegate reads the value out of the slot. The gate keeps this to one array
                // read per registered type when no listener exists — the loop itself is not new cost,
                // it already walked every registered type.
                if (anyRemovedListener
                    && (uint)typeId < (uint)masks.Removed.Length && masks.Removed[typeId])
                {
                    ComponentStorageRegistry.GetRemovedSignalDispatch(typeId)?.Invoke(this, entity);
                }

                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                RemoveFromStorage(_heap, in layout, entity.Index);
            }
        }

        // Generic-free remove — byte-level swap-back without type information.
        // Same semantics as ComponentStorageFlat<T>.Remove, without exposing T.
        private static void RemoveFromStorage(byte[] heap, in StorageLayout layout, int entityIndex)
        {
            if ((uint)entityIndex >= (uint)layout.Capacity) return;

            var sparse = MemoryMarshal.Cast<byte, int>(heap.AsSpan(layout.SparseOffset, layout.Capacity * 4));
            int denseIdx = sparse[entityIndex];
            if (denseIdx < 0) return;   // not present

            ref int countRef = ref MemoryMarshal.Cast<byte, int>(heap.AsSpan(layout.CountOffset, 4))[0];
            int count = countRef;
            if (denseIdx >= count) return;   // guard against stale sparse

            var dense = MemoryMarshal.Cast<byte, int>(heap.AsSpan(layout.DenseOffset, layout.SlotCapacity * 4));
            if (dense[denseIdx] != entityIndex) return;   // sparse-dense mismatch

            int lastIdx = count - 1;
            if (denseIdx < lastIdx)
            {
                int lastEntity = dense[lastIdx];
                dense[denseIdx] = lastEntity;
                // swap-back components: byte-level copy (4-byte aligned under Pack=4)
                int compSize = layout.ComponentSize;
                Buffer.BlockCopy(heap, layout.ComponentsOffset + lastIdx * compSize,
                                 heap, layout.ComponentsOffset + denseIdx * compSize, compSize);
                sparse[lastEntity] = denseIdx;
            }
            sparse[entityIndex] = -1;
            countRef = lastIdx;
        }

        // --- Network serialization (full state resync, via dispatch) ---

        public int EstimateSerializedSize()
        {
            // Size of Tick(4) + DeltaTimeMs(4) + EntityManager + storageCount(4)
            int size = 4 + 4 + Entities.GetSerializedSize() + 4;

            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                int count = MemoryMarshal.Cast<byte, int>(_heap.AsSpan(layout.CountOffset, 4))[0];
                int perSize = ComponentStorageRegistry.PerComponentSize[typeId];
                // typeId(4) + count(4) + perComponentSize(4) + dense(count*4) + components(count*perSize)
                size += 4 + 4 + 4 + count * (4 + perSize);
            }

            return size;
        }

        public byte[] SerializeTo()
        {
            int estimatedSize = EstimateSerializedSize();
            byte[] pooledBuffer = StreamPool.GetBuffer(estimatedSize);
            try
            {
                var writer = new SpanWriter(pooledBuffer);

                var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
                writer.WriteInt32(Tick);
                writer.WriteInt32(DeltaTimeMs);
                Entities.Serialize(ref writer);
                writer.WriteInt32(typeIds.Length);

                for (int i = 0; i < typeIds.Length; i++)
                {
                    int typeId = typeIds[i];
                    ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                    writer.WriteInt32(typeId);
                    ComponentStorageRegistry.GetSerializeDispatch(typeId)(_heap, in layout, ref writer);
                }

                var result = new byte[writer.Position];
                Buffer.BlockCopy(pooledBuffer, 0, result, 0, writer.Position);
                return result;
            }
            finally
            {
                StreamPool.ReturnBuffer(pooledBuffer);
            }
        }

        public (byte[] data, ulong hash) SerializeToWithHash()
        {
            int estimatedSize = EstimateSerializedSize();
            byte[] pooledBuffer = StreamPool.GetBuffer(estimatedSize);
            try
            {
                var writer = new SpanWriter(pooledBuffer);
                ulong hash = SerializeWithHash(ref writer);

                var result = new byte[writer.Position];
                Buffer.BlockCopy(pooledBuffer, 0, result, 0, writer.Position);
                return (result, hash);
            }
            finally
            {
                StreamPool.ReturnBuffer(pooledBuffer);
            }
        }

        /// <summary>
        /// Serializes directly to an external buffer while computing the hash.
        /// The writer can start at any desired offset of a buffer prepared by the caller.
        /// </summary>
        public ulong SerializeWithHash(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteInt32(DeltaTimeMs);

            // Must fold identically to CalculateHash (per-type from a fresh FNV seed, then fold)
            // so the returned hash equals CalculateHash(), keeping GetStateHash() == the FullState hash
            // verified after a resync restore. That symmetry also carries CalculateHash's consequence:
            // the registered type set is hash input, so a peer with extra empty types cannot verify a
            // FullState from a peer without them — applying the authoritative state does not reconcile
            // it, because the difference is in the fold set rather than in the values.
            ulong frameHash = FPHash.FNV_OFFSET;
            frameHash = FPHash.Hash(frameHash, Tick);
            frameHash = FPHash.Hash(frameHash, Entities.Count);

            Entities.Serialize(ref writer);
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            writer.WriteInt32(typeIds.Length);

            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                writer.WriteInt32(typeId);
                ulong h = FPHash.FNV_OFFSET;
                ComponentStorageRegistry.GetSerializeAndHashDispatch(typeId)(
                    _heap, in layout, Entities, ref writer, ref h);
                frameHash = FPHash.Hash(frameHash, h);
            }

            return frameHash;
        }

        public void DeserializeFrom(byte[] data)
        {
            var reader = new SpanReader(data);

            Tick = reader.ReadInt32();
            DeltaTimeMs = reader.ReadInt32();
            Entities.Deserialize(ref reader);

            int storageCount = reader.ReadInt32();

            int maxTypeId = ComponentStorageRegistry.MaxTypeId;
            if (_deserializedTypeFlags == null || _deserializedTypeFlags.Length <= maxTypeId)
                _deserializedTypeFlags = new bool[maxTypeId + 1];
            else
                Array.Clear(_deserializedTypeFlags, 0, _deserializedTypeFlags.Length);

            for (int i = 0; i < storageCount; i++)
            {
                int typeId = reader.ReadInt32();

                if ((uint)typeId > (uint)maxTypeId)
                {
                    // Unknown typeId — skip the data
                    int count = reader.ReadInt32();
                    int componentSize = reader.ReadInt32();
                    reader.ReadRawBytes(count * 4);              // dense
                    reader.ReadRawBytes(count * componentSize);  // components
                    _logger?.KError($"[Frame] DeserializeFrom: Unknown component typeId={typeId}, skipped");
                    continue;
                }

                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                if (layout.TypeId == 0)
                {
                    // Registration slot is empty (typeId exists in Registry but layout was not created) — skip the data
                    int count = reader.ReadInt32();
                    int componentSize = reader.ReadInt32();
                    reader.ReadRawBytes(count * 4);
                    reader.ReadRawBytes(count * componentSize);
                    _logger?.KError($"[Frame] DeserializeFrom: typeId={typeId} has no layout, skipped");
                    continue;
                }

                _deserializedTypeFlags[typeId] = true;
                ComponentStorageRegistry.GetDeserializeDispatch(typeId)(_heap, in layout, ref reader);
            }

            // Clear local storages not present in received data (rollback safety)
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                if (_deserializedTypeFlags[typeId]) continue;
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                ComponentStorageRegistry.GetClearDispatch(typeId)(_heap, in layout);
            }
        }
    }
}
