using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Diagnostics;
using xpTURN.Klotho.Input;
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

        // Highest tick ever enqueued to _pendingVerifiedQueue. Together with
        // _lastVerifiedTick this makes the enqueue guard reject same-tick duplicates that are
        // still queued — the proof that every queued container holds instances no other
        // container shares, which is what allows DrainPendingVerifiedQueue to Return them.
        // Reset to the restore tick at the two FullState-restore sites (alongside
        // _lastVerifiedTick); intentionally NOT touched by the rollback-failure drain, which
        // does not move _lastVerifiedTick (lowering the watermark there would only weaken
        // monotonicity). VerifiedState is ReliableOrdered, so this never fires normally.
        private int _lastEnqueuedVerifiedTick;

        // SD FullState request timeout/retry. The P2P resync ladder
        // (_resyncState / CheckResyncTimeout) is unreachable on the SD path (KlothoEngine.cs:828-836
        // returns before that block), so without these the request has no re-arm timer — a lost
        // response or connection reset leaves _fullStateRequestPending stuck true forever, freezing
        // ProcessVerifiedBatch. Reuses RESYNC_TIMEOUT_MS / ResyncMaxRetries (no SD-only constants).
        private float _fullStateRequestElapsedMs;
        private int _fullStateRequestRetryCount;

        // Set while a reconnect (ReconnectTimeoutMs budget) is in progress with a FullState request
        // outstanding: suspends the retry/abort timer so the shorter FullState abort window cannot
        // preempt a recoverable reconnect. Cleared by ClearFullStateRequestState (a reconnect that
        // succeeds consumes a server FullState, which routes through that clear) and by the terminal
        // abort when the reconnect ultimately fails.
        private bool _fullStateRequestPausedForReconnect;

        // Per-entry scratch for verified-batch instances the buffer rejected (Dropped*) —
        // returned to CommandPool at the entry consumption point, after the last
        // entry.Commands use (RecordTick). In practice always empty (SD has no seals and
        // same-instance re-entry is closed by the Initialize/enqueue guards) — insurance.
        private readonly List<ICommand> _rejectedVerifiedCmdsCache = new List<ICommand>();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Per-tick perf measurement. Logs only when an iteration exceeds TickIntervalMs.
        private readonly System.Diagnostics.Stopwatch _perfSw = new System.Diagnostics.Stopwatch();
