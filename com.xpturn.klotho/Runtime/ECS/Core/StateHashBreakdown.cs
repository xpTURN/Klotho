using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Layered breakdown of a simulation state hash. Pre-allocated and reused so filling it on the
    /// steady path is allocation-free. <see cref="Total"/> reproduces the scalar
    /// <see cref="EcsSimulation.GetStateHash()"/> value exactly; the per-component and per-system arrays
    /// expose the fold inputs so a desync can be localized to a component type or a snapshot
    /// participant / system instead of collapsing to a single opaque scalar.
    ///
    /// <para>Array indices are positional, NOT raw typeIds:
    /// <list type="bullet">
    /// <item><c>ComponentHashes[i]</c> / <c>ComponentCounts[i]</c> ↔
    /// <c>ComponentStorageRegistry.RegisteredTypeIdsSorted[i]</c></item>
    /// <item><c>SystemHashes[i]</c> ↔ the i-th registered <c>ISnapshotParticipant</c></item>
    /// </list>
    /// Both orderings are build-stable, so index <c>i</c> maps to the same entity on every peer of a
    /// match (same-build-per-match deployment).</para>
    /// </summary>
    public sealed class StateHashBreakdown
    {
        /// <summary>Scalar state hash — identical to <see cref="EcsSimulation.GetStateHash()"/>.</summary>
        public long Total;

        /// <summary>Frame-level hash: Tick + entity count + fold of every component-type hash.</summary>
        public ulong FrameHash;

        /// <summary>Per-component-type hash, indexed by position in RegisteredTypeIdsSorted.</summary>
        public ulong[] ComponentHashes = Array.Empty<ulong>();

        /// <summary>Live component count per type, indexed by position in RegisteredTypeIdsSorted.</summary>
        public int[] ComponentCounts = Array.Empty<int>();

        /// <summary>Per-snapshot-participant hash, indexed by registration order.</summary>
        public ulong[] SystemHashes = Array.Empty<ulong>();

        /// <summary>Grows the component arrays to <paramref name="count"/> (no-op when already sized).</summary>
        public void EnsureComponentCapacity(int count)
        {
            if (ComponentHashes.Length == count) return;
            ComponentHashes = new ulong[count];
            ComponentCounts = new int[count];
        }

        /// <summary>Grows the system array to <paramref name="count"/> (no-op when already sized).</summary>
        public void EnsureSystemCapacity(int count)
        {
            if (SystemHashes.Length == count) return;
            SystemHashes = new ulong[count];
        }
    }
}
