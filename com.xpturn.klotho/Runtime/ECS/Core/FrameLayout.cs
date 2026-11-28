using System;
using System.Runtime.CompilerServices;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Immutable component heap layout of one simulation: per-type storage offsets computed from
    /// <c>(maxEntities, maxCount overrides, prune-set)</c> over the registered component types.
    /// <see cref="EcsSimulation"/> computes it once at construction and shares the instance with its
    /// live <see cref="Frame"/>, every rollback-ring slot and every frame made by
    /// <see cref="EcsSimulation.CreateCompatibleFrame"/>. Simulations in one process may run different
    /// layouts side by side; creating one never changes another's.
    /// </summary>
    public sealed class FrameLayout
    {
        // Indexed by typeId; a slot with TotalSize 0 is an unregistered or reservation-pruned type.
        internal readonly StorageLayout[] Layouts;
        private readonly int[] _registeredTypeIdsSorted;
        private readonly int[] _cleanupClearTypeIds;
        private readonly int[] _cleanupDestroyTypeIds;

        public int MaxEntities { get; }
        public int HeapSize { get; }

        /// <summary>
        /// Determinism fingerprint of this layout — see <see cref="ComponentStorageRegistry.LayoutFingerprint"/>
        /// for what it folds. Equal inputs give an equal value in every process.
        /// </summary>
        public long LayoutFingerprint { get; }

        public int MaxTypeId => Layouts.Length - 1;

        /// <summary>Number of component types in this layout — the set <c>Frame.CalculateHash</c> folds.</summary>
        public int LayoutTypeCount => _registeredTypeIdsSorted.Length;

        /// <summary>Number of layout types declaring a <see cref="CleanupMode"/> other than None.</summary>
        public int CleanupTypeCount => _cleanupClearTypeIds.Length + _cleanupDestroyTypeIds.Length;

        /// <summary>typeIds of this layout in ascending order (reservation-pruned types are absent).</summary>
        public ReadOnlySpan<int> RegisteredTypeIdsSorted => _registeredTypeIdsSorted;

        internal ReadOnlySpan<int> CleanupClearTypeIds => _cleanupClearTypeIds;
        internal ReadOnlySpan<int> CleanupDestroyTypeIds => _cleanupDestroyTypeIds;

        internal FrameLayout(int maxEntities, int heapSize, long layoutFingerprint, StorageLayout[] layouts,
                             int[] registeredTypeIdsSorted, int[] cleanupClearTypeIds, int[] cleanupDestroyTypeIds)
        {
            MaxEntities = maxEntities;
            HeapSize = heapSize;
            LayoutFingerprint = layoutFingerprint;
            Layouts = layouts;
            _registeredTypeIdsSorted = registeredTypeIdsSorted;
            _cleanupClearTypeIds = cleanupClearTypeIds;
            _cleanupDestroyTypeIds = cleanupDestroyTypeIds;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly StorageLayout GetLayout(int typeId) => ref Layouts[typeId];

        /// <summary>Whether this layout reserves storage for <paramref name="typeId"/> (false for pruned or unregistered types).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasLayout(int typeId) => (uint)typeId < (uint)Layouts.Length && Layouts[typeId].TotalSize > 0;
    }
}