#endif

        private struct VerifiedStateEntry
        {
            public int Tick;
            public List<ICommand> Commands; // ListPool container; commands are queue-owned until stored into the InputBuffer — both returned at per-entry consumption / queue drain.
            public long StateHash;
        }

        // Drains the pending verified queue, returning each entry's ICommand instances and
        // ListPool container. Provably safe:
        // queued entries are pre-store (commands enter the InputBuffer only during
        // ProcessVerifiedBatchCore), so the queue is the sole owner and the drained set is
        // disjoint from the adjacent buffer cleanups (Clear/ClearBefore) at the call sites;
        // duplicate containers sharing instances are excluded by the enqueue dedup guard +
        // the Initialize re-entry guard.
        private void DrainPendingVerifiedQueue()
        {
            while (_pendingVerifiedQueue.Count > 0)
            {
                var commands = _pendingVerifiedQueue.Dequeue().Commands;
                for (int i = 0; i < commands.Count; i++)
                    CommandPool.Return(commands[i]);
                ListPool<ICommand>.Return(commands);
            }
        }

        // Clears the SD FullState-request gate AND its retry timer in one place.
        // Every _fullStateRequestPending=false site must go through here so the timer state
        // (elapsed/retry) never lingers across a completed/abandoned request.
        private void ClearFullStateRequestState()
        {
            _fullStateRequestPending = false;
            _fullStateRequestElapsedMs = 0f;
            _fullStateRequestRetryCount = 0;
            _fullStateRequestPausedForReconnect = false;
        }

        // SD-local terminal for an unrecoverable resync — retry budget
        // exhausted or a post-apply hash mismatch (non-deterministic deserialize that
        // re-requesting cannot fix). SD is server-authoritative, so the P2P rung-3 host hand-off
        // (FullStateResync.cs RegisterDesyncForEscalation) does not apply — the client decides
        // locally. Mirrors the P2P retry-exhaustion branch (FullStateResync.cs:201-210).
        private void HandleSdResyncFailure(AbortReason reason)
        {
            ClearFullStateRequestState();
            OnResyncFailed?.Invoke();
            if (_simConfig.AutoAbortOnRecoveryExhausted)
                AbortMatch(reason);
            // else: game layer decides (teardown/reconnect). Engine is left as-is; OnResyncFailed
            // is the signal. For HashMismatch the state is restored-but-untrusted, for exhaustion
            // the last request simply went unanswered.
        }

        // SD FullState-request timeout. While a request is outstanding,
        // ProcessVerifiedBatch early-returns; without this re-arm a lost response / connection reset
        // would freeze verified processing forever. Mirrors P2P CheckResyncTimeout, which the SD path
        // never reaches (KlothoEngine.cs:828-836 returns before that block). Constants shared with P2P.
        // Returns true if the match was aborted (caller must skip the tick loop).
        private bool CheckSdFullStateRequestTimeout(float deltaTime)
        {
            if (!_fullStateRequestPending)
                return false;

            // A reconnect is in progress — suspend the retry/abort countdown so the shorter FullState
            // abort window does not preempt the reconnect budget. The pause is lifted either by
            // ClearFullStateRequestState when the reconnect succeeds, or by HandleSdReconnectFailed
            // when it ultimately fails.
            if (_fullStateRequestPausedForReconnect)
                return false;

            _fullStateRequestElapsedMs += deltaTime * 1000f;
            if (_fullStateRequestElapsedMs >= RESYNC_TIMEOUT_MS)
            {
                if (_fullStateRequestRetryCount >= _simConfig.ResyncMaxRetries)
                {
                    _logger?.KError(
                        $"[KlothoEngine][SD] FullState request unanswered after {_fullStateRequestRetryCount} retries — terminating");
                    HandleSdResyncFailure(AbortReason.StateDivergence);
                }
                else
                {
                    _fullStateRequestRetryCount++;
                    _fullStateRequestElapsedMs = 0f;
                    // Server answers with its own authoritative CurrentTick regardless of the requested
                    // tick (provider invariant), so the verified frontier is the clearest re-request anchor.
                    _serverDrivenNetwork.SendFullStateRequest(_lastServerVerifiedTick + 1);
                    _logger?.KWarning(
                        $"[KlothoEngine][SD] FullState request timed out, retry {_fullStateRequestRetryCount}/{_simConfig.ResyncMaxRetries}");
                }
            }

            // Exhaustion may have aborted the match — signal the caller to skip the prediction loop.
            return State != KlothoState.Running;
        }

        // SD reconnect interrupt — a recoverable disconnect (NetworkFailure) started a reconnect.
        // While a FullState request is outstanding, suspend its retry/abort timer so the abort
        // window cannot preempt the reconnect (up to ReconnectTimeoutMs). No-op when nothing is
        // pending (a new request cannot start mid-reconnect — transport is down), and idempotent
        // across repeated signals. Cleared by ClearFullStateRequestState / the terminal abort.
        private void HandleSdReconnecting()
        {
            if (_fullStateRequestPending)
                _fullStateRequestPausedForReconnect = true;
        }

        // SD reconnect terminal failure — reconnect exhausted retries / timed out / was rejected
        // (all four service paths converge on OnReconnectFailed). Act only when a FullState request
        // was outstanding (the freeze this targets); otherwise reconnect-failure handling stays with
        // the game layer (IKlothoSessionObserver.OnReconnectFailed), unchanged. HandleSdResyncFailure
        // is idempotent (it clears the pending gate), so a duplicate/late signal no-ops.
        private void HandleSdReconnectFailed(ReconnectRejectReason reason)
        {
            if (!_fullStateRequestPending)
                return;
            _logger?.KWarning(
                $"[KlothoEngine][SD] Reconnect failed ({reason}) with FullState request outstanding — terminating");
            HandleSdResyncFailure(AbortReason.ReconnectFailed);
        }

        // ── Client tick loop ──────────────────────────

        /// <summary>
        /// SD Client tick loop.
        /// Proceeds in order: lead tick control → prediction sim → ProcessVerifiedBatch → cleanup.
        /// </summary>
        private void UpdateServerDrivenClient(float deltaTime)
        {
            ClearErrorDeltas();

            // re-arm timer for an outstanding FullState request. Returns true if the
            // match was aborted (retry budget exhausted) — skip the tick loop in that case.
            if (CheckSdFullStateRequestTimeout(deltaTime))
                return;

            // Lead tick control: slow down at the soft threshold, wait at the hard limit.
            int leadTicks = CurrentTick - _lastServerVerifiedTick;
            int targetLead = ComputeSDInputLeadTicks();
            // Prediction lead is bounded by what the snapshot ring can restore:
            // the ring retains GetSnapshotCapacity() (= MaxRollbackTicks + 2) ticks, so a hard
            // limit above MaxRollbackTicks let the restore target fall off the ring and degraded
            // every late batch to a FullState resync. targetLead < hardLimit is guaranteed by
            // the GetEffectiveSDInputLeadTicks clamp.
            int hardLimit = _simConfig.MaxRollbackTicks;
            int softThreshold = targetLead + (hardLimit - targetLead) / 2;

            if (_consumePendingDeltaTime)
            {
                _consumePendingDeltaTime = false;
                _logger?.KDebug($"[KlothoEngine] Pending deltaTime consumed: {deltaTime * 1000f:F1}ms dropped at tick={CurrentTick}");
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
                    // Demote to Debug once OnMatchEnded has fired — server's EndGracePolicy.Pause
                    // freezes verified-tick advance, so client prediction naturally hits hardLimit
                    // until ClientShutdownGraceMs expires. Known trade-off, not a stall.
                    if (_matchEndedDispatched)
                        _logger?.KDebug($"[KlothoEngine] ClientTick: Hard limit reached (post-match-end): currentTick={CurrentTick}, lastVerifiedTick={_lastServerVerifiedTick}, leadTicks={leadTicks}, hardLimit={hardLimit}");
                    else
                        _logger?.KWarning($"[KlothoEngine] ClientTick: Hard limit reached: currentTick={CurrentTick}, lastVerifiedTick={_lastServerVerifiedTick}, leadTicks={leadTicks}, hardLimit={hardLimit}");
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
                            _logger?.KWarning($"[KlothoEngine] Tick gap: {gap}ms (expected {_simConfig.TickIntervalMs}ms), tick={CurrentTick}");
                    }
                    _lastTickWallMs = nowMs;
                }
