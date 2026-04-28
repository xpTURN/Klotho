using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// SyncTest verification result status.
    /// </summary>
    public enum SyncTestStatus
    {
        /// <summary>Hash matched — determinism confirmed.</summary>
        Pass,
        /// <summary>Hash mismatched — determinism violation detected.</summary>
        Fail,
        /// <summary>Insufficient history — verification skipped.</summary>
        Skip
    }

    /// <summary>
    /// Per-tick SyncTest verification result.
    /// </summary>
    public struct SyncTestResult
    {
        public SyncTestStatus Status;
        public int Tick;
        public int RollbackFromTick;
        public int RollbackToTick;
        public long ExpectedHash;
        public long ActualHash;
    }

    /// <summary>
    /// SyncTest failure details for the OnSyncError event.
    /// </summary>
    public struct SyncTestFailure
    {
        public int Tick;
        public int RollbackDistance;
        public long ExpectedHash;
        public long ActualHash;
        public int EntityCount;
    }

    /// <summary>
    /// Validates determinism and rollback correctness without networking.
    /// Each tick, rolls back and resimulates, then compares the state hashes.
    /// </summary>
    public interface ISyncTestRunner
    {
        /// <summary>
        /// Initialize.
        /// </summary>
        /// <param name="simulation">Target simulation (concrete EcsSimulation type required).</param>
        /// <param name="checkDistance">Rollback distance (in ticks; must be at most RollbackCapacity).</param>
        void Initialize(ISimulation simulation, int checkDistance = 5);

        /// <summary>
        /// Tick execution + verification. Called every tick.
        /// </summary>
        SyncTestResult RunTick(int tick, IReadOnlyList<ICommand> commands);

        /// <summary>
        /// Cumulative success rate (0.0 ~ 1.0).
        /// </summary>
        float SuccessRate { get; }

        /// <summary>
        /// Total number of verifications.
        /// </summary>
        int TotalChecks { get; }

        /// <summary>
        /// Number of failed verifications.
        /// </summary>
        int FailedChecks { get; }

        /// <summary>
        /// Invoked when a sync error occurs (hash mismatch).
        /// </summary>
        event Action<SyncTestFailure> OnSyncError;

        /// <summary>
        /// Notifies the runner of an externally-triggered rollback (e.g. from KlothoEngine).
        /// Resets the internal ring buffer so verification restarts cleanly.
        /// </summary>
        /// <param name="resumeFromTick">The tick on which the next RunTick will be called (the engine's CurrentTick).</param>
        void NotifyExternalRollback(int resumeFromTick);
    }
}
