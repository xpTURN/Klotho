using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // ── SD client-only fields ─────────────────────────

        /// <summary>
        /// Queue for accumulating received VerifiedStateMessages per frame for batch processing.
        /// </summary>
        private readonly Queue<VerifiedStateEntry> _pendingVerifiedQueue
            = new Queue<VerifiedStateEntry>();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Per-tick perf measurement. Logs only when an iteration exceeds TickIntervalMs.
        private readonly System.Diagnostics.Stopwatch _perfSw = new System.Diagnostics.Stopwatch();
#endif

        private struct VerifiedStateEntry
        {
            public int Tick;
            public List<ICommand> Commands; // Pooled list. Return after ProcessVerifiedBatch completes.
            public long StateHash;
        }

        // ── Client tick loop ──────────────────────────

        /// <summary>
        /// SD Client tick loop.
        /// Proceeds in order: lead tick control → prediction sim → ProcessVerifiedBatch → cleanup.
        /// </summary>
        private void UpdateServerDrivenClient(float deltaTime)
        {
            ClearErrorDeltas();

            // Advance the Verified clock adaptively.
            AdaptiveClock.Tick(deltaTime, _simConfig.TickIntervalMs, _simConfig.InterpolationDelayTicks);

            // Lead tick control: slow down at the soft threshold, wait at the hard limit.
            int leadTicks = CurrentTick - _lastServerVerifiedTick;
            int targetLead = ComputeSDInputLeadTicks();
            int softThreshold = targetLead + _simConfig.MaxRollbackTicks / 4;
            int hardLimit = targetLead + _simConfig.MaxRollbackTicks;

            if (_consumePendingDeltaTime)
            {
                _consumePendingDeltaTime = false;
                _logger?.ZLogDebug($"[KlothoEngine] Pending deltaTime consumed: {deltaTime * 1000f:F1}ms dropped at tick={CurrentTick}");
            }
            else if (softThreshold > 0 && hardLimit > softThreshold && leadTicks > softThreshold)
            {
                // Past the soft threshold, reduce accumulation speed proportionally toward the hard limit.
                float ratio = (float)(leadTicks - softThreshold) / (hardLimit - softThreshold);
                if (ratio > 0.9f) ratio = 0.9f;
                _accumulator += deltaTime * 1000f * (1f - ratio);
            }
            else
            {
                _accumulator += deltaTime * 1000f;
            }

            while (_accumulator >= _simConfig.TickIntervalMs)
            {
                // Wait when the hard limit is reached until a server confirmation arrives.
                leadTicks = CurrentTick - _lastServerVerifiedTick;
                if (hardLimit > 0 && leadTicks >= hardLimit)
                {
                    _logger?.ZLogWarning($"[KlothoEngine] ClientTick: Hard limit reached: currentTick={CurrentTick}, lastVerifiedTick={_lastServerVerifiedTick}, leadTicks={leadTicks}, hardLimit={hardLimit}");
                    break;
                }

                _accumulator -= _simConfig.TickIntervalMs;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if (_simConfig.TickDriftWarnMultiplier > 0)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_lastTickWallMs > 0)
                    {
                        long gap = nowMs - _lastTickWallMs;
                        if (gap > _simConfig.TickIntervalMs * _simConfig.TickDriftWarnMultiplier)
                            _logger?.ZLogWarning($"[KlothoEngine] Tick gap: {gap}ms (expected {_simConfig.TickIntervalMs}ms), tick={CurrentTick}");
                    }
                    _lastTickWallMs = nowMs;
                }