#endif

                // Pause-grace auto-stop — emit StopCommand instead of game OnPollInput.
                // Unify the gate expression with P2P (latch START + state RELEASE, see KlothoEngine.cs).
                // SD's Removed is inert, but keep the gate semantics consistent.
                if (_matchEndedDispatched
                    && _simulation.IsMatchEndedState
                    && _sessionConfig.EndGracePolicy == EndGracePolicy.Pause)
                {
                    var stop = CommandPool.Get<StopCommand>();
                    InputCommand(stop);
                }
                else if (_simulationCallbacks != null)
                {
                    _simulationCallbacks.OnPollInput(LocalPlayerId, CurrentTick, _commandSender);
                }
                else
                {
                    OnPreTick?.Invoke(CurrentTick);
                }

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
                _logger?.KWarning($"[KlothoEngine][SD] Tick desync: CurrentTick={CurrentTick}, frame.Tick={frameTick}");

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
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    var bufRange = _inputBuffer.GetBufferedTickRange(playerId);
                    _logger?.KDebug($"[SD][PredSource] tick={CurrentTick} targetPlayerId={playerId} selfPeerId={LocalPlayerId} bufferedTickLo={bufRange.lo} bufferedTickHi={bufRange.hi}");
#endif
                }
            }

            _eventCollector.BeginTick(CurrentTick);
            _tickCommandsCache.Sort(s_commandComparer);
            _simulation.Tick(_tickCommandsCache);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Prediction-tick hash is dev-only diagnostics; NOT recorded into the history — the
            // verified resim re-executes and re-records this tick, so a predicted value would be
            // overwritten anyway and buys no prod signal.
            _logger?.KDebug($"[SD][HASH] PredTick: tick={CurrentTick} hash=0x{_simulation.GetStateHash():X16}");
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
                _logger?.KWarning(
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
                _logger?.KWarning(
                    $"[SD][PERF] VerifiedBatch slow: {elapsedMs}ms (tickInterval={_simConfig.TickIntervalMs}ms), batchCount={batchCount}, currentTick={CurrentTick}");
#endif
        }

        private void ProcessVerifiedBatchCore()
        {
            int batchCount = _pendingVerifiedQueue.Count;

            _logger?.KDebug($"[KlothoEngine][SD] ProcessVerifiedBatch: batchCount={batchCount}, currentTick={CurrentTick}, lastVerifiedTick={_lastServerVerifiedTick}");

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

            // Pre-batch dispatched-Synced boundary — captured before the verified
            // promotion loop raises the watermark. SD batches start above the prior watermark
            // (firstExecutionTick > old _lastVerifiedTick >= watermark), so the diff's divergence
            // passes are naturally inert on this path; captured for contract clarity.
            int dispatchedSyncedMark = _syncedDispatchHighWaterMark;

            // Restore state from the nearest verified snapshot.
            bool rollbackPerformed = false;
            if (restoreTick >= 0)
            {
                int actualRestoreTick = _simulation.GetNearestRollbackTick(restoreTick);

                // If no snapshot exists or it is too old, request a FullState and discard this batch.
                if (actualRestoreTick < 0 || actualRestoreTick < firstExecutionTick - _simConfig.MaxRollbackTicks)
                {
                    _logger?.KWarning(
                        $"[KlothoEngine][SD] Rollback failed: no valid snapshot for restoreTick={restoreTick} (nearest={actualRestoreTick}), requesting FullState");

                    if (!_fullStateRequestPending)
                    {
                        _serverDrivenNetwork.SendFullStateRequest(firstExecutionTick);
                        _fullStateRequestPending = true;
                        _fullStateRequestElapsedMs = 0f; // arm timeout on first request
                    }
                    DrainPendingVerifiedQueue();

                    for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                        EventPool.Return(_rollbackOldEventsCache[i]);
                    _rollbackOldEventsCache.Clear();
                    return;
                }
                else
                {
                    restoreTick = actualRestoreTick;
                    _logger?.KDebug($"[KlothoEngine][SD] Rollback: restoreTick={restoreTick}, frame.Tick before={_simulation.CurrentTick}");
                    _simulation.Rollback(restoreTick);
                    _logger?.KDebug($"[KlothoEngine][SD] Rollback: frame.Tick after={_simulation.CurrentTick}");
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[SD][DIAG] PostRestore: restoreTick={restoreTick} hash=0x{_simulation.GetStateHash():X16}");
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

            // If the restored tick is earlier than the first execution tick, fill the gap.
            // Uses the shared helper so missing remote players are substituted with EmptyCommand
            // (mirrors server-side CollectTickInputs and guarantees hash parity).
            if (rollbackPerformed && restoreTick < firstExecutionTick)
            {
                int gapTick = restoreTick;
                while (gapTick < firstExecutionTick)
                {
                    SimulateGapTickWithEmptyFallback(gapTick);
                    gapTick++;
                }
            }

            // Resimulate the verified ticks in order while validating the state hash.
            int lastVerifiedTick = -1;
            while (_pendingVerifiedQueue.Count > 0)
            {
                var entry = _pendingVerifiedQueue.Dequeue();
                int executionTick = entry.Tick - 1; // input tick at the time of execution

                int simTickAtEntry = _simulation.CurrentTick;
                if (simTickAtEntry < executionTick)
                {
                    _logger?.KWarning($"[SD][ResimGap] currentSimTick={simTickAtEntry} executionTick={executionTick} gap={executionTick - simTickAtEntry} entryTick={entry.Tick} batchRemaining={_pendingVerifiedQueue.Count}");
                }

                // Overwrite the predicted input in the InputBuffer with the verified input.
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                {
                    bool hasLocal = false;
                    for (int i = 0; i < entry.Commands.Count; i++)
                    {
                        if (entry.Commands[i].PlayerId == LocalPlayerId) hasLocal = true;
                    }
                    if (!hasLocal)
                        _logger?.KWarning($"[SD] Verified entry missing local input: executionTick={executionTick}, localId={LocalPlayerId}, entryCmds={entry.Commands.Count}");
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
                                    _logger?.KWarning($"[SD][DIAG] CmdDiff: executionTick={executionTick} pid={vc.PlayerId} predicted={ec.GetType().Name}(tick={ec.Tick}) verified={vc.GetType().Name}(tick={vc.Tick})");
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            _logger?.KWarning($"[SD][DIAG] CmdMissing: executionTick={executionTick} pid={vc.PlayerId} verified={vc.GetType().Name}(tick={vc.Tick})");
                    }
                }
#endif
                _rejectedVerifiedCmdsCache.Clear();
                for (int i = 0; i < entry.Commands.Count; i++)
                {
                    var storeResult = _inputBuffer.AddCommandChecked(entry.Commands[i], overwriteExisting: true);
                    // Buffer-rejected instances stay queue-owned — collect for a deferred
                    // CommandPool.Return at the entry consumption point (after RecordTick,
                    // the last entry.Commands use). AlreadyStored/Replaced are buffer-owned.
                    if (storeResult == CommandStoreResult.DroppedDuplicate || storeResult == CommandStoreResult.DroppedSealed)
                        _rejectedVerifiedCmdsCache.Add(entry.Commands[i]);
                }

                // Verified resimulation
                SaveSnapshot(executionTick);
                _tickCommandsCache.Clear();
                var cmds = _inputBuffer.GetCommandList(executionTick);
                if (cmds.Count < entry.Commands.Count)
                {
                    _logger?.KWarning(
                        $"[SD] Command lookup mismatch: executionTick={executionTick}, entry.Tick={entry.Tick}, expected={entry.Commands.Count}, found={cmds.Count}");
                }
                for (int i = 0; i < cmds.Count; i++)
                    _tickCommandsCache.Add(cmds[i]);

                _logger?.KDebug($"[SD] Resim: executionTick={executionTick}, entry.Tick={entry.Tick}, frame.Tick before={_simulation.CurrentTick}, cmds={_tickCommandsCache.Count}");
                _eventCollector.BeginTick(executionTick);
                _tickCommandsCache.Sort(s_commandComparer);
#if DEBUG || DEVELOPMENT_BUILD
                _inputBuffer.SetResimulating(true);
#endif
                _simulation.Tick(_tickCommandsCache);
#if DEBUG || DEVELOPMENT_BUILD
                _inputBuffer.SetResimulating(false);
#endif
                _logger?.KDebug($"[SD] Resim: frame.Tick after={_simulation.CurrentTick}");
                for (int ei = 0; ei < _eventCollector.Count; ei++)
                    _eventBuffer.AddEvent(executionTick, _eventCollector.Collected[ei]);

                // Hash validation
                long resimHash = _simulation.GetStateHash();
                // Verified resim hashes every tick, so recording is a free byproduct. This is
                // the SD client's prod accumulation path (the prediction tick is intentionally skipped).
                _diagHistorySim?.RecordHashHistory(executionTick);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                _logger?.KDebug($"[SD][DIAG] VerifiedHash: executionTick={executionTick} hash=0x{resimHash:X16}");
#endif
                if (resimHash != entry.StateHash)
                {
                    // Hash mismatch — determinism is broken, so request a FullState.
                    _logger?.KError(
                        $"[KlothoEngine][SD] Determinism failure: tick={entry.Tick}, local=0x{resimHash:X16}, server=0x{entry.StateHash:X16}");

                    // Command-digest classification — the SD client holds both its executed set (_tickCommandsCache)
                    // and the server's confirmed set (entry.Commands), so it compares locally with no new
                    // message. Differing digests ⇒ the confirmed inputs the client resimulated were
                    // not the server's (input propagation/buffer — upgrades the former count-only warning);
                    // equal digests ⇒ same inputs, diverged result (determinism violation).
                    long localCmdDigest = ComputeCommandDigest(_tickCommandsCache);
                    long serverCmdDigest = ComputeCommandDigest(entry.Commands);
                    string desyncClass = localCmdDigest != serverCmdDigest ? "InputDivergence" : "StateDivergence";
                    _logger?.KError(
                        $"[Desync][Diag] tick={entry.Tick} class={desyncClass} " +
                        $"local.cmd=0x{localCmdDigest:X16} server.cmd=0x{serverCmdDigest:X16}");

                    // Ask the server for its breakdown at this tick so the layer can be named, and
                    // report the conclusion back so it lands in the SERVER's log. The class is already
                    // settled above (both digests are in hand), so only the layer is missing — that is why
                    // there is no tick-search round trip here, unlike P2P.
                    //
                    // The scalars are captured now, inside this branch: the response is an RTT away, and by
                    // then _tickCommandsCache has been refilled by the next verified tick and the commands
                    // it pointed at have gone back to the pool.
                    //
                    // executionTick, not entry.Tick: BOTH history rings key this state under the tick that
                    // PRODUCED it, while the wire calls it entry.Tick = executionTick + 1. Asking the server
                    // for entry.Tick misses our own capture and reads the wrong slot out of the server's.
                    TryBeginDesyncProbeSD(executionTick, entry.Tick, resimHash, entry.StateHash,
                        localCmdDigest != serverCmdDigest ? DesyncClass.Input : DesyncClass.State);

                    // Diagnostic — surface the diverging layer + recent history locally, in prod too.
                    // The live sim is exactly at the diverged tick here, so LogStateBreakdown
                    // pinpoints the component/system layer directly (includes the system layer, the old
                    // per-component dump did not); FlushHashHistory dumps the surrounding ticks. Both
                    // no-op cheaply when the log level / history are disabled.
                    if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                    {
                        if (!_fullStateRequestPending)
                            ecsSimDiag.FlushHashHistory(_logger, entry.Tick);
                        ecsSimDiag.LogStateBreakdown(_logger, "DesyncLocal");
                        ecsSimDiag.LogStaticFingerprint(_logger, "DesyncLocal");
                    }

                    if (!_fullStateRequestPending)
                    {
                        _serverDrivenNetwork.SendFullStateRequest(entry.Tick);
                        _fullStateRequestPending = true;
                        _fullStateRequestElapsedMs = 0f; // arm timeout on first request
                    }

                    // The remaining queued entries are intentionally left in place; they are
                    // discarded by the FullState-restore handler (the restore clears the snapshot
                    // ring, so entries at ticks <= the restore tick have no valid rollback target
                    // and nothing is lost by dropping them there).
                    for (int i = 0; i < _rejectedVerifiedCmdsCache.Count; i++)
                        CommandPool.Return(_rejectedVerifiedCmdsCache[i]);
                    _rejectedVerifiedCmdsCache.Clear();
                    ListPool<ICommand>.Return(entry.Commands);

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

                for (int i = 0; i < _rejectedVerifiedCmdsCache.Count; i++)
                    CommandPool.Return(_rejectedVerifiedCmdsCache[i]);
                _rejectedVerifiedCmdsCache.Clear();
                ListPool<ICommand>.Return(entry.Commands);
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
                _logger?.KDebug($"[SD][DIAG] PredResim: range=[{lastVerifiedTick + 1},{CurrentTick - 1}] depth={CurrentTick - lastVerifiedTick - 1} activeIds=[{string.Join(",", _activePlayerIds)}]");
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
                                _logger?.KWarning($"[SD] PredResim: local input missing, using predictor: resimTick={resimTick}, localId={LocalPlayerId}");
#endif
                            GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT, resimTick);
                            var predicted = _inputPredictor.PredictInput(playerId, resimTick, _previousCommandsCache);
                            _tickCommandsCache.Add(predicted);
                        }
                    }

                    _eventCollector.BeginTick(resimTick);
                    _tickCommandsCache.Sort(s_commandComparer);
#if DEBUG || DEVELOPMENT_BUILD
                    _inputBuffer.SetResimulating(true);
#endif
                    _simulation.Tick(_tickCommandsCache);
#if DEBUG || DEVELOPMENT_BUILD
                    _inputBuffer.SetResimulating(false);
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[SD][HASH] ResimTick: tick={resimTick} hash=0x{_simulation.GetStateHash():X16}");
#endif

                    for (int ei = 0; ei < _eventCollector.Count; ei++)
                        _eventBuffer.AddEvent(resimTick, _eventCollector.Collected[ei]);

                    resimTick++;
                }
            }

            // Reconcile Canceled/Predicted/Confirmed via event diff against the previous prediction.
            DiffRollbackEvents(firstExecutionTick, dispatchedSyncedMark);

            for (int i = 0; i < _rollbackOldEventsCache.Count; i++)
                EventPool.Return(_rollbackOldEventsCache[i]);
            _rollbackOldEventsCache.Clear();
        }

        /// <summary>
        /// Resimulate a single gap tick using own input from the InputBuffer plus EmptyCommand
        /// substitution for missing players. Mirrors server-side ServerInputCollector.CollectTickInputs
        /// so the resulting state hash matches the server's verified hash for the same tick.
        /// </summary>
        private void SimulateGapTickWithEmptyFallback(int gapTick)
        {
            SaveSnapshot(gapTick);
            _tickCommandsCache.Clear();

            var gapCmds = _inputBuffer.GetCommandList(gapTick);
            for (int gi = 0; gi < gapCmds.Count; gi++)
                _tickCommandsCache.Add(gapCmds[gi]);

            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int playerId = _activePlayerIds[pi];
                if (!_inputBuffer.HasCommandForTick(gapTick, playerId))
                {
                    var empty = CommandPool.Get<EmptyCommand>();
                    empty.PlayerId = playerId;
                    empty.Tick = gapTick;
                    _tickCommandsCache.Add(empty);
                }
            }

            _tickCommandsCache.Sort(s_commandComparer);
            // Open the collector for this gap tick so RaiseEvent stamps evt.Tick correctly and any
            // stale residue from the prior path is cleared (mirrors every other Tick path).
            _eventCollector.BeginTick(gapTick);
