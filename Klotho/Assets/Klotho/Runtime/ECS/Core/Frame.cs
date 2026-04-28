using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
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

        public EntityManager Entities { get; private set; }
        public EntityPrototypeRegistry Prototypes { get; } = new EntityPrototypeRegistry();
        public IDataAssetRegistry AssetRegistry { get; private set; }

        private readonly ILogger _logger;
        private readonly int _maxEntities;

        private readonly byte[] _heap;
        private readonly int _heapSize;

        private bool[] _deserializedTypeFlags;

        public ILogger Logger => _logger;
        public int MaxEntities => _maxEntities;

        // The assetRegistry parameter accepts any implementation that inherits IDataAssetRegistry.
        public Frame(int maxEntities, ILogger logger, IDataAssetRegistry assetRegistry = null)
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
            return new ComponentStorageFlat<T>(_heap,
                in ComponentStorageRegistry.TypeIdCache<T>.Layout);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get<T>(EntityRef entity) where T : unmanaged, IComponent
            => ref GetStorage<T>().Get(entity.Index);

        public ref readonly T GetReadOnly<T>(EntityRef entity) where T : unmanaged, IComponent
            => ref GetStorage<T>().GetReadOnly(entity.Index);

        public bool Has<T>(EntityRef entity) where T : unmanaged, IComponent
            => GetStorage<T>().Has(entity.Index);

        public void Add<T>(EntityRef entity, T component) where T : unmanaged, IComponent
            => GetStorage<T>().Add(entity.Index, component);

        public void Remove<T>(EntityRef entity) where T : unmanaged, IComponent
            => GetStorage<T>().Remove(entity.Index);

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

        public void DestroyEntity(EntityRef entity)
        {
            OnEntityDestroyed?.Invoke(entity);
            RemoveAllComponents(entity.Index);
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

        public ulong CalculateHash()
        {
            ulong hash = FPHash.FNV_OFFSET;
            hash = FPHash.Hash(hash, Tick);
            hash = FPHash.Hash(hash, Entities.Count);

            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                ComponentStorageRegistry.GetHashDispatch(typeId)(_heap, in layout, Entities, ref hash);
            }

            return hash;
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
        internal void RemoveAllComponents(int entityIndex)
        {
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);
                RemoveFromStorage(_heap, in layout, entityIndex);
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

            var dense = MemoryMarshal.Cast<byte, int>(heap.AsSpan(layout.DenseOffset, layout.Capacity * 4));
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
            ulong hash = FPHash.FNV_OFFSET;

            writer.WriteInt32(Tick);
            writer.WriteInt32(DeltaTimeMs);
            hash = FPHash.Hash(hash, Tick);
            hash = FPHash.Hash(hash, Entities.Count);

            Entities.Serialize(ref writer);
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            writer.WriteInt32(typeIds.Length);

            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                writer.WriteInt32(typeId);
                ComponentStorageRegistry.GetSerializeAndHashDispatch(typeId)(
                    _heap, in layout, Entities, ref writer, ref hash);
            }

            return hash;
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
                    _logger?.ZLogError($"[Frame] DeserializeFrom: Unknown component typeId={typeId}, skipped");
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
                    _logger?.ZLogError($"[Frame] DeserializeFrom: typeId={typeId} has no layout, skipped");
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
