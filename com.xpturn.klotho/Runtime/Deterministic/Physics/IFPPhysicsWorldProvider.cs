using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Interface that exposes the physics world's bodies, static colliders, contacts, and trigger pairs externally.
    /// </summary>
    public interface IFPPhysicsWorldProvider
    {
        void GetBodies(out FPPhysicsBody[] bodies, out int count);
        void GetStaticColliders(out FPStaticCollider[] colliders, out int count);
        void GetContacts(out FPContact[] contacts, out int count,
                         out FPContact[] staticContacts, out int staticCount);
        void GetTriggerPairs(out (int idA, int idB)[] pairs, out int count);

        /// <summary>
        /// Copied-vs-total accounting for the three views above, plus the per-kind truncation
        /// counters. The views need it because <c>count == buffer.Length</c> cannot tell a full
        /// buffer from a dropped tail.
        /// </summary>
        FPContactSnapshotStats GetSnapshotStats();
    }
}
