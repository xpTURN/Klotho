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
    /// <c>Check</c> and <c>Record</c> carry <see cref="ConditionalAttribute"/>, so those call sites are
    /// removed by the C# compiler — arguments included — and the struct is left with no fields. The
    /// remaining construction goes through <see cref="Create{T}"/> rather than the constructor, so a
    /// release build has nothing to eliminate there either (see that method).
    /// </summary>
    /// <remarks>
    /// A Filter snapshots Count and holds a span over the live dense array, while
    /// ComponentStorageFlat.Remove compacts by swapping the last dense slot into the removed one. That
    /// moves an entity the filter has not reached yet into a slot it has already passed, so the entity
    /// gets visited twice in a single pass.
    ///
    /// It is NOT a desync. Dense order is hash input (the per-type hash walks DenseSpan in dense order)
    /// and it is serialised raw, so every peer and every re-execution after a rollback restore
    /// reproduces the same double visit. What you get is an effect applied twice, with no exception, no
    /// hash mismatch and no desync log — which is precisely why a runtime check is the only thing that
    /// can see it. Docs/ECS.md §5 carries the long form.
    ///
    /// The single-type Filter&lt;T1&gt; has a second SYMPTOM rather than a second trap: with no Has
    /// re-check it can hand out an entity whose component is gone, and Frame.Get then indexes
    /// Components through a sparse slot of -1 and throws IndexOutOfRangeException — not a silent stale
    /// read. Reaching it means removing the entity sitting at the dense tail, which this guard already
    /// throws on, so in a development build the exception below comes first and the symptom is
    /// release-only.
    ///
    /// Removing the component off the entity the filter just returned — including by destroying that
    /// entity — is the one safe case: the swap lands in the slot that was just handed out, and the
    /// stale dense tail past the new Count still resolves through Has(), so the displaced entity is
    /// still visited exactly once. Systems rely on that, and it is not reported here.
    ///
    /// Additions are not reported. A new component appends at Count, which is either past the snapshot
    /// the filter took (never reached) or fills a hole an earlier removal left, and is visited at most
    /// once either way.
    ///
    /// Two shapes net the count out and pass unreported: removing one entity's component and adding
    /// another's in the same body, and removing the CURRENT entity's component then re-adding it (that
    /// second one appends over the stale dense tail, so the entity swapped into the vacated slot is
    /// skipped AND the current one is visited twice). The count is the only signal available that is
    /// not itself hashed state — a version counter would have to live in the heap, and that moves
    /// StorageLayout, the state hash and LayoutFingerprint.
    ///
    /// Nested filters over the same storage are covered but read oddly: removing the INNER loop's
    /// current entity is a genuine violation for the outer walk, so the throw surfaces in the outer
    /// Next() — a different loop, sometimes a different system, than the line that did the removal.
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
        /// Builds the guard for the storage a <c>Filter</c> picked. Takes the storage itself rather than
        /// a pre-computed <see cref="ComponentStorageWatch"/> so that a release build has nothing to
        /// eliminate: the body is empty there, the call inlines to <c>default</c>, and the watch is never
        /// materialised. <see cref="ConditionalAttribute"/> cannot do this job — it is not valid on a
        /// constructor (CS0592) and cannot go on a method that returns a value — but it is not needed.
        /// </summary>
        /// <param name="count">
        /// The Count the Filter snapshotted from the SAME storage. The assert is the only automatic
        /// check that the watch and the iterated span were taken from one storage: the selection assigns
        /// both per branch, and a mismatched pair stays invisible to arity tests unless the two storages
        /// differ in count — which is exactly when the pairing matters.
        /// </param>
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static FilterMutationGuard Create<T>(in ComponentStorageFlat<T> storage, int count)
            where T : unmanaged, IComponent
        {
            Debug.Assert(storage.Count == count,
                "FilterMutationGuard: the watched storage is not the one this Filter iterates " +
                "(Count mismatch). Check the watch/span pair in the selecting branch.");
            return new FilterMutationGuard(storage.Watch);
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static FilterMutationGuard Create<T>(in ComponentStorageFlat<T> storage, int count)
            where T : unmanaged, IComponent => default;
#endif

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

            // throw, not Debug.Assert — a deliberate departure from the convention this repo uses for
            // dev-only detection (SystemRunner.AddSystem, SystemRunner.RunUpdateSystems). The reason is
            // testability, not severity: the violation leaves no exception and no hash mismatch of its
            // own, so a regression test is the only thing that can observe it and Assert.Throws needs a
            // throw. Debug.Assert would also be narrower — it is itself [Conditional("DEBUG")], so it
            // would go silent in a Unity development player, which the three symbols here cover.
            if (!removedIteratedEntity)
                throw new InvalidOperationException(
                    "Filter iteration: a component was removed from a storage this Filter is walking, " +
                    "on an entity other than the one Next() just returned. Swap-back compaction moves an " +
                    "unvisited entity into an already-passed dense slot, so the pass visits it twice (or, " +
                    "for Filter<T1>, hands out an entity whose component is gone). Collect the entities " +
                    "during the loop and apply the removal after it. If the loop in the stack looks " +
                    "innocent, check for an inner filter over the same storage: removing ITS current " +
                    "entity is a violation for the outer walk, and the throw lands here.");

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
