using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    // === Per-typeId feature dispatch delegate types ===
    public delegate void HashDelegate(
        byte[] heap, in StorageLayout layout,
        EntityManager entities, ref ulong hash);

    public delegate void SerializeDelegate(
        byte[] heap, in StorageLayout layout,
        ref SpanWriter writer);

    public delegate void SerializeAndHashDelegate(
        byte[] heap, in StorageLayout layout,
        EntityManager entities, ref SpanWriter writer, ref ulong hash);

    public delegate void DeserializeDelegate(
        byte[] heap, in StorageLayout layout,
        ref SpanReader reader);

    public delegate void ClearDelegate(
        byte[] heap, in StorageLayout layout);

    public static class ComponentStorageRegistry
    {
        // === Upper-bound constant ===
        // typeId upper bound. Fixes the dispatch array size to prevent out-of-range access in Register<T>.
        // The generator must respect this limit when allocating typeIds.
        public const int MAX_COMPONENT_TYPES = 10000;

        private static readonly Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();
        private static readonly Dictionary<int, Type> _idToType = new Dictionary<int, Type>();

        private static readonly Dictionary<(Type componentType, string fieldName), Func<object, int[]>>
            _fixedArrayReaders = new Dictionary<(Type, string), Func<object, int[]>>();

        // === Layout + freeze state ===
        private static StorageLayout[] _layouts;
        private static int _heapSize;
        private static long _layoutFingerprint;
        private static int _computedMaxEntities = -1;
        private static bool _frozen = false;
        // Config-supplied per-type slot-cap overrides (typeId→maxCount). Process-global layout input:
        // must be uniform across all concurrent sessions/rooms in the process (same as maxEntities).
        // null/empty = fall back to each type's attribute-declared cap.
        private static IReadOnlyDictionary<int, int> _maxCountOverrides;

        // === Reservation pruning ===
        // Raw config denylist (typeIds to skip reserving); process-global layout input like
        // _maxCountOverrides. null/empty = no pruning (all registered types reserved = current behavior).
        private static IReadOnlyCollection<int> _prunedComponentTypeIds;
        // Effective prune-set = (denylist − core base-set), computed at freeze. null = no pruning.
        // The layout loop skips typeIds in this set; subtracting core guarantees [KlothoCoreComponent]
        // types are never pruned even if a game denylist lists them.
        private static HashSet<int> _prunedSet;
        // typeIds carrying [KlothoCoreComponent] (registration-derived, self-declaring core base-set).
        // Retained across ResetForRecompute (like dispatch tables); populated in Register.
        private static readonly HashSet<int> _coreTypeIds = new HashSet<int>();

        private static readonly Dictionary<Type, int> _componentSizes = new Dictionary<Type, int>();

        // Used to accelerate Frame's per-tick hash/serialize/deserialize loops. Built once at freeze time.
        // Uses a sorted array so only registered typeIds are iterated, even if MaxTypeId is large.
        private static int[] _registeredTypeIdsSorted = Array.Empty<int>();
        internal static ReadOnlySpan<int> RegisteredTypeIdsSorted => _registeredTypeIdsSorted;

        // === Dispatch tables (statically initialized at MAX_COMPONENT_TYPES size) ===
        private static readonly HashDelegate[]             _hashDispatch             = new HashDelegate[MAX_COMPONENT_TYPES];
        private static readonly SerializeDelegate[]        _serializeDispatch        = new SerializeDelegate[MAX_COMPONENT_TYPES];
        private static readonly SerializeAndHashDelegate[] _serializeAndHashDispatch = new SerializeAndHashDelegate[MAX_COMPONENT_TYPES];
        private static readonly DeserializeDelegate[]      _deserializeDispatch      = new DeserializeDelegate[MAX_COMPONENT_TYPES];
        private static readonly ClearDelegate[]            _clearDispatch            = new ClearDelegate[MAX_COMPONENT_TYPES];
        public  static readonly int[]                      PerComponentSize          = new int[MAX_COMPONENT_TYPES];
        private static readonly bool[]                     _isSingleton              = new bool[MAX_COMPONENT_TYPES];
        private static readonly int[]                      _maxCount                 = new int[MAX_COMPONENT_TYPES];

        // === Editor-debug reflector dispatch ===
        private static readonly IStorageReflector[] _reflectors = new IStorageReflector[MAX_COMPONENT_TYPES];

        // === Generic static cache ===
        // ResetForTesting increments _generation, invalidating all caches in bulk through a CachedGeneration mismatch.
        private static int _generation = 0;

        public static class TypeIdCache<T> where T : unmanaged, IComponent
        {
            public static int Id;
            public static StorageLayout Layout;
            public static bool IsSingleton;
            public static int CachedGeneration = -1;   // -1 : not yet populated
        }

        public static int MaxTypeId { get; private set; }

        public static IReadOnlyCollection<Type> RegisteredTypes => _typeToId.Keys;

        // === Test/debug convenience properties ===
        public static bool IsFrozen => _frozen;
        public static int  RegisteredTypeCount => _typeToId.Count;

        public static void RegisterFixedArrayReader(Type componentType, string fieldName, Func<object, int[]> reader)
            => _fixedArrayReaders[(componentType, fieldName)] = reader;

        public static bool TryGetFixedArrayReader(Type componentType, string fieldName, out Func<object, int[]> reader)
            => _fixedArrayReaders.TryGetValue((componentType, fieldName), out reader);

        public static bool Register<T>(int typeId, bool isSingleton = false, int maxCount = 0, bool core = false) where T : unmanaged, IComponent
        {
            if (_frozen)
                throw new InvalidOperationException(
                    $"ComponentStorageRegistry frozen. Cannot register {typeof(T).Name} " +
                    $"(typeId={typeId}) post-freeze.");

            if ((uint)typeId >= MAX_COMPONENT_TYPES)
                throw new ArgumentOutOfRangeException(
                    nameof(typeId),
                    $"typeId={typeId} exceeds MAX_COMPONENT_TYPES={MAX_COMPONENT_TYPES}");

            if (_idToType.TryGetValue(typeId, out var existingType) && existingType != typeof(T))
                System.Diagnostics.Debug.Assert(false,
                    $"[ComponentStorageRegistry] typeId collision: {typeId} already registered as {existingType.Name}, overwriting with {typeof(T).Name}");
            if (_typeToId.TryGetValue(typeof(T), out var existingId) && existingId != typeId)
                System.Diagnostics.Debug.Assert(false,
                    $"[ComponentStorageRegistry] type re-registered with different id: {typeof(T).Name} was {existingId}, now {typeId}");

            _typeToId[typeof(T)] = typeId;
            _idToType[typeId] = typeof(T);
            _componentSizes[typeof(T)] = KUnsafe.SizeOf<T>();
            _isSingleton[typeId] = isSingleton;
            _maxCount[typeId] = maxCount;
            if (core) _coreTypeIds.Add(typeId);   // self-declaring core base-set (never pruned)
            if (typeId > MaxTypeId) MaxTypeId = typeId;

            RegisterDispatch<T>(typeId);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSingleton(int typeId)
            => (uint)typeId < MAX_COMPONENT_TYPES && _isSingleton[typeId];

        // Registers the dispatch entry, reflector, and PerComponentSize for the given typeId in one go.
        private static void RegisterDispatch<T>(int typeId) where T : unmanaged, IComponent
        {
            PerComponentSize[typeId] = default(T).GetSerializedSize();
            _reflectors[typeId]      = new ComponentReflector<T>();

            _hashDispatch[typeId] = (byte[] heap, in StorageLayout l, EntityManager e, ref ulong h) =>
            {
                var storage = new ComponentStorageFlat<T>(heap, in l);
                int count = storage.Count;
                for (int i = 0; i < count; i++)
                {
                    int entityIdx = storage.DenseSpan[i];
                    if (e.IsAlive(entityIdx))
                        h = storage.ComponentsSpan[i].GetHash(h);
                }
            };

            _serializeDispatch[typeId] = (byte[] heap, in StorageLayout l, ref SpanWriter w) =>
            {
                var storage = new ComponentStorageFlat<T>(heap, in l);
                int count = storage.Count;
                int perSize = count > 0 ? PerComponentSize[l.TypeId] : 0;
                w.WriteInt32(count);
                w.WriteInt32(perSize);
                if (count > 0)
                {
                    w.WriteRawBytes(MemoryMarshal.AsBytes(storage.DenseSpan.Slice(0, count)));
                    for (int i = 0; i < count; i++)
                        storage.ComponentsSpan[i].Serialize(ref w);
                }
            };

            _serializeAndHashDispatch[typeId] = (byte[] heap, in StorageLayout l, EntityManager e, ref SpanWriter w, ref ulong h) =>
            {
                var storage = new ComponentStorageFlat<T>(heap, in l);
                int count = storage.Count;
                int perSize = count > 0 ? PerComponentSize[l.TypeId] : 0;
                w.WriteInt32(count);
                w.WriteInt32(perSize);
                if (count > 0)
                {
                    w.WriteRawBytes(MemoryMarshal.AsBytes(storage.DenseSpan.Slice(0, count)));
                    for (int i = 0; i < count; i++)
                    {
                        storage.ComponentsSpan[i].Serialize(ref w);
                        if (e.IsAlive(storage.DenseSpan[i]))
                            h = storage.ComponentsSpan[i].GetHash(h);
                    }
                }
            };

            _deserializeDispatch[typeId] = (byte[] heap, in StorageLayout l, ref SpanReader r) =>
            {
                var storage = new ComponentStorageFlat<T>(heap, in l);
                storage.Clear();   // count=0 + sparse Fill(-1) — byte-for-byte compatible

                int count = r.ReadInt32();
                int perSize = r.ReadInt32();
                if (count == 0) return;

                var denseBytes = r.ReadRawBytes(count * sizeof(int));
                var denseTarget = storage.DenseSpan.Slice(0, count);
                denseBytes.CopyTo(MemoryMarshal.AsBytes(denseTarget));

                // Rebuild sparse reverse mapping
                var sparse = storage.SparseSpan;
                for (int i = 0; i < count; i++)
                    sparse[denseTarget[i]] = i;

                // Per-field component deserialization (explicit ref — avoid value copy)
                for (int i = 0; i < count; i++)
                {
                    ref T comp = ref storage.ComponentsSpan[i];
                    comp.Deserialize(ref r);
                }

                storage.CountRef = count;
            };

            _clearDispatch[typeId] = (byte[] heap, in StorageLayout l) =>
            {
                MemoryMarshal.Cast<byte, int>(heap.AsSpan(l.CountOffset, 4))[0] = 0;
                var sparse = MemoryMarshal.Cast<byte, int>(heap.AsSpan(l.SparseOffset, l.Capacity * 4));
                sparse.Fill(-1);
            };
        }

        // === Dispatch accessor (used internally by Frame and others) ===
        internal static HashDelegate             GetHashDispatch(int typeId)             => _hashDispatch[typeId];
        internal static SerializeDelegate        GetSerializeDispatch(int typeId)        => _serializeDispatch[typeId];
        internal static SerializeAndHashDelegate GetSerializeAndHashDispatch(int typeId) => _serializeAndHashDispatch[typeId];
        internal static DeserializeDelegate      GetDeserializeDispatch(int typeId)      => _deserializeDispatch[typeId];
        internal static ClearDelegate            GetClearDispatch(int typeId)            => _clearDispatch[typeId];

        public static bool TryGetReflector(Type componentType, out IStorageReflector reflector)
        {
            if (!_typeToId.TryGetValue(componentType, out int id))
            {
                reflector = null;
                return false;
            }
            reflector = _reflectors[id];
            return reflector != null;
        }

        public static int GetTypeId(Type type)
        {
            if (_typeToId.TryGetValue(type, out var id))
                return id;

            EnsureAllAssembliesScanned();
            return _typeToId[type];
        }

        // Generic static-cache path. Populates Id/Layout on the first call; subsequent calls are O(1).
        // When ResetForTesting bumps _generation, the CachedGeneration mismatch forces re-population.
        public static int GetTypeId<T>() where T : unmanaged, IComponent
        {
            if (TypeIdCache<T>.CachedGeneration != _generation)
            {
                if (!_typeToId.TryGetValue(typeof(T), out int id))
                {
                    EnsureAllAssembliesScanned();
                    if (!_typeToId.TryGetValue(typeof(T), out id))
                        throw new InvalidOperationException(
                            $"Component type {typeof(T).Name} not registered. " +
                            $"Ensure [KlothoComponent] attribute + typeId assignment via KlothoGenerator.");
                }
                TypeIdCache<T>.Id     = id;
                if (_layouts != null && id < _layouts.Length)
                    TypeIdCache<T>.Layout = _layouts[id];
                TypeIdCache<T>.IsSingleton = _isSingleton[id];
                TypeIdCache<T>.CachedGeneration = _generation;
            }
            return TypeIdCache<T>.Id;
        }

        public static Type GetType(int typeId)
        {
            if (_idToType.TryGetValue(typeId, out var t))
                return t;

            EnsureAllAssembliesScanned();
            return _idToType.TryGetValue(typeId, out t) ? t : null;
        }

        // === Layout computation + freeze transition ===

        // No-override entry point (Frame ctor, GetHeapSize): re-affirms the currently-set overrides AND
        // prune-set so a post-freeze Frame construction stays idempotent (never clobbers them to null).
        public static void EnsureLayoutComputed(int maxEntities)
            => EnsureLayoutComputed(maxEntities, _maxCountOverrides, _prunedComponentTypeIds);

        // Applies config-supplied per-type slot-cap overrides (typeId→maxCount) and the reservation-pruning
        // denylist (typeIds to skip reserving; null/empty = prune nothing = current behavior). Both are
        // process-global layout inputs (like maxEntities) — all concurrent sessions/rooms in a process must
        // pass equal values. A different value while frozen recomputes (editor/test) or throws (release);
        // for concurrent live rooms that would corrupt/abort, so source it once at server bootstrap,
        // uniform. Call before any Frame is built.
        public static void EnsureLayoutComputed(int maxEntities, IReadOnlyDictionary<int, int> maxCountOverrides,
                                                IReadOnlyCollection<int> prunedComponentTypeIds = null)
        {
            if (_frozen)
            {
                // Compare the EFFECTIVE prune-set (denylist − core), not the raw list: BuildEffectivePrunedSet
                // is non-injective (null / empty / core-only all collapse to null), so two rooms passing
                // different-but-equivalent raw lists must not spuriously conflict. _coreTypeIds is already
                // populated (first call scanned), so the incoming set is computable here.
                if (_computedMaxEntities == maxEntities && OverridesEqual(_maxCountOverrides, maxCountOverrides)
                    && PrunedSetEqual(_prunedSet, BuildEffectivePrunedSet(prunedComponentTypeIds)))
                    return;   // idempotent
                // Automatically relax cross-fixture freeze conflicts, so unit tests can construct
                // Frames with different maxEntities per fixture without every one of them having
                // to reset first. Gated at RUNTIME rather than by #if: the compile-time form made
                // the suite behave differently in Debug and Release, which is how 170 Release
                // failures went unnoticed behind "Release is just red". See AllowLayoutRecompute.
                if (AllowLayoutRecompute)
                {
                    ResetForRecompute();
                }
                else
                {
                    throw new InvalidOperationException(
                        $"ComponentStorageRegistry frozen at maxEntities={_computedMaxEntities}, " +
                        $"attempted EnsureLayoutComputed({maxEntities}) with a different maxEntities, " +
                        $"maxCount-override map, or prune-set. Layout cannot be recomputed within a session " +
                        $"(these must be process-uniform). " +
                        $"Call ResetForTesting() (test code only) if session reset is required.");
                }
            }

            _maxCountOverrides = maxCountOverrides;
            _prunedComponentTypeIds = prunedComponentTypeIds;

            EnsureAllAssembliesScanned();

            // After scanning: _coreTypeIds is fully populated (registrars ran), so the subtraction is complete.
            // Effective prune-set = (game denylist − core base-set); null = no pruning (reserve all).
            _prunedSet = BuildEffectivePrunedSet(prunedComponentTypeIds);

            _layouts = new StorageLayout[MaxTypeId + 1];
            var registeredList = new List<int>();
            int offset = 0;
            for (int typeId = 1; typeId <= MaxTypeId; typeId++)
            {
                if (!_idToType.TryGetValue(typeId, out var type)) continue;
                // Reservation pruning: skip typeIds inside the effective prune-set (denylist − core).
                // A skipped typeId keeps _layouts[typeId]=default (TotalSize 0) and stays out of
                // _registeredTypeIdsSorted → auto-elided from heap/serialize/hash/clear loops.
                if (_prunedSet != null && _prunedSet.Contains(typeId)) continue;
                int componentSize = _componentSizes[type];

                // Sparse is entity-indexed (maxEntities); singletons hold at most one carrier,
                // so dense/components shrink to a single slot. Non-singletons resolve a per-type
                // maxCount cap (0 = unspecified = maxEntities); the singleton branch takes priority.
                int slotCapacity = _isSingleton[typeId] ? 1 : ResolveSlotCapacity(typeId, maxEntities);

                int countOffset      = offset;
                int sparseOffset     = AlignTo(countOffset + 4, kAlignment);
                int denseOffset      = AlignTo(sparseOffset + maxEntities * 4, kAlignment);
                int componentsOffset = AlignTo(denseOffset + slotCapacity * 4, kAlignment);
                int end              = componentsOffset + slotCapacity * componentSize;

                _layouts[typeId] = new StorageLayout(typeId, maxEntities, slotCapacity, countOffset, sparseOffset,
                                                     denseOffset, componentsOffset, componentSize,
                                                     end - countOffset);
                offset = AlignTo(end, kAlignment);   // Next storage boundary (Pack=4 mandate → 4-byte sufficient)
                registeredList.Add(typeId);
            }
            _heapSize = offset;
            _registeredTypeIdsSorted = registeredList.ToArray();   // Guaranteed ascending typeId order (loop order)
            _computedMaxEntities = maxEntities;
            _layoutFingerprint = ComputeLayoutFingerprint(maxEntities);
            _frozen = true;
            // Invalidate TypeIdCache<T> populated before the layout existed. A typeId resolved pre-compute
            // (e.g. SimulationConfig.PruneComponent<T> at bootstrap, when _layouts was null) cached a
            // default(0) layout; without this bump GetTypeId<T> would keep that stale layout after freeze and
            // the GetStorage pruned-type guard would falsely reject it. Bumping here forces a lazy refresh on next GetTypeId.
            _generation++;

            // Diagnostic logging at freeze time — first-line defense against missing ModuleInitializers.
            // The Runtime asmdef has noEngineReferences=true → UnityEngine.Debug is unavailable.
            // System.Diagnostics.Trace is used instead (also surfaces on the Unity Console via a Trace listener).
#if UNITY_EDITOR || DEBUG
            Trace.WriteLine(
                $"[ComponentStorageRegistry] Frozen at maxEntities={maxEntities}, " +
                $"registered types={_typeToId.Count}, heapSize={_heapSize}");
#endif
        }

        /// <summary>
        /// Fingerprint of the frozen layout: which component types are registered, and how each is
        /// sized. Peers whose fingerprints differ CANNOT produce equal state hashes —
        /// <c>Frame.CalculateHash</c> folds every registered type, including the ones with zero
        /// instances, so the registered SET is itself hash input. This property therefore adds no
        /// constraint; it names one that was previously visible only as an unexplained hash
        /// difference. The motivating case: a Unity Editor session registers Editor-only test
        /// assemblies' components, which a player build does not have, so Editor and player build
        /// can never agree on a state hash even with an identical world.
        /// Compare this between peers before chasing a state-hash divergence.
        ///
        /// Historical note: the chained hash fold that preceded the layered one did NOT have
        /// this property — an empty type contributed no bytes and was invisible, so a registry difference
        /// stayed harmless until the day it silently became fatal. That is why the fingerprint is logged
        /// unconditionally rather than behind a diagnostic flag.
        /// </summary>
        public static long LayoutFingerprint => _layoutFingerprint;

        /// <summary>
        /// Number of component types in the frozen LAYOUT — i.e. the set that
        /// <c>Frame.CalculateHash</c> folds. Differs from <see cref="RegisteredTypeCount"/>, which
        /// counts every scanned type including reservation-pruned ones.
        /// </summary>
        public static int LayoutTypeCount => _registeredTypeIdsSorted?.Length ?? 0;

        private static long ComputeLayoutFingerprint(int maxEntities)
        {
            ulong h = FPHash.FNV_OFFSET;
            h = FPHash.Hash(h, maxEntities);
            h = FPHash.Hash(h, _registeredTypeIdsSorted.Length);
            for (int i = 0; i < _registeredTypeIdsSorted.Length; i++)
            {
                int typeId = _registeredTypeIdsSorted[i];
                ref readonly StorageLayout layout = ref _layouts[typeId];
                h = FPHash.Hash(h, typeId);
                h = FPHash.Hash(h, HashTypeName(_idToType[typeId].FullName));
                h = FPHash.Hash(h, layout.SlotCapacity);
                h = FPHash.Hash(h, layout.ComponentSize);
            }
            return (long)h;
        }

        /// <summary>
        /// Explicit FNV over the characters. NOT string.GetHashCode(): that is randomized per
        /// process, which would make two runs of the SAME build report different fingerprints.
        /// </summary>
        private static ulong HashTypeName(string name)
        {
            ulong h = FPHash.FNV_OFFSET;
            for (int i = 0; i < name.Length; i++)
                h = FPHash.Hash(h, (int)name[i]);
            return h;
        }

        public static ref readonly StorageLayout GetLayout(int typeId) => ref _layouts[typeId];

        public static int GetHeapSize(int maxEntities)
        {
            EnsureLayoutComputed(maxEntities);
            return _heapSize;
        }

        /// <summary>
        /// Whether a conflicting <see cref="EnsureLayoutComputed"/> may silently recompute the
        /// layout instead of throwing. Test/editor convenience only.
        /// <para>Defaults ON for editor, Unity-test and DEBUG builds — the configurations a test
        /// runner uses — and OFF everywhere else, which reproduces exactly what the old
        /// <c>#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEBUG</c> did for shipping code. What it
        /// adds is a way for a Release TEST run to opt in, which the compile-time form could not
        /// offer: the symbol is evaluated when this assembly is built, so a Release `dotnet test`
        /// linked against a Release runtime got the shipping behaviour and 170 fixtures failed on
        /// a freeze conflict they never hit in Debug.</para>
        /// <para><c>internal</c>, so only the assemblies named in InternalsVisibleTo can set it.
        /// A shipping process has no way to turn it on and still throws.</para>
        /// </summary>
        internal static bool AllowLayoutRecompute =
#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEBUG
            true;
#else
            false;
#endif

        // === Test-only reset ===
        // Reachable only through InternalsVisibleTo (Runtime's AssemblyInfo.cs names the test and
        // editor assemblies), which is what keeps it out of shipping code.
        //
        // Deliberately NOT [Conditional]. It used to carry UNITY_EDITOR/UNITY_INCLUDE_TESTS/DEBUG,
        // and [Conditional] removes the CALL, not the method — so in a Release `dotnet test` every
        // `ResetForTesting()` in a fixture silently became a no-op. That did not fail loudly: it
        // left the registry frozen from whichever test ran first, and the freeze guard in
        // EnsureLayoutComputed then threw for 146 of the 149 Release failures. Worse, one test
        // (Fingerprint_IsStable_ForTheSameLayout) still PASSED, because the reset that was
        // supposed to force a from-scratch recompute vanished and it ended up comparing a frozen
        // fingerprint with itself — asserting nothing, in the exact place that asserts the
        // fingerprint is a pure function of its inputs rather than of process state.
        //
        // Determinism is protected by the two things that remain build-independent: the method is
        // internal, and the automatic freeze relaxation below stays editor/test/debug-only, so a
        // shipping build still throws on a conflicting EnsureLayoutComputed.
        internal static void ResetForTesting()
        {
            ResetForRecompute();
        }

        // Automatic cross-fixture freeze relaxation path, limited to editor/test/debug builds.
        // Called directly when EnsureLayoutComputed is re-invoked with a different maxEntities.
        // Kept separate from ResetForTesting: that one is an explicit test action allowed in any
        // build, this one is an implicit rescue that must never happen in a shipping process.
        private static void ResetForRecompute()
        {
            _computedMaxEntities = -1;
            _layoutFingerprint = 0;
            _frozen = false;
            _layouts = null;
            _heapSize = 0;
            _maxCountOverrides = null;   // next EnsureLayoutComputed re-supplies; prevents cross-fixture leak
            _prunedComponentTypeIds = null;   // next EnsureLayoutComputed re-supplies (same reason)
            _prunedSet = null;
            _registeredTypeIdsSorted = Array.Empty<int>();
            _generation++;   // Bulk-invalidate TypeIdCache<T>
            // _typeToId / _idToType / _componentSizes / dispatch tables / _coreTypeIds are retained (ModuleInitializer-based)
        }

        // Pack=4 mandate → all storage and components are 4-byte aligned.
        // When switching to Pack=8, modify only this constant to 8 (conditional on dropping ARMv7 support).
        private const int kAlignment = 4;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignTo(int offset, int alignment) =>
            (offset + (alignment - 1)) & ~(alignment - 1);

        // Resolves the dense/components slot cap for a non-singleton type (singletons are handled at the
        // call site and never reach here). Priority: config override map > build-baked per-type _maxCount
        // (0 = unspecified) > maxEntities. Every source clamps to maxEntities.
        private static int ResolveSlotCapacity(int typeId, int maxEntities)
        {
            // Config override takes priority over the build-baked attribute cap; both clamp to maxEntities.
            if (_maxCountOverrides != null && _maxCountOverrides.TryGetValue(typeId, out int ov) && ov > 0)
                return Math.Min(ov, maxEntities);
            int mc = _maxCount[typeId];
            return mc > 0 ? Math.Min(mc, maxEntities) : maxEntities;
        }

        // Content-equality for the override map; null and empty are equal (both = "no overrides").
        private static bool OverridesEqual(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b)
        {
            if (ReferenceEquals(a, b)) return true;
            int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
            if (ca != cb) return false;
            if (ca == 0) return true;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out int v) || v != kv.Value) return false;
            return true;
        }

        // Content-equality for the effective prune-set; null and empty are equal (both = "no pruning").
        // Order-independent (compares as sets) — the prune-set is a membership declaration, not a sequence.
        private static bool PrunedSetEqual(IReadOnlyCollection<int> a, IReadOnlyCollection<int> b)
        {
            if (ReferenceEquals(a, b)) return true;
            int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
            if (ca != cb) return false;
            if (ca == 0) return true;
            var set = new HashSet<int>(a);
            foreach (var id in b)
                if (!set.Contains(id)) return false;
            return true;
        }

        // Builds the effective prune-set: null/empty denylist ⇒ null (no pruning, reserve all registered
        // types). Otherwise (denylist − core base-set) so [KlothoCoreComponent] types are never pruned even
        // if a game denylist lists them (force-exclude); if the subtraction empties the set, returns null.
        // Call only after EnsureAllAssembliesScanned so _coreTypeIds is fully populated (the core typeIds are
        // registered during that scan).
        private static HashSet<int> BuildEffectivePrunedSet(IReadOnlyCollection<int> prunedComponentTypeIds)
        {
            if (prunedComponentTypeIds == null || prunedComponentTypeIds.Count == 0)
                return null;
            var set = new HashSet<int>(prunedComponentTypeIds);
            set.ExceptWith(_coreTypeIds);   // core is force-excluded (never pruned)
            return set.Count == 0 ? null : set;
        }

        private static void EnsureAllAssembliesScanned()
        {
            Core.ModuleInitializerHelper.EnsureAll();
        }
    }
}
