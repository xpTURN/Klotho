using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.State;

#if KLOTHO_FAULT_INJECTION
using xpTURN.Klotho.Diagnostics;
#endif

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Rollback failure reason constants.
    /// </summary>
    public static class RollbackFailureReason
    {
        public const string TooFar = "RollbackTooFar";
        public const string NoSnapshot = "NoSnapshot";
    }

#if DEBUG
    /// <summary>
    /// Rollback diagnostic information (DEBUG/editor only).
    /// </summary>
    public struct RollbackStats
    {
        public int TotalRollbacks;
        public int FailedRollbacks;
        public int MergedRollbacks;
        public int MaxRollbackDepth;
        public int LastRollbackTick;

        public void Reset()
        {
            TotalRollbacks = 0;
            FailedRollbacks = 0;
            MergedRollbacks = 0;
            MaxRollbackDepth = 0;
            LastRollbackTick = -1;
        }
    }
#endif

    public partial class KlothoEngine
    {
        // Engine-layer snapshots for rollback (separate from simulation Frame).
        // MAX_PREDICTION + 2
        private int SnapshotCapacity => _simConfig.MaxRollbackTicks + 2;
        private EngineStateSnapshot[] _engineSnapshots;

        // Deferred rollback merging.
        private int _pendingRollbackTick = -1;
        private bool _hasPendingRollback;

        internal struct EngineStateSnapshot
        {
            public int[] ActivePlayerIds;
        }

#if DEBUG
        private RollbackStats _rollbackStats;

        /// <summary>
        /// Rollback diagnostic information (DEBUG/editor only).
        /// </summary>
        public RollbackStats Stats => _rollbackStats;
#endif

        /// <summary>
        /// Deferred rollback request (merged at frame end).
        /// </summary>
        public void RequestRollback(int targetTick)
        {
            if (targetTick >= CurrentTick)
                return;

            if (!_hasPendingRollback || targetTick < _pendingRollbackTick)
            {
#if DEBUG
                if (_hasPendingRollback)
                    _rollbackStats.MergedRollbacks++;
#endif
                _pendingRollbackTick = targetTick;
                _hasPendingRollback = true;
            }
#if DEBUG
            else
            {
                _rollbackStats.MergedRollbacks++;
            }
#endif
        }

        /// <summary>
        /// Flushes pending rollback requests (called at frame end).
        /// </summary>
        private void FlushPendingRollback()
        {
            if (!_hasPendingRollback)
                return;

            _hasPendingRollback = false;
            int targetTick = _pendingRollbackTick;
            _pendingRollbackTick = -1;

            ExecuteRollback(targetTick);
        }

        /// <summary>
        /// Executes rollback and re-simulation (internal).
        /// </summary>
        private void ExecuteRollback(int targetTick)
        {
            int fromTick = CurrentTick;

            // Guard 1: future tick.
            if (targetTick >= CurrentTick)
                return;

            // Resolve snapshot tick (ECS / non-ECS branch).
            int resolvedTick;
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                resolvedTick = ResolveRollbackTick_Ecs(ecsSim, ref targetTick);
            else
                resolvedTick = ResolveRollbackTick_Default(ref targetTick);

            if (resolvedTick < 0)
                return;

            // Restore state.
            _simulation.Rollback(resolvedTick);

            // Restore engine-layer state.
            var engineSnapshot = _engineSnapshots[resolvedTick % _engineSnapshots.Length];
            _activePlayerIds.Clear();
            if (engineSnapshot.ActivePlayerIds != null)
                _activePlayerIds.AddRange(engineSnapshot.ActivePlayerIds);

            // Clamp the verified chain to before the rollback point.
            if (_lastVerifiedTick >= resolvedTick)
                _lastVerifiedTick = resolvedTick - 1;
            _lastBatchedTick = Math.Min(_lastBatchedTick, resolvedTick - 1);

            // Clear pending predictions (actual commands in InputBuffer are preserved during rollback).
            _pendingCommands.Clear();

            // Save previous predicted events in the rolled-back tick range (for cancel/confirm comparison).
            _rollbackOldEventsCache.Clear();
            for (int t = resolvedTick; t < CurrentTick; t++)
            {
                var oldEvents = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < oldEvents.Count; ei++)
                    _rollbackOldEventsCache.Add(oldEvents[ei]);
                _eventBuffer.ClearTick(t, returnToPool: false);
            }

            // Re-simulation with event collection.
#if DEBUG
            _inputBuffer.SetResimulating(true);
#endif
            int resimTick = resolvedTick;
            while (resimTick < CurrentTick)
            {
                SaveSnapshot(resimTick);

                // Collect actual commands + predict for missing players.
                _tickCommandsCache.Clear();
                var received = _inputBuffer.GetCommandList(resimTick);
                for (int i = 0; i < received.Count; i++)
                    _tickCommandsCache.Add(received[i]);

                for (int pi = 0; pi < _activePlayerIds.Count; pi++)
                {
                    int playerId = _activePlayerIds[pi];
                    if (!_inputBuffer.HasCommandForTick(resimTick, playerId))
                    {
                        GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT, resimTick);
                        var predicted = _inputPredictor.PredictInput(playerId, resimTick, _previousCommandsCache);
                        _tickCommandsCache.Add(predicted);
                        _pendingCommands.Add(predicted);
                    }
                }

                _eventCollector.BeginTick(resimTick);
                _tickCommandsCache.Sort(s_commandComparer);
                _simulation.Tick(_tickCommandsCache);
                for (int ei = 0; ei < _eventCollector.Count; ei++)
                    _eventBuffer.AddEvent(resimTick, _eventCollector.Collected[ei]);

                resimTick++;
            }