#endif

                // Collect local input.
                if (_simulationCallbacks != null)
                    _simulationCallbacks.OnPollInput(LocalPlayerId, CurrentTick, _commandSender);
                else
                    OnPreTick?.Invoke(CurrentTick);

                // Execute prediction tick.
                ExecuteClientPredictionTick();
            }

            // Capture Transform state right before rollback for use in subsequent error delta computation.
            CapturePreRollbackTransforms();

            // Process the server verified batch in bulk.
            ProcessVerifiedBatch();

            // Compute error delta by comparing with current Transform after rollback.
            ComputeErrorDeltas();

            // Defer Late Join activation callback. This fires right after the warmup burst finishes following catchup,
            // and before FlushSendQueue, so commands sent inside the callback are transmitted this frame.
            if (_pendingLateJoinActivation)
            {
                _pendingLateJoinActivation = false;
                _viewCallbacks?.OnLateJoinActivated(this);
            }

            CleanupOldData();
            _networkService?.FlushSendQueue();
        }

        /// <summary>
        /// Executes a single client prediction tick.
        /// </summary>
        private void ExecuteClientPredictionTick()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _perfSw.Restart();
#endif
            int frameTick = _simulation.CurrentTick;
            if (frameTick != CurrentTick)
                _logger?.ZLogWarning($"[KlothoEngine][SD] Tick desync: CurrentTick={CurrentTick}, frame.Tick={frameTick}");

            SaveSnapshot(CurrentTick);

            _tickCommandsCache.Clear();

            var received = _inputBuffer.GetCommandList(CurrentTick);
            for (int i = 0; i < received.Count; i++)
                _tickCommandsCache.Add(received[i]);

            // Predict missing player input
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int playerId = _activePlayerIds[pi];
                if (!_inputBuffer.HasCommandForTick(CurrentTick, playerId))
                {
                    GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT);
                    var predicted = _inputPredictor.PredictInput(playerId, CurrentTick, _previousCommandsCache);
                    _tickCommandsCache.Add(predicted);
                    // On the SD path, prediction validation is replaced by the state hash, so _pendingCommands is not used.
                }
            }

            _eventCollector.BeginTick(CurrentTick);
            _tickCommandsCache.Sort(s_commandComparer);
            _simulation.Tick(_tickCommandsCache);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _logger?.ZLogDebug($"[SD][HASH] PredTick: tick={CurrentTick} hash=0x{_simulation.GetStateHash():X16}");
#endif

            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, FrameState.Predicted);
            DispatchTickEvents(executedTick, FrameState.Predicted);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _perfSw.Stop();
            long elapsedMs = _perfSw.ElapsedMilliseconds;
            if (elapsedMs >= _simConfig.TickIntervalMs)
                _logger?.ZLogWarning(
                    $"[SD][PERF] PredictionTick slow: {elapsedMs}ms (tickInterval={_simConfig.TickIntervalMs}ms), tick={executedTick}");
#endif
        }

        // ── ProcessVerifiedBatch ─────────────────

        /// <summary>
        /// Called once per frame. Bundles received VerifiedStateMessages and
        /// processes snapshot restore -> verified resimulation -> hash validation -> prediction resimulation in one pass.
        /// </summary>
        private void ProcessVerifiedBatch()
        {
            // Skip if waiting for a FullState response.
            if (_fullStateRequestPending)
                return;

            if (_pendingVerifiedQueue.Count == 0)
                return;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            int batchCount = _pendingVerifiedQueue.Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
#endif

            // Entering the resimulation section. Guard with try/finally so every return path returns to Forward.
            Stage = SimulationStage.Resimulate;
            try
            {
                ProcessVerifiedBatchCore();
            }
            finally
            {
                Stage = SimulationStage.Forward;
            }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            sw.Stop();
            long elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs >= _simConfig.TickIntervalMs)
                _logger?.ZLogWarning(
                    $"[SD][PERF] VerifiedBatch slow: {elapsedMs}ms (tickInterval={_simConfig.TickIntervalMs}ms), batchCount={batchCount}, currentTick={CurrentTick}");
