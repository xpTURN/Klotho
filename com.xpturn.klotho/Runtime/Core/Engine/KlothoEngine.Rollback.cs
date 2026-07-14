using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Input;

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
        private int SnapshotCapacity => _simConfig.GetSnapshotCapacity();
        private EngineStateSnapshot[] _engineSnapshots;

        // Deferred rollback merging.
        private int _pendingRollbackTick = -1;
        private bool _hasPendingRollback;

        internal struct EngineStateSnapshot
        {
            // Slot-owned, reused in place across ring wraps.
            // null = never-written slot — restore paths rely on this sentinel.
            public List<int> ActivePlayerIds;
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
        /// Rollback request that falls back to FullStateResync when the target is below
        /// the snapshot-ring window. Used by the catchup reconcile path (resyncOnDeepRollback): a
        /// late mispredicted input whose tick is older than CurrentTick - MaxRollbackTicks cannot be
        /// corrected by ResolveRollbackTick's clamp (it clamps to the window edge and re-sims from
        /// there, leaving the mispredicted gap tick frozen — clamp-without-resync). Resync instead.
        /// The normal arrival path passes allowResync=false to keep the existing clamp behavior.
        /// </summary>
        private void RequestRollbackOrResync(int targetTick, bool allowResync)
        {
            if (allowResync && targetTick < CurrentTick - _simConfig.MaxRollbackTicks)
            {
                RequestFullStateResync();
                return;
            }
            RequestRollback(targetTick);
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

            // Resolve the snapshot tick through the simulation's own history:
            // simulations own their rollback snapshots, the engine only queries availability.
            int resolvedTick = ResolveRollbackTick(ref targetTick);

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
            // Rewind, NOT a promotion — does not call RecordVerifiedTick. The subsequent
            // chain re-advance (TryAdvanceVerifiedChain) re-records the re-simulated (corrected) set.
            if (_lastVerifiedTick >= resolvedTick)
                _lastVerifiedTick = resolvedTick - 1;
            _lastBatchedTick = Math.Min(_lastBatchedTick, resolvedTick - 1);

            // Clear pending predictions (actual commands in InputBuffer are preserved during rollback).
            _pendingCommands.Clear();

            // Invalidate local sync hashes in the rolled-back range — re-simulation may change
            // them when inputs changed (e.g. presumed-drop empty fills replaced by late real
            // commands). Re-simulation and chain re-advance recompute and re-send them.
            for (int t = resolvedTick; t < CurrentTick; t++)
            {
                _localHashes.Remove(t);
                _deferredHashSendTicks.Remove(t);
            }
            _networkService?.InvalidateLocalSyncHashes(resolvedTick);

            // The host's serialized FullState cache keys only on CurrentTick, which
            // rollback+resim leaves unchanged while the same-tick state bytes may differ. Invalidate
            // so the next unicast serve / corrective reset re-serializes the rolled-back state.
            _cachedFullStateTick = -1;

            // Save previous predicted events in the rolled-back tick range (for cancel/confirm comparison).
            _rollbackOldEventsCache.Clear();
            for (int t = resolvedTick; t < CurrentTick; t++)
            {
                var oldEvents = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < oldEvents.Count; ei++)
                    _rollbackOldEventsCache.Add(oldEvents[ei]);
                _eventBuffer.ClearTick(t, returnToPool: false);
            }

            // Re-simulation with event collection. Stage covers the whole post-rollback window
            // — resim loop, chain re-advance, and event diff — matching the SD batch path, which
            // wraps ProcessVerifiedBatchCore (resim + promotion dispatch + diff) entirely, so
            // ILockstepEngine.IsResimulation is mode-agnostic for side-effect suppression.
            Stage = SimulationStage.Resimulate;
            try
            {
#if DEBUG || DEVELOPMENT_BUILD
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
                            // Empty-predict confirmed-disconnected peers so the resim does
                            // not re-create a repeat-last pending that the host's empty fill would
                            // mismatch into a cascade rollback. Same branch as the main path.
                            var predicted = PredictInputOrEmpty(playerId, resimTick, resimTick);
                            _tickCommandsCache.Add(predicted);
                            _pendingCommands.Add(predicted);
                        }
                    }

                    _eventCollector.BeginTick(resimTick);
                    _tickCommandsCache.Sort(s_commandComparer);
                    _simulation.Tick(_tickCommandsCache);
                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(resimTick, _eventCollector.Collected[ei]);

                    // Recompute the stashed sync hash for re-simulated check ticks (invalidated
                    // above); the chain re-advance performs the deferred send.
                    if (_networkService != null && resimTick % _simConfig.GetEffectiveSyncCheckInterval() == 0)
                    {
                        long resimHash = _simulation.GetStateHash();
                        _diagHistorySim?.RecordHashHistory(resimTick);   // Adjacent to the compute (contract)
                        _localHashes[resimTick] = (resimHash, ComputeCommandDigest(_tickCommandsCache));   // Command digest
                        _deferredHashSendTicks.Add(resimTick);
                    }
                    else
                    {
                        // Re-record the corrected-timeline breakdown for every resim tick so L1 sees
                        // the post-rollback values, not the pre-rollback speculative ones.
                        // No network gate needed here: an engine rollback only occurs in networked play.
                        _diagHistorySim?.ComputeAndRecordHashHistory(resimTick);
                    }

                    resimTick++;
                }

