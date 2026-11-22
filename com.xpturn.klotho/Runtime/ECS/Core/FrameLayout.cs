using System;
using System.Runtime.CompilerServices;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Immutable per-simulation / per-room ECS frame heap layout computed from
    /// <c>(maxEntities, maxCountOverrides, prunedComponentTypeIds)</c> plus the registered
    /// component type set. Fixed at simulation creation and shared by the live <see cref="Frame"/>
    /// and every slot in its <see cref="FrameRingBuffer"/>; never mutated or grown during a match.
    /// </summary>
    public sealed class FrameLayout
    {
        internal readonly StorageLayout[] Layouts;
        private readonly int[] _registeredTypeIdsSorted;
        private readonly int[] _cleanupClearTypeIds;
        private readonly int[] _cleanupDestroyTypeIds;

        public int MaxEntities { get; }
        public int HeapSize { get; }
        public long LayoutFingerprint { get; }
        public int MaxTypeId => Layouts.Length - 1;
        public int LayoutTypeCount => _registeredTypeIdsSorted.Length;
        public int CleanupTypeCount => _cleanupClearTypeIds.Length + _cleanupDestroyTypeIds.Length;

        public ReadOnlySpan<int> RegisteredTypeIdsSorted => _registeredTypeIdsSorted;
        internal ReadOnlySpan<int> CleanupClearTypeIds => _cleanupClearTypeIds;
        internal ReadOnlySpan<int> CleanupDestroyTypeIds => _cleanupDestroyTypeIds;

        internal FrameLayout(
            int maxEntities,
            int heapSize,
            long layoutFingerprint,
            StorageLayout[] layouts,
            int[] registeredTypeIdsSorted,
            int[] cleanupClearTypeIds,
            int[] cleanupDestroyTypeIds)
        {
            MaxEntities = maxEntities;
            HeapSize = heapSize;
            LayoutFingerprint = layoutFingerprint;
            Layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
            _registeredTypeIdsSorted = registeredTypeIdsSorted ?? Array.Empty<int>();
            _cleanupClearTypeIds = cleanupClearTypeIds ?? Array.Empty<int>();
            _cleanupDestroyTypeIds = cleanupDestroyTypeIds ?? Array.Empty<int>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly StorageLayout GetLayout(int typeId) => ref Layouts[typeId];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasLayout(int typeId) => (uint)typeId < (uint)Layouts.Length && Layouts[typeId].TotalSize > 0;
    }
}
