using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Implemented by systems that hold deterministic state outside of Frame.
    /// EcsSimulation calls this automatically during snapshot save/restore.
    /// </summary>
    public interface ISnapshotParticipant
    {
        int GetSnapshotSize();
        void SaveSnapshot(ref SpanWriter writer);
        void RestoreSnapshot(ref SpanReader reader);
    }
}
