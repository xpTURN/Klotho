using System;
using System.Buffers;
using Microsoft.Extensions.Logging;
using ZLogger;

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

            State = KlothoState.Running;
        }

        public void ConfirmSpectatorTick(int tick)
        {
            var prev = _spectatorLastConfirmedTick;
            if (tick > _spectatorLastConfirmedTick)
                _spectatorLastConfirmedTick = tick;
            _logger?.ZLogTrace($"[KlothoEngine][Spectator] ConfirmSpectatorTick: tick={tick}, prev={prev}, now={_spectatorLastConfirmedTick}, CurrentTick={CurrentTick}");
        }

        private void HandleSpectatorUpdate(float deltaTime)
        {
            _accumulator += deltaTime * 1000f;

            // Apply inputs confirmed by batch arrival first, and re-simulate via rollback if needed.
            if (_spectatorLastConfirmedTick > _prevSpectatorLastConfirmedTick)
                SpectatorHandleConfirmedInput();

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
                _logger?.ZLogWarning($"[KlothoEngine][Spectator] FireBatch Skipped (already batched): _lastVerifiedTick={_lastVerifiedTick}, _lastBatchedTick={_lastBatchedTick}");
                return;
            }

            int batchStart = _lastVerifiedTick - SPECTATOR_INPUT_INTERVAL + 1;
            if (TrySerializeVerifiedInputRange(batchStart, _lastVerifiedTick, out byte[] buf, out int bytesWritten))
            {
                _logger?.ZLogTrace($"[KlothoEngine][Spectator] FireBatch OK: batchStart={batchStart}, batchEnd={_lastVerifiedTick}, bytes={bytesWritten}");
                OnVerifiedInputBatchReady?.Invoke(batchStart, SPECTATOR_INPUT_INTERVAL, buf, bytesWritten);
                _lastBatchedTick = _lastVerifiedTick;
                ArrayPool<byte>.Shared.Return(buf);
            }
            else
            {
                _logger?.ZLogWarning($"[KlothoEngine][Spectator] FireBatch Serialization failed: batchStart={batchStart}, batchEnd={_lastVerifiedTick}, oldestTick={_inputBuffer.OldestTick}");
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
            _inputBuffer.AddCommand(command);
        }

        public void ResetToTick(int tick)
        {
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
            else if (_snapshotManager is State.RingSnapshotManager ringMgr)
                ringMgr.ClearAll();
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
        }

        private void ExecuteSpectatorPredictedTick()
        {
            if (_spectatorPredictionStartTick < 0)
                _spectatorPredictionStartTick = CurrentTick;

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
                    _spectatorPredictionStartTick = -1;
                    return;
                }

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

                DiffRollbackEvents(rollbackTo);

                for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                    EventPool.Return(_rollbackOldEventsCache[i]);
                _rollbackOldEventsCache.Clear();

                _spectatorPredictionStartTick = -1;
            }
            else
            {
                var snapshot = _snapshotManager.GetSnapshot(rollbackTo);
                if (snapshot == null)
                {
                    _spectatorPredictionStartTick = -1;
                    return;
                }

                _rollbackOldEventsCache.Clear();
                for (int t = snapshot.Tick; t < CurrentTick; t++)
                {
                    var oldEvents = _eventBuffer.GetEvents(t);
                    for (int ei = 0; ei < oldEvents.Count; ei++)
                        _rollbackOldEventsCache.Add(oldEvents[ei]);
                    _eventBuffer.ClearTick(t, returnToPool: false);
                }

                _simulation.Rollback(snapshot.Tick);
                CurrentTick = snapshot.Tick;
                _lastVerifiedTick = snapshot.Tick - 1;

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

                DiffRollbackEvents(snapshot.Tick);

                for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                    EventPool.Return(_rollbackOldEventsCache[i]);
                _rollbackOldEventsCache.Clear();

                _spectatorPredictionStartTick = -1;
            }
        }

        public int GetNearestSnapshotTickWithinBuffer()
        {
            int minTick = _inputBuffer.OldestTick + _simConfig.SyncCheckInterval;
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
            else
            {
                _savedTicksCache.Clear();
                _snapshotManager.GetSavedTicks(_savedTicksCache);
                for (int i = 0; i < _savedTicksCache.Count; i++)
                {
                    int t = _savedTicksCache[i];
                    if (t >= minTick && (bestTick == -1 || t < bestTick))
                        bestTick = t;
                }
            }
            return bestTick;
        }

        #endregion
    }
}