#if DEBUG || DEVELOPMENT_BUILD
                _inputBuffer.SetResimulating(false);
#endif

                // Pre-rollback dispatched-Synced boundary: captured before the chain
                // re-advance raises the watermark, so re-advance dispatches above it are not misread
                // as divergences by the diff below.
                int dispatchedSyncedMark = _syncedDispatchHighWaterMark;

                // Advance verified chain after re-simulation.
                TryAdvanceVerifiedChain();

                // Compare old vs new events: cancel old, dispatch new events.
                DiffRollbackEvents(resolvedTick, dispatchedSyncedMark);

                // Return old events to the pool after comparison.
                for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                    EventPool.Return(_rollbackOldEventsCache[i]);
                _rollbackOldEventsCache.Clear();
            }
            finally
            {
                Stage = SimulationStage.Forward;
            }

            _logger?.KWarning($"[KlothoEngine][Rollback] complete: {resolvedTick} -> {CurrentTick}");
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
            // Reuse the slot-owned list instead of allocating per tick.
            // Do not reassign the struct — that would orphan the reused list.
            ref var slot = ref _engineSnapshots[tick % _engineSnapshots.Length];
            slot.ActivePlayerIds ??= new List<int>(_activePlayerIds.Count);
            slot.ActivePlayerIds.Clear();
            slot.ActivePlayerIds.AddRange(_activePlayerIds);

            _simulation.SaveSnapshot();
        }

        private int ResolveRollbackTick(ref int targetTick)
        {
            // Deep request — clamp to the window edge and take the nearest restorable tick.
            if (targetTick < CurrentTick - _simConfig.MaxRollbackTicks)
            {
                int clampedTick = CurrentTick - _simConfig.MaxRollbackTicks;
                int nearestTick = _simulation.GetNearestRollbackTick(clampedTick);
                if (nearestTick < 0)
                {
                    _logger?.KError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.TooFar}): target={targetTick}, current={CurrentTick}");
                    OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.TooFar);
                    RequestFullStateResync();
#if DEBUG
                    _rollbackStats.FailedRollbacks++;
#endif
                    return -1;
                }
                _logger?.KWarning($"[KlothoEngine][Rollback] clamped: requested={targetTick}, clamped={nearestTick}, current={CurrentTick}");
                targetTick = nearestTick;
            }

            int resolvedTick = _simulation.GetNearestRollbackTick(targetTick);
            if (resolvedTick < 0)
            {
                _logger?.KError($"[KlothoEngine][Rollback] failed ({RollbackFailureReason.NoSnapshot}): target={targetTick}, current={CurrentTick}");
                OnRollbackFailed?.Invoke(targetTick, RollbackFailureReason.NoSnapshot);
#if DEBUG
                _rollbackStats.FailedRollbacks++;
#endif
                return -1;
            }
            return resolvedTick;
        }
    }
}