#if DEBUG
            _inputBuffer.SetResimulating(false);
#endif

            // Advance verified chain after re-simulation.
            TryAdvanceVerifiedChain();

            // Compare old vs new events: cancel old, dispatch new events.
            DiffRollbackEvents(resolvedTick);

            // Return old events to the pool after comparison.
            for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                EventPool.Return(_rollbackOldEventsCache[i]);
            _rollbackOldEventsCache.Clear();

            _logger?.ZLogWarning($"[KlothoEngine][Rollback] complete: {resolvedTick} -> {CurrentTick}");
            OnRollbackExecuted?.Invoke(fromTick, resolvedTick);

#if KLOTHO_FAULT_INJECTION
            RttSpikeMetricsCollector.OnRollback(fromTick - resolvedTick);
#endif

#if DEBUG
            _rollbackStats.TotalRollbacks++;
            _rollbackStats.LastRollbackTick = CurrentTick;
            int depth = fromTick - resolvedTick;
            if (depth > _rollbackStats.MaxRollbackDepth)
                _rollbackStats.MaxRollbackDepth = depth;

            if (_syncTestEnabled && _syncTestRunner != null)
                _syncTestRunner.NotifyExternalRollback(CurrentTick);
#endif
        }

        private void SaveSnapshot(int tick)
        {
            _engineSnapshots[tick % _engineSnapshots.Length] = new EngineStateSnapshot
            {
                ActivePlayerIds = _activePlayerIds.ToArray(),
            };

            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
            {
                ecsSim.SaveSnapshot();
            }
        }

        private int ResolveRollbackTick_Ecs(xpTURN.Klotho.ECS.EcsSimulation ecsSim, ref int targetTick)
        {
            if (targetTick < CurrentTick - _simConfig.MaxRollbackTicks)
            {
                int clampedTick = CurrentTick - _simConfig.MaxRollbackTicks;
                int nearestTick = ecsSim.GetNearestSnapshotTick(clampedTick);
                if (nearestTick < 0)
                {
                    _logger?.ZLogError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.TooFar}): target={targetTick}, current={CurrentTick}");
                    OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.TooFar);
                    RequestFullStateResync();
#if DEBUG
                    _rollbackStats.FailedRollbacks++;
#endif
                    return -1;
                }
                _logger?.ZLogWarning($"[KlothoEngine][Rollback] clamped: requested={targetTick}, clamped={nearestTick}, current={CurrentTick}");
                targetTick = nearestTick;
            }

            int resolvedTick = ecsSim.HasSnapshot(targetTick)
                ? targetTick
                : ecsSim.GetNearestSnapshotTick(targetTick);
            if (resolvedTick < 0)
            {
                _logger?.ZLogError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.NoSnapshot}): target={targetTick}, current={CurrentTick}");
                OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.NoSnapshot);
#if DEBUG
                _rollbackStats.FailedRollbacks++;
#endif
            }
            return resolvedTick;
        }

        private int ResolveRollbackTick_Default(ref int targetTick)
        {
            if (targetTick < CurrentTick - _simConfig.MaxRollbackTicks)
            {
                int clampedTick = CurrentTick - _simConfig.MaxRollbackTicks;
                IStateSnapshot fallbackSnapshot = _snapshotManager.GetSnapshot(clampedTick);
                if (fallbackSnapshot == null && _snapshotManager is RingSnapshotManager ringMgr)
                    fallbackSnapshot = ringMgr.GetNearestSnapshot(clampedTick);

                if (fallbackSnapshot != null)
                {
                    _logger?.ZLogWarning($"[KlothoEngine][Rollback] clamped: requested={targetTick}, clamped={fallbackSnapshot.Tick}, current={CurrentTick}");
                    targetTick = fallbackSnapshot.Tick;
                }
                else
                {
                    _logger?.ZLogError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.TooFar}): target={targetTick}, current={CurrentTick}");
                    OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.TooFar);
                    RequestFullStateResync();
#if DEBUG
                    _rollbackStats.FailedRollbacks++;
#endif
                    return -1;
                }
            }

            var snapshot = _snapshotManager.GetSnapshot(targetTick);
            if (snapshot == null)
            {
                if (_snapshotManager is RingSnapshotManager ringManager)
                    snapshot = ringManager.GetNearestSnapshot(targetTick);
            }
            if (snapshot == null)
            {
                _logger?.ZLogError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.NoSnapshot}): target={targetTick}, current={CurrentTick}");
                OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.NoSnapshot);
#if DEBUG
                _rollbackStats.FailedRollbacks++;
#endif
                return -1;
            }
            return snapshot.Tick;
        }
    }
}
