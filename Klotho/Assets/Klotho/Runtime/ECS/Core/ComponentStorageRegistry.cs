using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        private static int _computedMaxEntities = -1;
        private static bool _frozen = false;
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

        // === Editor-debug reflector dispatch ===
        private static readonly IStorageReflector[] _reflectors = new IStorageReflector[MAX_COMPONENT_TYPES];

        // === Generic static cache ===
        // ResetForTesting increments _generation, invalidating all caches in bulk through a CachedGeneration mismatch.
        private static int _generation = 0;

        public static class TypeIdCache<T> where T : unmanaged, IComponent
        {
            public static int Id;
            public static StorageLayout Layout;
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

        public static bool Register<T>(int typeId) where T : unmanaged, IComponent
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
            _componentSizes[typeof(T)] = Unsafe.SizeOf<T>();
            if (typeId > MaxTypeId) MaxTypeId = typeId;

            RegisterDispatch<T>(typeId);
            return true;
        }

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

        public static void EnsureLayoutComputed(int maxEntities)
        {
            if (_frozen)
            {
                if (_computedMaxEntities == maxEntities) return;   // idempotent
#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEBUG
                // Editor/test/debug builds: automatically relax cross-fixture freeze conflicts.
                // Even when unit tests construct Frames with different maxEntities per fixture, layout is auto-recomputed.
                // In release builds the #else branch throws (to protect determinism).
                ResetForRecompute();
#else
                throw new InvalidOperationException(
                    $"ComponentStorageRegistry frozen at maxEntities={_computedMaxEntities}, " +
                    $"attempted EnsureLayoutComputed({maxEntities}). " +
                    $"Layout cannot be recomputed within a session. " +
                    $"Call ResetForTesting() (test code only) if session reset is required.");
#endif
            }

            EnsureAllAssembliesScanned();

            _layouts = new StorageLayout[MaxTypeId + 1];
            var registeredList = new List<int>();
            int offset = 0;
            for (int typeId = 1; typeId <= MaxTypeId; typeId++)
            {
                if (!_idToType.TryGetValue(typeId, out var type)) continue;
                int componentSize = _componentSizes[type];

                int countOffset      = offset;
                int sparseOffset     = AlignTo(countOffset + 4, kAlignment);
                int denseOffset      = AlignTo(sparseOffset + maxEntities * 4, kAlignment);
                int componentsOffset = AlignTo(denseOffset + maxEntities * 4, kAlignment);
                int end              = componentsOffset + maxEntities * componentSize;

                _layouts[typeId] = new StorageLayout(typeId, maxEntities, countOffset, sparseOffset,
                                                     denseOffset, componentsOffset, componentSize,
                                                     end - countOffset);
                offset = AlignTo(end, kAlignment);   // Next storage boundary (Pack=4 mandate → 4-byte sufficient)
                registeredList.Add(typeId);
            }
            _heapSize = offset;
            _registeredTypeIdsSorted = registeredList.ToArray();   // Guaranteed ascending typeId order (loop order)
            _computedMaxEntities = maxEntities;
            _frozen = true;

            // Diagnostic logging at freeze time — first-line defense against missing ModuleInitializers.
            // The Runtime asmdef has noEngineReferences=true → UnityEngine.Debug is unavailable.
            // System.Diagnostics.Trace is used instead (also surfaces on the Unity Console via a Trace listener).
#if UNITY_EDITOR || DEBUG
            Trace.WriteLine(
                $"[ComponentStorageRegistry] Frozen at maxEntities={maxEntities}, " +
                $"registered types={_typeToId.Count}, heapSize={_heapSize}");
#endif
        }

        public static ref readonly StorageLayout GetLayout(int typeId) => ref _layouts[typeId];

        public static int GetHeapSize(int maxEntities)
        {
            EnsureLayoutComputed(maxEntities);
            return _heapSize;
        }

        // === Test-only reset ===
        // Completely removed in release builds — protects determinism.
        // The Runtime asmdef AssemblyInfo.cs declares InternalsVisibleTo("xpTURN.Klotho.Tests").
        [Conditional("UNITY_EDITOR")]
        [Conditional("UNITY_INCLUDE_TESTS")]
        [Conditional("DEBUG")]
        internal static void ResetForTesting()
        {
            ResetForRecompute();
        }

        // Automatic cross-fixture freeze relaxation path, limited to editor/test/debug builds.
        // Called directly when EnsureLayoutComputed is re-invoked with a different maxEntities.
        // Separated because ResetForTesting is stripped in release via [Conditional] and cannot share its body.
        private static void ResetForRecompute()
        {
            _computedMaxEntities = -1;
            _frozen = false;
            _layouts = null;
            _heapSize = 0;
            _registeredTypeIdsSorted = Array.Empty<int>();
            _generation++;   // Bulk-invalidate TypeIdCache<T>
            // _typeToId / _idToType / _componentSizes / dispatch tables are retained (ModuleInitializer-based)
        }

        // Pack=4 mandate → all storage and components are 4-byte aligned.
        // When switching to Pack=8, modify only this constant to 8 (conditional on dropping ARMv7 support).
        private const int kAlignment = 4;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignTo(int offset, int alignment) =>
            (offset + (alignment - 1)) & ~(alignment - 1);

        private static void EnsureAllAssembliesScanned()
        {
            Core.ModuleInitializerHelper.EnsureAll();
        }
    }
}
