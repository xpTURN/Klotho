using System;
using xpTURN.Klotho.Logging;
using System.Buffers;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        private bool _isSpectatorMode;
        private int _spectatorLastConfirmedTick = -1;
        private int _spectatorPredictionStartTick = -1;
        private int _prevSpectatorLastConfirmedTick = -1;
        private const int MAX_SPECTATOR_PREDICTION_TICKS = SPECTATOR_INPUT_INTERVAL + 2;

        // True after first ResetToTick (= FullState arrival). Until then, spectator simulation
        // ticks are blocked — RandomSeedComponent / GameTimerStateComponent singletons are not
        // yet populated, so any system that calls GetReadOnlySingleton would throw.
        private bool _spectatorBootstrapped;

        public bool IsSpectatorMode => _isSpectatorMode;
 
        #region Spectator

        public void StartSpectator(SpectatorStartInfo info)
        {
            _isSpectatorMode = true;
            _randomSeed = info.RandomSeed;
            _activePlayerIds.Clear();
            _activePlayerIds.AddRange(info.PlayerIds);

            CurrentTick = 0;
            _lastVerifiedTick = -1;
            _spectatorLastConfirmedTick = -1;
            _spectatorPredictionStartTick = -1;
            _prevSpectatorLastConfirmedTick = -1;
            _accumulator = 0;
            _lastBatchedTick = -1;
            _spectatorBootstrapped = false;

            State = KlothoState.Running;
        }

        public void ConfirmSpectatorTick(int tick)
        {
            var prev = _spectatorLastConfirmedTick;
            if (tick > _spectatorLastConfirmedTick)
                _spectatorLastConfirmedTick = tick;
            _logger?.KTrace($"[KlothoEngine][Spectator] ConfirmSpectatorTick: tick={tick}, prev={prev}, now={_spectatorLastConfirmedTick}, CurrentTick={CurrentTick}");
        }

        private void HandleSpectatorUpdate(float deltaTime)
        {
            // Block all tick execution until first FullState arrival (RestoreFromFullState +
            // ResetToTick). Before bootstrap, simulation state is empty — required singletons
            // (RandomSeedComponent, GameTimerStateComponent, ...) do not exist and Update systems
            // that call GetReadOnlySingleton would throw. Drain accumulator to avoid burst tick
            // catch-up on bootstrap.
            if (!_spectatorBootstrapped)
            {
                _accumulator = 0;
                return;
            }

            _accumulator += deltaTime * 1000f;

            // Apply inputs confirmed by batch arrival first, and re-simulate via rollback if needed.
            // No error-correction pair here: a spectator has no LocalPlayerId, so every view is assigned
            // the snapshot interpolation path, and that path deliberately skips the rollback delta —
            // verified-frame interpolation already renders the authoritative state. Deltas computed here
            // would have no consumer (IMP103 V-4), which is why CapturePreRollbackTransforms now skips
            // spectators outright rather than leaving the resync path to park deltas nobody drains
            // (IMP105 C-6). The per-frame clear runs in Update, ahead of this method's own early return.
            bool batchArrived = _spectatorLastConfirmedTick > _prevSpectatorLastConfirmedTick;
            if (batchArrived)
            {
                SpectatorHandleConfirmedInput();
            }

            // If only the confirmed tick is behind without any prediction, run a catch-up loop regardless of accumulated time.
            while (CurrentTick + 1 < _spectatorLastConfirmedTick)
            {
                ExecuteSpectatorVerifiedTick();
            }

            // Advance verified/predicted ticks by the accumulated time.
            while (_accumulator >= _simConfig.TickIntervalMs)
            {
                if (CurrentTick <= _spectatorLastConfirmedTick)
                {
                    _accumulator -= _simConfig.TickIntervalMs;
                    ExecuteSpectatorVerifiedTick();
                }
                else if (CurrentTick <= _spectatorLastConfirmedTick + MAX_SPECTATOR_PREDICTION_TICKS)
                {
                    _accumulator -= _simConfig.TickIntervalMs;
                    ExecuteSpectatorPredictedTick();
                }
                else
                {
                    break;
                }
            }

            _prevSpectatorLastConfirmedTick = _spectatorLastConfirmedTick;
        }

        private void FireVerifiedInputBatch()
        {
            if (_networkService == null) return;
            // Gate: fire when there is any receiver — spectators OR pending Late Join catchups.
            // Without the LateJoin branch, P2P guests post-catch-up never receive verified input
            // batches (no spectators in typical P2P) → input buffer empty for gap ticks → chain
            // advance permanently stuck.
            if (_networkService.SpectatorCount == 0 && _networkService.PendingLateJoinCatchupCount == 0)
                return;
            if ((_lastVerifiedTick + 1) % SPECTATOR_INPUT_INTERVAL != 0)
                return;
            if (_lastVerifiedTick <= _lastBatchedTick)
            {
                _logger?.KWarning($"[KlothoEngine][Spectator] FireBatch Skipped (already batched): _lastVerifiedTick={_lastVerifiedTick}, _lastBatchedTick={_lastBatchedTick}");
                return;
            }

            int batchStart = _lastVerifiedTick - SPECTATOR_INPUT_INTERVAL + 1;
            if (TrySerializeVerifiedInputRange(batchStart, _lastVerifiedTick, out byte[] buf, out int bytesWritten))
            {
                _logger?.KTrace($"[KlothoEngine][Spectator] FireBatch OK: batchStart={batchStart}, batchEnd={_lastVerifiedTick}, bytes={bytesWritten}");
                OnVerifiedInputBatchReady?.Invoke(batchStart, SPECTATOR_INPUT_INTERVAL, buf, bytesWritten);
                _lastBatchedTick = _lastVerifiedTick;
                ArrayPool<byte>.Shared.Return(buf);
            }
            else
            {
                _logger?.KWarning($"[KlothoEngine][Spectator] FireBatch Serialization failed: batchStart={batchStart}, batchEnd={_lastVerifiedTick}, oldestTick={_inputBuffer.OldestTick}");
            }
        }

        public bool TrySerializeVerifiedInputRange(int fromTick, int toTick, out byte[] data, out int dataLength)
        {
            data = null;
            dataLength = 0;

            if (fromTick < _inputBuffer.OldestTick)
                return false;

            int totalSize = 0;
            for (int tick = fromTick; tick <= toTick; tick++)
            {
                var commands = _inputBuffer.GetCommandList(tick);
                totalSize += 4; // commandCount
                for (int i = 0; i < commands.Count; i++)
                    totalSize += commands[i].GetSerializedSize();
            }

            byte[] buf = ArrayPool<byte>.Shared.Rent(totalSize);
            var writer = new SpanWriter(buf.AsSpan());

            for (int tick = fromTick; tick <= toTick; tick++)
            {
                var commands = _inputBuffer.GetCommandList(tick);
                writer.WriteInt32(commands.Count);
                for (int i = 0; i < commands.Count; i++)
                    commands[i].Serialize(ref writer);
            }

            data = buf;
            dataLength = writer.Position;
            return true;
        }

        public void ReceiveConfirmedCommand(ICommand command)
        {
            // Spectator: speculative-ahead does not use _pendingCommands (ResetToTick only Clears
            // it), so the prediction reconcile is neither needed nor correct — keep the bare add.
            if (_isSpectatorMode)
            {
                _inputBuffer.AddCommand(command);
                return;
            }

            // P2P reconnect/late-join catchup. The confirmed input is seated, then
            // reconciled against the peer's speculative prediction exactly like a network arrival
            // (HandleCommandReceived). Without this, a late real that the peer mispredicted
            // (empty/repeat-last) was promoted frozen by TryAdvanceVerifiedChain -> per-peer desync.
            // resyncOnDeepRollback: a rollback target below the snapshot-ring window falls back to
            // FullStateResync; the command is seated first so resync's buffer Clear disposes it
            // (no separate pool Return — it is buffer-owned once stored).
            var storeResult = _inputBuffer.AddCommandChecked(command);
            ReconcileConfirmedAgainstPrediction(command, storeResult, resyncOnDeepRollback: true);
        }

        public void ResetToTick(int tick)
        {
            if (!_spectatorBootstrapped)
            {
                _spectatorBootstrapped = true;
                _logger?.KDebug($"[KlothoEngine][Spectator] Bootstrap complete at tick={tick}");
            }
            CurrentTick = tick;
            _lastVerifiedTick = tick - 1;
            _spectatorLastConfirmedTick = tick - 1;
            _spectatorPredictionStartTick = -1;
            _prevSpectatorLastConfirmedTick = tick - 1;
            _accumulator = 0;
            _inputBuffer.Clear();
            _pendingCommands.Clear();
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.ClearSnapshots();
            SaveSnapshot(tick);

            _eventCollector.BeginTick(tick);
            _simulation.EmitSyncEvents();
            _eventBuffer.ClearTick(tick);
            // Watermark cascade: ClearTick discards buffered Synced at `tick`, and the following
            // DispatchTickEvents will re-dispatch the freshly re-emitted batch. Lower watermark
            // below tick so the helper does not short-circuit.
            if (_syncedDispatchHighWaterMark >= tick)
                _syncedDispatchHighWaterMark = tick - 1;
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(tick, _eventCollector.Collected[ei]);
            DispatchTickEvents(tick, FrameState.Verified);
        }

        private void ExecuteSpectatorVerifiedTick()
        {
            var commands = _inputBuffer.GetCommandList(CurrentTick);
            _logger?.KDebug($"[Spectator] VerifiedTick: tick={CurrentTick}, cmds={commands.Count}, frame.Tick before={_simulation.CurrentTick}");
            SaveSnapshot(CurrentTick);

            _eventCollector.BeginTick(CurrentTick);
            _simulation.Tick(commands);

            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            _lastVerifiedTick = CurrentTick;

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, FrameState.Verified);
            DispatchTickEvents(executedTick, FrameState.Verified);
            _logger?.KDebug($"[Spectator] VerifiedTick: executedTick={executedTick}, frame.Tick after={_simulation.CurrentTick}");
        }

        private void ExecuteSpectatorPredictedTick()
        {
            if (_spectatorPredictionStartTick < 0)
                _spectatorPredictionStartTick = CurrentTick;

            _logger?.KDebug($"[Spectator] PredictedTick: tick={CurrentTick}, predictionStart={_spectatorPredictionStartTick}, confirmedTick={_spectatorLastConfirmedTick}, frame.Tick before={_simulation.CurrentTick}");

            SaveSnapshot(CurrentTick);

            _tickCommandsCache.Clear();
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int playerId = _activePlayerIds[pi];
                GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT);
                var predicted = _inputPredictor.PredictInput(playerId, CurrentTick, _previousCommandsCache);
                _tickCommandsCache.Add(predicted);
            }

            _eventCollector.BeginTick(CurrentTick);
            _simulation.Tick(_tickCommandsCache);

            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, FrameState.Predicted);
            DispatchTickEvents(executedTick, FrameState.Predicted);
            _logger?.KDebug($"[Spectator] PredictedTick: executedTick={executedTick}, frame.Tick after={_simulation.CurrentTick}");
        }

        private void SpectatorHandleConfirmedInput()
        {
            if (_spectatorPredictionStartTick < 0)
                return;

            int rollbackTo = _spectatorPredictionStartTick;

            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
            {
                if (!ecsSim.HasSnapshot(rollbackTo))
                {
                    _logger?.KWarning($"[Spectator] Rollback skipped (ECS): no snapshot at rollbackTo={rollbackTo}, frame.Tick={_simulation.CurrentTick}, confirmedTick={_spectatorLastConfirmedTick}");
                    _spectatorPredictionStartTick = -1;
                    return;
                }

                _logger?.KDebug($"[Spectator] Rollback (ECS): rollbackTo={rollbackTo}, frame.Tick before={_simulation.CurrentTick}, confirmedTick={_spectatorLastConfirmedTick}, resimRange=[{rollbackTo},{_spectatorLastConfirmedTick}], predictedDepth={CurrentTick - rollbackTo}");

                _rollbackOldEventsCache.Clear();
                for (int t = rollbackTo; t < CurrentTick; t++)
                {
                    var oldEvents = _eventBuffer.GetEvents(t);
                    for (int ei = 0; ei < oldEvents.Count; ei++)
                        _rollbackOldEventsCache.Add(oldEvents[ei]);
                    _eventBuffer.ClearTick(t, returnToPool: false);
                }

                _simulation.Rollback(rollbackTo);
                CurrentTick = rollbackTo;
                _lastVerifiedTick = rollbackTo - 1;

                // Pre-re-advance dispatched-Synced boundary.
                int dispatchedSyncedMark = _syncedDispatchHighWaterMark;

                while (CurrentTick <= _spectatorLastConfirmedTick)
                {
                    SaveSnapshot(CurrentTick);
                    var commands = _inputBuffer.GetCommandList(CurrentTick);
                    _eventCollector.BeginTick(CurrentTick);
                    _simulation.Tick(commands);
                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

                    // When promoted to verified, dispatch buffered Synced events at once.
                    // Prevents the issue where Synced events buffered during prediction never fire even after re-simulation, leaving VFX behind.
                    DispatchSyncedEventsForTick(CurrentTick, _eventBuffer.GetEvents(CurrentTick));

                    _lastVerifiedTick = CurrentTick;
                    CurrentTick++;
                }

                DiffRollbackEvents(rollbackTo, dispatchedSyncedMark);

                _logger?.KDebug($"[Spectator] Rollback (ECS) complete: frame.Tick after={_simulation.CurrentTick}, lastVerifiedTick={_lastVerifiedTick}");

                for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                    EventPool.Return(_rollbackOldEventsCache[i]);
                _rollbackOldEventsCache.Clear();

                _spectatorPredictionStartTick = -1;
            }
            else
            {
                // Non-ECS: resolve through the simulation's own snapshot history.
                int resolved = _simulation.GetNearestRollbackTick(rollbackTo);
                if (resolved < 0)
                {
                    _logger?.KWarning($"[Spectator] Rollback skipped (Snapshot): no snapshot at rollbackTo={rollbackTo}, frame.Tick={_simulation.CurrentTick}, confirmedTick={_spectatorLastConfirmedTick}");
                    _spectatorPredictionStartTick = -1;
                    return;
                }

                _logger?.KDebug($"[Spectator] Rollback (Snapshot): rollbackTo={resolved}, frame.Tick before={_simulation.CurrentTick}, confirmedTick={_spectatorLastConfirmedTick}, resimRange=[{resolved},{_spectatorLastConfirmedTick}], predictedDepth={CurrentTick - resolved}");

                _rollbackOldEventsCache.Clear();
                for (int t = resolved; t < CurrentTick; t++)
                {
                    var oldEvents = _eventBuffer.GetEvents(t);
                    for (int ei = 0; ei < oldEvents.Count; ei++)
                        _rollbackOldEventsCache.Add(oldEvents[ei]);
                    _eventBuffer.ClearTick(t, returnToPool: false);
                }

                _simulation.Rollback(resolved);
                CurrentTick = resolved;
                _lastVerifiedTick = resolved - 1;

                // Pre-re-advance dispatched-Synced boundary.
                int dispatchedSyncedMark = _syncedDispatchHighWaterMark;

                while (CurrentTick <= _spectatorLastConfirmedTick)
                {
                    SaveSnapshot(CurrentTick);
                    var commands = _inputBuffer.GetCommandList(CurrentTick);
                    _eventCollector.BeginTick(CurrentTick);
                    _simulation.Tick(commands);
                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

                    // When promoted to verified, dispatch buffered Synced events at once.
                    // Prevents the issue where Synced events buffered during prediction never fire even after re-simulation, leaving VFX behind.
                    DispatchSyncedEventsForTick(CurrentTick, _eventBuffer.GetEvents(CurrentTick));

                    _lastVerifiedTick = CurrentTick;
                    CurrentTick++;
                }

                DiffRollbackEvents(resolved, dispatchedSyncedMark);

                _logger?.KDebug($"[Spectator] Rollback (Snapshot) complete: frame.Tick after={_simulation.CurrentTick}, lastVerifiedTick={_lastVerifiedTick}");

                for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                    EventPool.Return(_rollbackOldEventsCache[i]);
                _rollbackOldEventsCache.Clear();

                _spectatorPredictionStartTick = -1;
            }
        }

        public int GetNearestSnapshotTickWithinBuffer()
        {
            int minTick = _inputBuffer.OldestTick + _simConfig.GetEffectiveSyncCheckInterval();
            int bestTick = -1;

            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
            {
                _savedTicksCache.Clear();
                ecsSim.GetSavedSnapshotTicks(_savedTicksCache);
                for (int i = 0; i < _savedTicksCache.Count; i++)
                {
                    int t = _savedTicksCache[i];
                    if (t >= minTick && (bestTick == -1 || t < bestTick))
                        bestTick = t;
                }
            }
            // else: non-ECS simulations have no engine-side snapshot history —
            // no catchup start is available (bestTick stays -1).
            return bestTick;
        }

        #endregion
    }
}
