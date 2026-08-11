using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    // A struct view that projects a specific offset range of the byte[] heap as a Span to provide per-type storage.
    // Declared as readonly struct to avoid copies when passed by ref; internal mutation is performed through CountRef (ref int).
    public readonly struct ComponentStorageFlat<T> where T : unmanaged, IComponent
    {
        private readonly byte[] _heap;
        private readonly int _countOffset;
        private readonly int _sparseOffset;
        private readonly int _denseOffset;
        private readonly int _componentsOffset;
        private readonly int _capacity;       // entity-index domain: Sparse length + Has/Add entity bound
        private readonly int _slotCapacity;   // concurrent slots: Dense/Components length + Add overflow bound

        public ComponentStorageFlat(byte[] heap, in StorageLayout layout)
        {
            _heap = heap;
            _countOffset      = layout.CountOffset;
            _sparseOffset     = layout.SparseOffset;
            _denseOffset      = layout.DenseOffset;
            _componentsOffset = layout.ComponentsOffset;
            _capacity         = layout.Capacity;
            _slotCapacity     = layout.SlotCapacity;
        }

        public int Capacity => _capacity;

        public ref int CountRef =>
            ref MemoryMarshal.Cast<byte, int>(_heap.AsSpan(_countOffset, 4))[0];

        public int Count => CountRef;

        public Span<int> SparseSpan =>
            MemoryMarshal.Cast<byte, int>(_heap.AsSpan(_sparseOffset, _capacity * 4));

        public Span<int> DenseSpan =>
            MemoryMarshal.Cast<byte, int>(_heap.AsSpan(_denseOffset, _slotCapacity * 4));

        public Span<T> ComponentsSpan =>
            MemoryMarshal.Cast<byte, T>(_heap.AsSpan(_componentsOffset, _slotCapacity * KUnsafe.SizeOf<T>()));

        // For Filter iterators — a dense (entityIndex) view sliced to the active entity count.
        public ReadOnlySpan<int> DenseToSparse => DenseSpan.Slice(0, Count);

        // Type-erased view of Count + Sparse, for FilterMutationGuard. Reads live, so it sees the
        // compaction a Remove performs behind the span the Filter captured.
        internal ComponentStorageWatch Watch =>
            new ComponentStorageWatch(_heap, _countOffset, _sparseOffset, _capacity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int entityIndex)
        {
            if ((uint)entityIndex >= (uint)_capacity) return false;
            int denseIndex = SparseSpan[entityIndex];
            return denseIndex >= 0 && denseIndex < Count && DenseSpan[denseIndex] == entityIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(int entityIndex)
        {
            return ref ComponentsSpan[SparseSpan[entityIndex]];
        }

        public ref readonly T GetReadOnly(int entityIndex) =>
            ref ComponentsSpan[SparseSpan[entityIndex]];

        // Direct heap-slot mutation via CountRef (ref int) despite being a readonly struct.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int entityIndex, in T component)
        {
            if ((uint)entityIndex >= (uint)_capacity)
                throw new ArgumentOutOfRangeException(nameof(entityIndex));

            if (Has(entityIndex))
                throw new InvalidOperationException(
                    $"Entity {entityIndex} already has component {typeof(T).Name}");

            ref int count = ref CountRef;
            if (count >= _slotCapacity)
                throw new InvalidOperationException(
                    $"ComponentStorageFlat<{typeof(T).Name}> capacity exceeded ({_slotCapacity})");

            SparseSpan[entityIndex] = count;
            DenseSpan[count]        = entityIndex;
            ComponentsSpan[count]   = component;
            count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(int entityIndex)
        {
            if (!Has(entityIndex)) return;

            ref int count = ref CountRef;
            int denseIdx = SparseSpan[entityIndex];
            int lastIdx  = count - 1;

            if (denseIdx < lastIdx)
            {
                // swap-back: move the last slot into the removed position
                int lastEntity           = DenseSpan[lastIdx];
                DenseSpan[denseIdx]      = lastEntity;
                ComponentsSpan[denseIdx] = ComponentsSpan[lastIdx];
                SparseSpan[lastEntity]   = denseIdx;
            }

            SparseSpan[entityIndex] = -1;
            count--;
        }

        // count=0 + sparse Fill(-1). dense/components are unreachable when count=0, so zero-clear is unnecessary.
        // (Same semantics as ComponentStorageRegistry.ClearDelegate)
        public void Clear()
        {
            CountRef = 0;
            SparseSpan.Fill(-1);
        }
    }
}