#if DEBUG || DEVELOPMENT_BUILD
            _inputBuffer.SetResimulating(true);
#endif
            _simulation.Tick(_tickCommandsCache);
#if DEBUG || DEVELOPMENT_BUILD
            _inputBuffer.SetResimulating(false);
#endif
            // Gap-tick events are state-advance only — they fall below both the old-event backup and
            // the DiffRollbackEvents window (both start at firstExecutionTick), so buffering them would
            // double-dispatch against the original predicted events. Return them to the pool instead;
            // without this they leak (the next BeginTick clears _collected with no pool return).
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                EventPool.Return(_eventCollector.Collected[ei]);
            _eventCollector.Clear();
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

            if (_isCatchingUp || _isSpectatorMode)
            {
                for (int i = 0; i < confirmedInputs.Count; i++)
                {
                    var storeResult = _inputBuffer.AddCommandChecked(confirmedInputs[i], overwriteExisting: true);
                    // Buffer-rejected instances are deserialize-born and sole-owned by this
                    // dispatch — return immediately; element-wise return does not
                    // disturb the remaining iteration over the shared list. AlreadyStored
                    // (double-dispatch insurance) is buffer property — never returned.
                    if (storeResult == CommandStoreResult.DroppedDuplicate || storeResult == CommandStoreResult.DroppedSealed)
                        CommandPool.Return(confirmedInputs[i]);
                }

                if (_isCatchingUp)
                    ConfirmCatchupTick(tick - 1);
                if (_isSpectatorMode)
                    ConfirmSpectatorTick(tick - 1);
            }
            else
            {
                // Discard messages with ticks already confirmed via FullState restore, and
                // same-tick duplicates still sitting in the queue — the dedup is
                // what proves queued containers never share instances, enabling the drain's
                // pool return. The only duplicate vector is a double-dispatched handler
                // (closed by the Initialize re-entry guard; VerifiedState is ReliableOrdered),
                // so this never fires on the normal path. Discarded instances are left to GC,
                // NOT returned: under a (hypothetical, guard-bypassing) duplicate dispatch this
                // is the one spot whose instances are shared with the queued copy — a Return
                // here would turn that future bug from a benign leak into pool poisoning.
                if (tick <= Math.Max(_lastVerifiedTick, _lastEnqueuedVerifiedTick))
                    return;

                // confirmedInputs is a shared list, so copy it into a separate list before
                // enqueueing. Container from ListPool. The ICommand instances are
                // queue-owned from here until stored into the InputBuffer: rejected
                // and drained instances are returned to CommandPool by the queue side; accepted
                // ones transfer to the buffer, which returns them in cleanup.
                var commands = ListPool<ICommand>.Get();
                for (int i = 0; i < confirmedInputs.Count; i++)
                    commands.Add(confirmedInputs[i]);

                _pendingVerifiedQueue.Enqueue(new VerifiedStateEntry
                {
                    Tick = tick,
                    Commands = commands,
                    StateHash = stateHash
                });
                _lastEnqueuedVerifiedTick = tick;
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
                // Defensive: this branch consumes the FullState without falling through
                // to the determinism path's clear (:835-equiv), so clear the request gate+timer here too.
                // Unreachable with pending=true today (this branch runs on a fresh engine / pre-subscribe
                // ExpectFullState no-op), but guards a future in-place reconnect path.
                ClearFullStateRequestState();
                ApplyFullState(tick, stateData, stateHash, ApplyReason.LateJoin);
                SaveSnapshot(tick);
                _inputBuffer.Clear();
                _lastServerVerifiedTick = tick;
                _lastVerifiedTick = tick;
                _lastEnqueuedVerifiedTick = tick;
                _serverDrivenNetwork.ClearUnackedInputs();
                DrainPendingVerifiedQueue();
                _posDeltas.Clear();
                _yawDeltas.Clear();
                _teleportedEntities.Clear();

                // In catchup mode, HandleVerifiedStateReceived stores directly into the InputBuffer.
                StartCatchingUp();
                _logger?.KInformation($"[KlothoEngine][SD] Late Join FullState received, starting catchup: tick={tick}");
                return;
            }

            int previousTick = CurrentTick;

            // F47 (L1): capture the pre-reset recorded frontier BEFORE ApplyFullState. A resync replaces
            // state wholesale (a jump with no replay record), so the recording must be truncated here — the
            // SD-client analog of the P2P host/guest corrective-reset truncation. Applied below, past the
            // early-return gates.
            int preResetTotalTicks = ComputeReplayTotalTicks();

            // Determinism failure or Reconnect recovery path.
            var applyResult = ApplyFullState(tick, stateData, stateHash, ApplyReason.ResyncRequest);

            // respect the ApplyFullState contract (P2P mirror, FullStateResync.cs:456-468).
            // Skipped — retreat guard rejected the state, NOTHING applied. Skip all post-processing
            // (clearing input history / resim on an unrestored state stalls the chain) and
            // keep _fullStateRequestPending true so the timer re-requests with a tick that clears
            // the guard (_lastVerifiedTick >= tick is what tripped it).
            if (applyResult == FullStateApplyResult.Skipped)
            {
                _logger?.KWarning(
                    $"[KlothoEngine][SD] FullState apply skipped (retreat guard) at tick={tick}; awaiting timeout retry");
                return;
            }

            // HashMismatch — state WAS restored (RestoreFromFullState ran before the hash check) but its
            // hash disagrees with the server's (non-deterministic deserialize). Re-requesting yields the
            // same bytes → same mismatch = unrecoverable, so signal failure. With AutoAbort (default) the
            // match ends. With AutoAbort off the game layer owns teardown — fall through to normal
            // post-processing so the restored (untrusted) state stays bookkeeping-consistent (mirrors P2P,
            // FullStateResync.cs which proceeds on a restored-but-mismatched state).
            if (applyResult == FullStateApplyResult.HashMismatch)
            {
                _logger?.KError(
                    $"[KlothoEngine][SD] FullState apply hash mismatch at tick={tick} — unrecoverable");
                if (_simConfig.AutoAbortOnRecoveryExhausted)
                {
                    HandleSdResyncFailure(AbortReason.StateDivergence);
                    return;
                }
                // AutoAbort off: warn the game layer, then fall through to normal post-processing.
                OnResyncFailed?.Invoke();
            }

            // DerivativeRebuildFailed — the state and its hash are sound; only this peer's
            // out-of-hash derivative rebuild (navmesh) threw. Deliberately NOT folded into the
            // branch above: that one ends the match, and a local rebuild failure — usually this
            // client's assets or config disagreeing with the authority's — does not warrant that.
            // Re-requesting is no use either, since the same deterministic rebuild fails the same
            // way. So fall through to normal post-processing (the state is good and its bookkeeping
            // must land) and make the degradation visible instead.
            if (applyResult == FullStateApplyResult.DerivativeRebuildFailed)
            {
                _logger?.KError(
                    $"[KlothoEngine][SD] FullState applied at tick={tick} but this client's peer-local " +
                    $"derivative rebuild failed — its navmesh no longer matches its state. Not an abort: " +
                    $"the state is sound and a retry would fail identically. See the preceding KError for the cause.");
            }

            // F47 (L1): truncate the recording at the pre-reset frontier before the state jump lands in the
            // bookkeeping below. Placed after the Skipped / HashMismatch+AutoAbort early-returns so only an
            // actually-applied resync truncates; the helper's reason gate + IsRecording guard keep it inert
            // otherwise. The reconnect resim loop below does not RecordTick, so nothing re-extends the replay.
            TruncateReplayForStateJump(ApplyReason.ResyncRequest, preResetTotalTicks, tick);

            // Preserve local inputs that are not yet confirmed.
            _inputBuffer.ClearBefore(tick);

            ClearFullStateRequestState();
            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();
            _lastServerVerifiedTick = tick;
            _lastVerifiedTick = tick;
            _lastEnqueuedVerifiedTick = tick;

            _serverDrivenNetwork.ClearUnackedInputs();

            // After FullState restore the ring buffer is cleared, so any VerifiedState remaining in the queue has no valid snapshot.
            DrainPendingVerifiedQueue();

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
#if DEBUG || DEVELOPMENT_BUILD
                    _inputBuffer.SetResimulating(true);
