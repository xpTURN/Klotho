using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Snapshot data for a navigation agent.
    /// </summary>
    public struct NavAgentSnapshot
    {
        public EntityRef Entity;
        public FPVector3 Position;
        public FPVector3 Destination;
        public bool HasDestination;
        public bool HasPath;
        public int CurrentTriangleIndex;
    }

    /// <summary>
    /// Interface for collecting navigation agent snapshots.
    /// </summary>
    public interface INavAgentSnapshotProvider
    {
        void CollectSnapshots(NavAgentSnapshot[] buffer, out int count);
    }
}
