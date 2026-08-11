using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    // Type-erased view of one storage's Count and Sparse slots. A Filter picks the storage it walks by
    // comparing counts at construction, so the choice is not visible in its type parameters — this lets
    // the guard watch whichever one was chosen without carrying that type through.
    internal readonly struct ComponentStorageWatch
    {
        private readonly byte[] _heap;
        private readonly int _countOffset;
        private readonly int _sparseOffset;
        private readonly int _capacity;

        internal ComponentStorageWatch(byte[] heap, int countOffset, int sparseOffset, int capacity)
        {
            _heap = heap;
            _countOffset = countOffset;
            _sparseOffset = sparseOffset;
            _capacity = capacity;
        }

        internal int Count =>
            MemoryMarshal.Cast<byte, int>(_heap.AsSpan(_countOffset, 4))[0];

        // Dense index of the entity's component, or negative when it holds none.
        internal int DenseIndexOf(int entityIndex) =>
            (uint)entityIndex >= (uint)_capacity
                ? -1
                : MemoryMarshal.Cast<byte, int>(_heap.AsSpan(_sparseOffset, _capacity * 4))[entityIndex];
    }

    /// <summary>
    /// Development-build detection of structural changes to the storage a <c>Filter</c> is iterating.
    /// Both entry points carry <see cref="ConditionalAttribute"/>, so every call site compiles away in
    /// a release build and the struct is left with no fields.
    /// </summary>
    /// <remarks>
    /// A Filter snapshots Count and holds a span over the live dense array, while
    /// ComponentStorageFlat.Remove compacts by swapping the last dense slot into the removed one. That
    /// moves an entity the filter has not reached yet into a slot it has already passed, so the entity
    /// gets visited twice in a single pass — or, in the single-type Filter&lt;T1&gt; which has no Has
    /// re-check, a removed entity is visited and its stale component read. Both are silent, and in a
    /// rollback-networked simulation both desync the replay rather than crashing.
    ///
    /// Removing the component off the entity the filter just returned — including by destroying that
    /// entity — is the one safe case: the swap lands in the slot that was just handed out, and the
    /// stale dense tail past the new Count still resolves through Has(), so the displaced entity is
    /// still visited exactly once. Systems rely on that, and it is not reported here.
    ///
    /// Additions are not reported. A new component appends at Count, which is either past the snapshot
    /// the filter took (never reached) or fills a hole an earlier removal left, and is visited at most
    /// once either way.
    /// </remarks>
    internal struct FilterMutationGuard
    {
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        private ComponentStorageWatch _watch;
        private int _prevCount;
        private int _entityIndex; // entity handed out by the previous Next()
        private bool _started;
#endif

        internal FilterMutationGuard(in ComponentStorageWatch watch)
        {
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            _watch = watch;
            _prevCount = watch.Count;
            _entityIndex = 0;
            _started = false;
#endif
        }

        /// <summary>
        /// Call at the top of <c>Next()</c>, before the iterator advances, so the check sees the storage
        /// as the previous loop body left it. Must run on the pass that ends the loop too — a body that
        /// violates the rule on the final entity would otherwise go unreported.
        /// </summary>
        [Conditional("DEBUG"), Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Check()
        {
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!_started) return;

            int live = _watch.Count;
            if (live >= _prevCount)
            {
                _prevCount = live;
                return;
            }

            // Exactly one removal is allowed, and only if it took the entity just handed out — which is
            // precisely the case where that entity no longer maps to a dense slot. Anything else shrank
            // the array under a walk that has not finished.
            bool removedIteratedEntity =
                live == _prevCount - 1 && _watch.DenseIndexOf(_entityIndex) < 0;

            if (!removedIteratedEntity)
                throw new InvalidOperationException(
                    "Filter iteration: a component was removed from a storage this Filter is walking, " +
                    "on an entity other than the one Next() just returned. Swap-back compaction moves an " +
                    "unvisited entity into an already-passed dense slot, so the pass visits it twice (or, " +
                    "for Filter<T1>, visits a removed entity and reads its stale component). Collect the " +
                    "entities during the loop and apply the removal after it.");

            _prevCount = live;
#endif
        }

        [Conditional("DEBUG"), Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Record(int entityIndex)
        {
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            _entityIndex = entityIndex;
            _started = true;
#endif
        }
    }
}
