using System.Collections.Generic;

namespace xpTURN.Klotho.State
{
    /// <summary>
    /// Game state snapshot interface
    /// Used to save/restore state for rollback
    /// </summary>
    public interface IStateSnapshot
    {
        /// <summary>
        /// Snapshot tick number
        /// </summary>
        int Tick { get; }

        /// <summary>
        /// Serializes the snapshot data into a byte array
        /// </summary>
        byte[] Serialize();

        /// <summary>
        /// Restores the snapshot from a byte array
        /// </summary>
        void Deserialize(byte[] data);

        /// <summary>
        /// Computes the state hash
        /// </summary>
        ulong CalculateHash();
    }

    /// <summary>
    /// State snapshot manager interface
    /// </summary>
    public interface IStateSnapshotManager
    {
        /// <summary>
        /// Maximum number of snapshots to retain
        /// </summary>
        int MaxSnapshots { get; set; }

        /// <summary>
        /// Saves the state snapshot for the current tick
        /// </summary>
        void SaveSnapshot(int tick, IStateSnapshot snapshot);

        /// <summary>
        /// Retrieves the snapshot for a specific tick
        /// </summary>
        IStateSnapshot GetSnapshot(int tick);

        /// <summary>
        /// Checks whether a snapshot exists for a specific tick
        /// </summary>
        bool HasSnapshot(int tick);

        /// <summary>
        /// Removes all snapshots after a specific tick (used during rollback)
        /// </summary>
        void ClearSnapshotsAfter(int tick);

        /// <summary>
        /// Removes all snapshots
        /// </summary>
        void ClearAll();

        /// <summary>
        /// List of stored snapshot ticks
        /// </summary>
        IEnumerable<int> SavedTicks { get; }

        /// <summary>
        /// Fills the output list with stored snapshot ticks (GC-free)
        /// </summary>
        void GetSavedTicks(IList<int> output);
    }
}