#endif
        }

        private void ProcessVerifiedBatchCore()
        {
            int batchCount = _pendingVerifiedQueue.Count;

            _logger?.ZLogDebug($"[KlothoEngine][SD] ProcessVerifiedBatch: batchCount={batchCount}, currentTick={CurrentTick}, lastVerifiedTick={_lastServerVerifiedTick}");

            // entry.Tick is _frame.Tick after execution (used for hash comparison); the actual command tick is entry.Tick - 1.
            var first = _pendingVerifiedQueue.Peek();
            int firstExecutionTick = first.Tick - 1;
            int restoreTick = firstExecutionTick;

            // For the tick 0 special case where the prediction tick has already advanced _frame, restore from the initial snapshot.
            if (restoreTick < 0 && CurrentTick > firstExecutionTick)
                restoreTick = 0;

            // Back up the existing predicted events and clear the event buffer for that range.
            _rollbackOldEventsCache.Clear();
            for (int t = firstExecutionTick; t < CurrentTick; t++)
            {
                var oldEvents = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < oldEvents.Count; ei++)
                    _rollbackOldEventsCache.Add(oldEvents[ei]);
                _eventBuffer.ClearTick(t, returnToPool: false);
            }

            // Restore state from the nearest verified snapshot.
            bool rollbackPerformed = false;
            if (restoreTick >= 0)
            {
                int actualRestoreTick = -1;
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                    actualRestoreTick = ecsSim.GetNearestSnapshotTick(restoreTick);

                // If no snapshot exists or it is too old, request a FullState and discard this batch.
                if (actualRestoreTick < 0 || actualRestoreTick < firstExecutionTick - _simConfig.MaxRollbackTicks)
                {
                    _logger?.ZLogWarning(
                        $"[KlothoEngine][SD] Rollback failed: no valid snapshot for restoreTick={restoreTick} (nearest={actualRestoreTick}), requesting FullState");

                    if (!_fullStateRequestPending)
                    {
                        _serverDrivenNetwork.SendFullStateRequest(firstExecutionTick);
                        _fullStateRequestPending = true;
                    }
                    _pendingVerifiedQueue.Clear();

                    for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                        EventPool.Return(_rollbackOldEventsCache[i]);
                    _rollbackOldEventsCache.Clear();
                    return;
                }
                else
                {
                    restoreTick = actualRestoreTick;
                    _logger?.ZLogDebug($"[KlothoEngine][SD] Rollback: restoreTick={restoreTick}, frame.Tick before={_simulation.CurrentTick}");
                    _simulation.Rollback(restoreTick);
                    _logger?.ZLogDebug($"[KlothoEngine][SD] Rollback: frame.Tick after={_simulation.CurrentTick}");
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.ZLogDebug($"[SD][DIAG] PostRestore: restoreTick={restoreTick} hash=0x{_simulation.GetStateHash():X16}");
#endif
                    rollbackPerformed = true;
                }
            }

            // Restore engine state
            if (rollbackPerformed && restoreTick >= 0)
            {
                var engineSnapshot = _engineSnapshots[restoreTick % _engineSnapshots.Length];
                if (engineSnapshot.ActivePlayerIds != null)
                {
                    _activePlayerIds.Clear();
                    _activePlayerIds.AddRange(engineSnapshot.ActivePlayerIds);
                }
            }

            // If the restored tick is earlier than the first execution tick, fill the gap from the InputBuffer.
            if (rollbackPerformed && restoreTick < firstExecutionTick)
            {
                int gapTick = restoreTick;
                while (gapTick < firstExecutionTick)
                {
                    SaveSnapshot(gapTick);
                    _tickCommandsCache.Clear();
                    var gapCmds = _inputBuffer.GetCommandList(gapTick);
                    for (int gi = 0; gi < gapCmds.Count; gi++)
                        _tickCommandsCache.Add(gapCmds[gi]);
                    _tickCommandsCache.Sort(s_commandComparer);
                    _simulation.Tick(_tickCommandsCache);
                    gapTick++;
                }
            }

            // Resimulate the verified ticks in order while validating the state hash.
            int lastVerifiedTick = -1;
            while (_pendingVerifiedQueue.Count > 0)
            {
                var entry = _pendingVerifiedQueue.Dequeue();
                int executionTick = entry.Tick - 1; // input tick at the time of execution

                // Overwrite the predicted input in the InputBuffer with the verified input.
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                {
                    bool hasLocal = false;
                    for (int i = 0; i < entry.Commands.Count; i++)
                    {
                        if (entry.Commands[i].PlayerId == LocalPlayerId) hasLocal = true;
                    }
                    if (!hasLocal)
                        _logger?.ZLogWarning($"[SD] Verified entry missing local input: executionTick={executionTick}, localId={LocalPlayerId}, entryCmds={entry.Commands.Count}");
                }
#endif
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                {
                    // Remote player commands are not stored in _inputBuffer (prediction only),
                    // so verify predicted-vs-verified consistency only for LocalPlayerId.
                    var existingCmds = _inputBuffer.GetCommandList(executionTick);
                    for (int ci = 0; ci < entry.Commands.Count; ci++)
                    {
                        var vc = entry.Commands[ci];
                        if (vc.PlayerId != LocalPlayerId) continue;
                        bool found = false;
                        for (int ei = 0; ei < existingCmds.Count; ei++)
                        {
                            if (existingCmds[ei].PlayerId == vc.PlayerId)
                            {
                                var ec = existingCmds[ei];
                                bool typeMatch = ec.GetType() == vc.GetType();
                                bool tickMatch = ec.Tick == vc.Tick;
                                if (!typeMatch || !tickMatch)
                                    _logger?.ZLogWarning($"[SD][DIAG] CmdDiff: executionTick={executionTick} pid={vc.PlayerId} predicted={ec.GetType().Name}(tick={ec.Tick}) verified={vc.GetType().Name}(tick={vc.Tick})");
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            _logger?.ZLogWarning($"[SD][DIAG] CmdMissing: executionTick={executionTick} pid={vc.PlayerId} verified={vc.GetType().Name}(tick={vc.Tick})");
                    }
                }
#endif
                for (int i = 0; i < entry.Commands.Count; i++)
                    _inputBuffer.AddCommand(entry.Commands[i]);

                // Verified resimulation
                SaveSnapshot(executionTick);
                _tickCommandsCache.Clear();
                var cmds = _inputBuffer.GetCommandList(executionTick);
                if (cmds.Count < entry.Commands.Count)
                {
                    _logger?.ZLogWarning(
                        $"[SD] Command lookup mismatch: executionTick={executionTick}, entry.Tick={entry.Tick}, expected={entry.Commands.Count}, found={cmds.Count}");
                }
                for (int i = 0; i < cmds.Count; i++)
                    _tickCommandsCache.Add(cmds[i]);

                _logger?.ZLogDebug($"[SD] Resim: executionTick={executionTick}, entry.Tick={entry.Tick}, frame.Tick before={_simulation.CurrentTick}, cmds={_tickCommandsCache.Count}");
                _eventCollector.BeginTick(executionTick);
                _tickCommandsCache.Sort(s_commandComparer);
#if DEBUG
                _inputBuffer.SetResimulating(true);
#endif
                _simulation.Tick(_tickCommandsCache);
#if DEBUG
                _inputBuffer.SetResimulating(false);
#endif
                _logger?.ZLogDebug($"[SD] Resim: frame.Tick after={_simulation.CurrentTick}");
                for (int ei = 0; ei < _eventCollector.Count; ei++)
                    _eventBuffer.AddEvent(executionTick, _eventCollector.Collected[ei]);

                // Hash validation
                long resimHash = _simulation.GetStateHash();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                _logger?.ZLogDebug($"[SD][DIAG] VerifiedHash: executionTick={executionTick} hash=0x{resimHash:X16}");
#endif
                if (resimHash != entry.StateHash)
                {
                    // Hash mismatch — determinism is broken, so request a FullState.
                    _logger?.ZLogError(
                        $"[KlothoEngine][SD] Determinism failure: tick={entry.Tick}, local=0x{resimHash:X16}, server=0x{entry.StateHash:X16}");

                    // Diagnostic — per-component hash to identify which component(s) diverged.
                    if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                        ecsSimDiag.LogComponentHashes(_logger, "DesyncLocal");

                    if (!_fullStateRequestPending)
                    {
                        _serverDrivenNetwork.SendFullStateRequest(entry.Tick);
                        _fullStateRequestPending = true;
                    }

                    // Leave the remaining batch in the queue to process after FullState restore.

                    // Save the desync-point state as a snapshot and synchronize CurrentTick.
                    SaveSnapshot(_simulation.CurrentTick);
                    if (_simulation.CurrentTick > CurrentTick)
                        CurrentTick = _simulation.CurrentTick;

                    // Dispatch events from the range that was successfully verified before the desync.
                    if (lastVerifiedTick >= 0)
                        DispatchVerifiedEventsPartial(firstExecutionTick, lastVerifiedTick);

                    for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                        EventPool.Return(_rollbackOldEventsCache[i]);
                    _rollbackOldEventsCache.Clear();
                    return;
                }

                // Promote to Verified. entry.Tick is _frame.Tick after execution.
                _lastVerifiedTick = entry.Tick;
                _lastServerVerifiedTick = entry.Tick;
                lastVerifiedTick = executionTick;

                // Record only commands whose hash validation succeeded as confirmed in the replay.
                if (_replaySystem.IsRecording)
                    _replaySystem.RecordTick(executionTick, entry.Commands);

                OnFrameVerified?.Invoke(executionTick);

                // When executionTick transitions to verified, dispatch the buffered Synced events exactly once.
                DispatchSyncedEventsForTick(executionTick, _eventBuffer.GetEvents(executionTick));
            }

            // Save a snapshot of the resulting state so the next ProcessVerifiedBatch can roll back to this tick.
            if (lastVerifiedTick >= 0)
                SaveSnapshot(_simulation.CurrentTick);

            // If verified has advanced past predicted, synchronize CurrentTick.
            if (_simulation.CurrentTick > CurrentTick)
                CurrentTick = _simulation.CurrentTick;

            // Prediction resimulation runs only once per batch.
            if (lastVerifiedTick >= 0 && lastVerifiedTick + 1 < CurrentTick)
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                _logger?.ZLogDebug($"[SD][DIAG] PredResim: range=[{lastVerifiedTick + 1},{CurrentTick - 1}] depth={CurrentTick - lastVerifiedTick - 1} activeIds=[{string.Join(",", _activePlayerIds)}]");
#endif
                int resimTick = lastVerifiedTick + 1;
                while (resimTick < CurrentTick)
                {
                    SaveSnapshot(resimTick);

                    _tickCommandsCache.Clear();
                    var received = _inputBuffer.GetCommandList(resimTick);
                    for (int i = 0; i < received.Count; i++)
                        _tickCommandsCache.Add(received[i]);

                    for (int pi = 0; pi < _activePlayerIds.Count; pi++)
                    {
                        int playerId = _activePlayerIds[pi];
                        if (!_inputBuffer.HasCommandForTick(resimTick, playerId))
                        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                            if (playerId == LocalPlayerId)
                                _logger?.ZLogWarning($"[SD] PredResim: local input missing, using predictor: resimTick={resimTick}, localId={LocalPlayerId}");
#endif
                            GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT, resimTick);
                            var predicted = _inputPredictor.PredictInput(playerId, resimTick, _previousCommandsCache);
                            _tickCommandsCache.Add(predicted);
                        }
                    }

                    _eventCollector.BeginTick(resimTick);
                    _tickCommandsCache.Sort(s_commandComparer);
#if DEBUG
                    _inputBuffer.SetResimulating(true);
#endif
                    _simulation.Tick(_tickCommandsCache);
#if DEBUG
                    _inputBuffer.SetResimulating(false);
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.ZLogDebug($"[SD][HASH] ResimTick: tick={resimTick} hash=0x{_simulation.GetStateHash():X16}");
#endif

                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(resimTick, _eventCollector.Collected[ei]);

                    resimTick++;
                }
            }

            // Reconcile Canceled/Predicted/Confirmed via event diff against the previous prediction.
            DiffRollbackEvents(firstExecutionTick);

            for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                EventPool.Return(_rollbackOldEventsCache[i]);
            _rollbackOldEventsCache.Clear();
        }

        /// <summary>
        /// When the batch is terminated early due to a desync, dispatch only the events from the successfully verified range.
        /// Compare with the previous predicted event cache and emit only new events as Confirmed.
        /// </summary>
        private void DispatchVerifiedEventsPartial(int fromTick, int toTickInclusive)
        {
            for (int t = fromTick; t <= toTickInclusive; t++)
            {
                var events = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var evt = events[ei];

                    // Synced events are already dispatched in the verified resim loop, so skip them.
                    if (evt.Mode == EventMode.Synced) continue;

                    bool foundInOld = false;
                    long hash = evt.GetContentHash();
                    for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
                    {
                        var oldEvt = _rollbackOldEventsCache[oi];
                        if (oldEvt.Tick == evt.Tick &&
                            oldEvt.EventTypeId == evt.EventTypeId &&
                            oldEvt.GetContentHash() == hash)
                        {
                            foundInOld = true;
                            break;
                        }
                    }
                    if (!foundInOld)
                        _dispatcher.Dispatch(OnEventConfirmed, evt.Tick, evt, nameof(OnEventConfirmed));
                }
            }

            // Cancel any previously predicted events in the verified range that are not present in the new events
            for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
            {
                var oldEvt = _rollbackOldEventsCache[oi];
                if (oldEvt.Mode != EventMode.Regular) continue;
                if (oldEvt.Tick < fromTick || oldEvt.Tick > toTickInclusive) continue;

                bool found = false;
                long oldHash = oldEvt.GetContentHash();
                for (int t = fromTick; t <= toTickInclusive; t++)
                {
                    var events = _eventBuffer.GetEvents(t);
                    for (int ei = 0; ei < events.Count; ei++)
                    {
                        var newEvt = events[ei];
                        if (newEvt.Tick == oldEvt.Tick &&
                            newEvt.EventTypeId == oldEvt.EventTypeId &&
                            newEvt.GetContentHash() == oldHash)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
                if (!found)
                    _dispatcher.Dispatch(OnEventCanceled, oldEvt.Tick, oldEvt, nameof(OnEventCanceled));
            }
        }

        // ── SD lead tick helpers ────────────────────────────────

        private int ComputeSDInputLeadTicks() => _simConfig.GetEffectiveSDInputLeadTicks();

        private void ApplySDWarmUpLead()
        {
            int targetLead = ComputeSDInputLeadTicks();
            int currentLead = CurrentTick - _lastServerVerifiedTick;
            int deficit = targetLead - currentLead;
            if (deficit > 0)
                _accumulator += deficit * _simConfig.TickIntervalMs;
        }

        // ── SD event handlers ────────────────────────────────

        /// <summary>
        /// VerifiedStateMessage receive handler.
        /// Catchup/spectator stores directly into the InputBuffer, while normal Playing enqueues to be processed in bulk by ProcessVerifiedBatch.
        /// </summary>
        private void HandleVerifiedStateReceived(int tick, IReadOnlyList<ICommand> confirmedInputs, long stateHash)
        {
            // Defensive guard for cases where we are neither Playing yet nor in catchup.
            if (!_isCatchingUp && State != KlothoState.Running)
                return;

            // Update the drift/delivery EMA only on the normal SD Client path.
            // Catchup/spectator have irregular batch intervals that would pollute the EMA, so skip them.
            if (!_isCatchingUp && !_isSpectatorMode && IsSDClient)
                AdaptiveClock.OnVerifiedBatchArrived(
                    System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    _simConfig.TickIntervalMs);

            if (_isCatchingUp || _isSpectatorMode)
            {
                for (int i = 0; i < confirmedInputs.Count; i++)
                    _inputBuffer.AddCommand(confirmedInputs[i]);

                if (_isCatchingUp)
                    ConfirmCatchupTick(tick - 1);
                if (_isSpectatorMode)
                    ConfirmSpectatorTick(tick - 1);
            }
            else
            {
                // Discard messages with ticks already confirmed via FullState restore.
                if (tick <= _lastVerifiedTick)
                    return;

                // confirmedInputs is a shared list, so copy it into a separate list before enqueueing.
                var commands = new List<ICommand>(confirmedInputs.Count);
                for (int i = 0; i < confirmedInputs.Count; i++)
                    commands.Add(confirmedInputs[i]);

                _pendingVerifiedQueue.Enqueue(new VerifiedStateEntry
                {
                    Tick = tick,
                    Commands = commands,
                    StateHash = stateHash
                });
            }
        }

        /// <summary>
        /// InputAck receive handler. The retransmit queue is already cleaned up by the network service.
        /// </summary>
        private void HandleInputAckReceived(int ackedTick)
        {
            // No additional processing at the engine layer.
        }

        /// <summary>
        /// Server FullState receive handler.
        /// Branches into one of: initial FullState unblock / Late Join catchup / determinism failure or Reconnect recovery,
        /// based on the combination of _expectingInitialFullState / _expectingFullState flags.
        /// </summary>
        private void HandleServerDrivenFullStateReceived(int tick, byte[] stateData, long stateHash)
        {
            // Handle the initial FullState unblock path with the highest priority.
            if (_expectingInitialFullState)
            {
                HandleInitialFullStateReceived(tick, stateData, stateHash);
                return;
            }

            // Late Join path: restore state and then enter catchup mode.
            if (_expectingFullState)
            {
                _expectingFullState = false;
                ApplyFullState(tick, stateData, stateHash, ApplyReason.LateJoin);
                SaveSnapshot(tick);
                _inputBuffer.Clear();
                _lastServerVerifiedTick = tick;
                _lastVerifiedTick = tick;
                _serverDrivenNetwork.ClearUnackedInputs();
                _pendingVerifiedQueue.Clear();
                _posDeltas.Clear();
                _yawDeltas.Clear();
                _teleportedEntities.Clear();

                // In catchup mode, HandleVerifiedStateReceived stores directly into the InputBuffer.
                StartCatchingUp();
                _logger?.ZLogInformation($"[KlothoEngine][SD] Late Join FullState received, starting catchup: tick={tick}");
                return;
            }

            int previousTick = CurrentTick;

            // Determinism failure or Reconnect recovery path.
            ApplyFullState(tick, stateData, stateHash, ApplyReason.ResyncRequest);

            // Preserve local inputs that are not yet confirmed.
            _inputBuffer.ClearBefore(tick);

            _fullStateRequestPending = false;
            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();
            _lastServerVerifiedTick = tick;
            _lastVerifiedTick = tick;

            _serverDrivenNetwork.ClearUnackedInputs();

            // After FullState restore the ring buffer is cleared, so any VerifiedState remaining in the queue has no valid snapshot.
            _pendingVerifiedQueue.Clear();

            // On Reconnect, if previousTick is smaller than tick there is no range to resimulate.
            if (tick + 1 < previousTick)
            {
                int resimTick = tick + 1;
                while (resimTick < previousTick)
                {
                    SaveSnapshot(resimTick);

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
                        }
                    }

                    _eventCollector.BeginTick(resimTick);
                    _tickCommandsCache.Sort(s_commandComparer);
#if DEBUG
                    _inputBuffer.SetResimulating(true);
#endif
                    _simulation.Tick(_tickCommandsCache);
#if DEBUG
                    _inputBuffer.SetResimulating(false);
#endif
                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(resimTick, _eventCollector.Collected[ei]);

                    resimTick++;
                }

                // After resimulation, synchronize CurrentTick so it does not diverge from _frame.Tick.
                CurrentTick = previousTick;
            }

            _consumePendingDeltaTime = true;
            ApplySDWarmUpLead();
            OnResyncCompleted?.Invoke(tick);

            _logger?.ZLogInformation(
                $"[KlothoEngine][SD] FullState restore complete: serverTick={tick}, previousTick={previousTick}");
        }

        /// <summary>
        /// SD Client initial FullState receive handler.
        /// On session start, corrects the local state with the authoritative initial state broadcast by the server, and embeds it in the replay as well.
        /// </summary>
        private void HandleInitialFullStateReceived(int tick, byte[] stateData, long stateHash)
        {
            _expectingInitialFullState = false;
            ApplyFullState(tick, stateData, stateHash, ApplyReason.InitialFullState);
            _lastServerVerifiedTick = tick;
            _replaySystem.SetInitialStateSnapshot(stateData, stateHash);
            // ApplyFullState resets _accumulator to 0, so re-establish the warm-up lead.
            ApplySDWarmUpLead();
            _logger?.ZLogInformation(
                $"[KlothoEngine][SD] Initial FullState applied: tick={tick}, size={stateData.Length}");

            // Signal bootstrap-ready so the server can proceed to first tick once all peers have ack'd.
#if KLOTHO_FAULT_INJECTION
            // Suppress this client's bootstrap-ready ack to exercise the server-side
            // BOOTSTRAP_TIMEOUT_MS path (FullState resync).
            if (xpTURN.Klotho.Diagnostics.FaultInjection.SuppressBootstrapAckPlayerIds.Contains(LocalPlayerId))
            {
                _logger?.ZLogWarning($"[FaultInjection][SD] Bootstrap ack suppressed: playerId={LocalPlayerId}, tick={tick}");
                return;
            }
#endif
            _serverDrivenNetwork.SendBootstrapReady(LocalPlayerId);
        }

        /// <summary>
        /// SD Client BootstrapBegin handler. Aligns _accumulator to the server's actual tick start
        /// time so the warm-up lead is preserved through the bootstrap window — matters most on the timeout path
        /// where the server may have been waiting up to BOOTSTRAP_TIMEOUT_MS before broadcasting.
        /// </summary>
        private void HandleBootstrapBegin(int firstTick, long tickStartTimeMs)
        {
            // Defensive guard. Under BootstrapPending + the CompleteBootstrap → broadcast → first tick
            // send order, server CurrentTick is 0 at broadcast time
            // regardless of timeout policy, so mismatch is not expected in normal flow. The guard
            // protects against implementation regression / cross-version skew / future timeout policy
            // changes — wait for a follow-up FullState resync to realign.
            if (firstTick != _lastServerVerifiedTick)
            {
                _logger?.ZLogWarning(
                    $"[KlothoEngine][SD] BootstrapBegin tick mismatch: firstTick={firstTick}, _lastServerVerifiedTick={_lastServerVerifiedTick} — awaiting FullState resync");
                return;
            }

            // Re-anchor _accumulator to the server's actual tick start. SharedTimeClock is an immutable
            // struct; reading SharedNow gives the current shared clock value comparable to the broadcast's
            // tickStartTimeMs.
            long elapsedSinceStart = _serverDrivenNetwork.SharedClock.SharedNow - tickStartTimeMs;
            if (elapsedSinceStart > 0)
            {
                long maxAccumMs = (long)_simConfig.TickIntervalMs * MAX_TICKS_PER_UPDATE;
                long clamped = Math.Min(elapsedSinceStart, maxAccumMs);
                _accumulator = (float)clamped;
            }
            // Re-establish the warm-up lead (ApplySDWarmUpLead is idempotent — only adds when deficit > 0).
            ApplySDWarmUpLead();

            _logger?.ZLogInformation(
                $"[KlothoEngine][SD] BootstrapBegin applied: firstTick={firstTick}, tickStartTimeMs={tickStartTimeMs}, elapsed={elapsedSinceStart}ms, accumulator={_accumulator:F1}ms");
        }

        // Forwards transport-level rejection notifications to the engine-public event so game code can react.
        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            _logger?.ZLogInformation($"[KlothoEngine][SD] CommandRejected: tick={tick}, cmdTypeId={cmdTypeId}, reason={reason}");
            OnCommandRejected?.Invoke(tick, cmdTypeId, reason);
        }
    }
}