#endif
                    _simulation.Tick(_tickCommandsCache);
#if DEBUG || DEVELOPMENT_BUILD
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

            _logger?.KInformation(
                $"[KlothoEngine][SD] FullState restore complete: serverTick={tick}, previousTick={previousTick}");
        }

        /// <summary>
        /// SD Client initial FullState receive handler.
        /// On session start, corrects the local state with the authoritative initial state broadcast by the server, and embeds it in the replay as well.
        /// </summary>
        private void HandleInitialFullStateReceived(int tick, byte[] stateData, long stateHash)
        {
            _expectingInitialFullState = false;
            // Defensive: same as the LateJoin branch — clear the request gate+timer so
            // a FullState consumed here never leaves _fullStateRequestPending stuck
            // (unreachable with pending=true today, but cheap regression guard).
            ClearFullStateRequestState();
            ApplyFullState(tick, stateData, stateHash, ApplyReason.InitialFullState);
            _lastServerVerifiedTick = tick;
            _replaySystem.SetInitialStateSnapshot(stateData, stateHash);
            // ApplyFullState resets _accumulator to 0, so re-establish the warm-up lead.
            ApplySDWarmUpLead();
            _logger?.KInformation(
                $"[KlothoEngine][SD] Initial FullState applied: tick={tick}, size={stateData.Length}");

            // One-time static geometry fingerprint after the authoritative state is applied — the SD
            // client's static is loaded by RegisterSystems, so this is comparable to the server's boot line.
            (_simulation as xpTURN.Klotho.ECS.EcsSimulation)?.LogStaticFingerprint(_logger, "boot");

            // Signal bootstrap-ready so the server can proceed to first tick once all peers have ack'd.
#if KLOTHO_FAULT_INJECTION
            // Suppress this client's bootstrap-ready ack to exercise the server-side
            // BOOTSTRAP_TIMEOUT_MS path (FullState resync).
            if (xpTURN.Klotho.Diagnostics.FaultInjection.SuppressBootstrapAckPlayerIds.Contains(LocalPlayerId))
            {
                _logger?.KWarning($"[FaultInjection][SD] Bootstrap ack suppressed: playerId={LocalPlayerId}, tick={tick}");
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
                _logger?.KWarning(
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

            _logger?.KInformation(
                $"[KlothoEngine][SD] BootstrapBegin applied: firstTick={firstTick}, tickStartTimeMs={tickStartTimeMs}, elapsed={elapsedSinceStart}ms, accumulator={_accumulator:F1}ms");
        }

        // Forwards transport-level rejection notifications to the engine-public event so game code can react.
        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            _logger?.KInformation($"[KlothoEngine][SD] CommandRejected: tick={tick}, cmdTypeId={cmdTypeId}, reason={reason}");
            OnCommandRejected?.Invoke(tick, cmdTypeId, reason);
        }
    }
}
