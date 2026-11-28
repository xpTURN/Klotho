using System;
using System.Collections.Generic;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Validates determinism and rollback correctness without networking.
    /// Every tick: forward execution -> rollback by checkDistance ticks -> re-simulation -> hash comparison.
    /// </summary>
    public class SyncTestRunner : ISyncTestRunner
    {
        private EcsSimulation _simulation;
        private int _checkDistance;

        // Hash ring buffer (for validation)
        private long[] _hashRing;
        private int _ringCapacity;

        // Input ring buffer (for re-simulation)
        private List<ICommand>[] _inputRing;

        // Statistics
        private int _totalChecks;
        private int _failedChecks;
        private int _consecutiveFails;

        // Post-forward state side buffer: the ring stores pre-tick snapshots only,
        // so a failed check needs this copy to restore the forward branch — otherwise the
        // diverged resim branch stays live and the tool injects the desync it should detect.
        private Frame _forwardFrame;
        private byte[] _forwardSystemState;

        public float SuccessRate => _totalChecks > 0
            ? (float)(_totalChecks - _failedChecks) / _totalChecks
            : 1.0f;
        public int TotalChecks => _totalChecks;
        public int FailedChecks => _failedChecks;

        public event Action<SyncTestFailure> OnSyncError;
        public event Action OnSyncTestDisabled;

        /// <inheritdoc />
        public int ConsecutiveFailLimit { get; set; } = 3;

        // Cache (GC prevention)
        private readonly List<ICommand> _resimCommandsCache = new List<ICommand>();

        public void Initialize(ISimulation simulation, int checkDistance = 5)
        {
            _simulation = simulation as EcsSimulation
                ?? throw new ArgumentException("SyncTestRunner requires EcsSimulation type");

            if (checkDistance < 1)
                throw new ArgumentException(
                    $"checkDistance({checkDistance}) must be ≥ 1");

            if (checkDistance >= _simulation.RollbackCapacity)
                throw new ArgumentException(
                    $"checkDistance({checkDistance}) must be < RollbackCapacity({_simulation.RollbackCapacity})");

            _checkDistance = checkDistance;
            _ringCapacity = checkDistance + 2; // headroom

            _hashRing = new long[_ringCapacity];
            _inputRing = new List<ICommand>[_ringCapacity];

            for (int i = 0; i < _ringCapacity; i++)
                _inputRing[i] = new List<ICommand>();

            _totalChecks = 0;
            _failedChecks = 0;
            _consecutiveFails = 0;

            _forwardFrame = new Frame(_simulation.Layout, null);
        }

        // Tracks the tick from which validation should resume after an external rollback
        private int _resumeFromTick = -1;

        public void NotifyExternalRollback(int resumeFromTick)
        {
            // After an external rollback, the internal ring buffers (hashes/inputs) are invalidated.
            // Skip validation until enough new ticks have accumulated.
            _resumeFromTick = resumeFromTick + _checkDistance;

            // Reset ring buffers
            for (int i = 0; i < _ringCapacity; i++)
            {
                _hashRing[i] = 0;
                _inputRing[i].Clear();
            }
        }

        public SyncTestResult RunTick(int tick, IReadOnlyList<ICommand> commands)
        {
            int ringIdx = tick % _ringCapacity;

            // -- 1. Save snapshot (before tick execution) --
            _simulation.SaveSnapshot();

            // -- 2. Record inputs --
            _inputRing[ringIdx].Clear();
            for (int i = 0; i < commands.Count; i++)
                _inputRing[ringIdx].Add(commands[i]);

            // -- 3. Forward execution --
            _simulation.Tick(_inputRing[ringIdx]);

            // -- 4. Save hash --
            long forwardHash = _simulation.GetStateHash();
            _hashRing[ringIdx] = forwardHash;

            // -- 5. Skip if history is insufficient --
            if (tick < _checkDistance || (_resumeFromTick >= 0 && tick < _resumeFromTick))
            {
                return new SyncTestResult
                {
                    Status = SyncTestStatus.Skip,
                    Tick = tick
                };
            }

            // Once past that point, clear the resume marker
            if (_resumeFromTick >= 0 && tick >= _resumeFromTick)
                _resumeFromTick = -1;

            // -- 5.5. Keep the post-forward state (restored on check failure) --
            _simulation.CaptureStateTo(_forwardFrame, ref _forwardSystemState);

            // -- 6. Rollback validation --
            int rollbackTo = tick - _checkDistance;

            // 6a. Suppress event collection during re-simulation
            var savedEventRaiser = _simulation.Frame.EventRaiser;
            _simulation.Frame.EventRaiser = null;

            // 6b. Execute rollback
            _simulation.Rollback(rollbackTo);

            // 6c. Re-simulate from rollbackTo to tick
            for (int resimTick = rollbackTo; resimTick <= tick; resimTick++)
            {
                int resimIdx = resimTick % _ringCapacity;

                // Save snapshot during re-simulation (needed for next validation)
                _simulation.SaveSnapshot();

                _resimCommandsCache.Clear();
                var savedCommands = _inputRing[resimIdx];
                for (int i = 0; i < savedCommands.Count; i++)
                    _resimCommandsCache.Add(savedCommands[i]);

                _simulation.Tick(_resimCommandsCache);
            }

            // 6d. Restore event collection
            _simulation.Frame.EventRaiser = savedEventRaiser;

            // 6e. Hash comparison
            long rollbackHash = _simulation.GetStateHash();
            _totalChecks++;

            if (forwardHash != rollbackHash)
            {
                _failedChecks++;

                var failure = new SyncTestFailure
                {
                    Tick = tick,
                    RollbackDistance = _checkDistance,
                    ExpectedHash = forwardHash,
                    ActualHash = rollbackHash,
                    EntityCount = _simulation.Frame.Entities.Count
                };

                // Restore the forward branch — the diverged resim branch must not stay live.
                // Ring slots in [rollbackTo, tick] still hold resim-branch
                // pre-tick saves; skip checks until forward saves repopulate the window so the
                // next comparison does not roll back into polluted history.
                _simulation.RestoreStateFrom(_forwardFrame, _forwardSystemState);
                _resumeFromTick = tick + _checkDistance + 1;

                _consecutiveFails++;
                OnSyncError?.Invoke(failure);
                if (_consecutiveFails >= ConsecutiveFailLimit)
                    OnSyncTestDisabled?.Invoke();

                return new SyncTestResult
                {
                    Status = SyncTestStatus.Fail,
                    Tick = tick,
                    RollbackFromTick = tick,
                    RollbackToTick = rollbackTo,
                    ExpectedHash = forwardHash,
                    ActualHash = rollbackHash
                };
            }

            // 6d. Validation passed
            _consecutiveFails = 0;
            return new SyncTestResult
            {
                Status = SyncTestStatus.Pass,
                Tick = tick,
                RollbackFromTick = tick,
                RollbackToTick = rollbackTo,
                ExpectedHash = forwardHash,
                ActualHash = rollbackHash
            };
        }
    }
}
