using System;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.State;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Full state resync
        private const int RESYNC_TIMEOUT_MS = 5000;

        private enum ResyncState { None, Requested, Applying }
        private ResyncState _resyncState = ResyncState.None;
        private float _resyncElapsedMs;
        private int _resyncRetryCount;
        private int _consecutiveDesyncCount;
        private bool _expectingFullState;

        // True while the SD Client is waiting for the server's initial FullState broadcast at session start.
        // Used for branching at the entry of HandleServerDrivenFullStateReceived and for blocking tick progression in the parent Update.
        // Mutually exclusive with _expectingFullState (Late Join).
        private bool _expectingInitialFullState;

        // Full state cache (host side)
        private byte[] _cachedFullState;
        private long _cachedFullStateHash;
        private int _cachedFullStateTick = -1;

        // Counts FullStateResponse messages dropped by the silent-ignore guard in
        // HandleFullStateReceived (received outside Requested / expectingFullState state).
        // Exposed to the network-service telemetry emitter.
        private int _unexpectedFullStateDropCount;
        internal int UnexpectedFullStateDropCount => _unexpectedFullStateDropCount;

        // Counts hash-mismatch observations in ApplyFullState. Quantifies post-②-B residual
        // divergence frequency — operational input for corrective-reset priority.
        private int _resyncHashMismatchCount;
        internal int ResyncHashMismatchCount => _resyncHashMismatchCount;

        // Peak _consecutiveDesyncCount reached during the match. Quantifies divergence
        // pressure: low peak = sporadic, high peak ≥ DesyncThresholdForResync = repeated
        // escalation. Resets when the desync streak ends.
        private int _consecutiveDesyncPeak;
        internal int ConsecutiveDesyncPeak => _consecutiveDesyncPeak;

        // Total RequestFullStateResync invocations across the match. Separate from
        // _resyncRetryCount which resets after a completed resync; this counter never resets
        // so the operator sees the lifetime resync-request burden.
        private int _resyncRequestTotalCount;
        internal int ResyncRequestTotalCount => _resyncRequestTotalCount;

        // Desync detections that occur after at least one successful resync — i.e. the
        // resync recovered to a clean state and the match diverged again. A high value
        // signals non-deterministic serialization or a persistent state bug.
        private int _postResyncDesyncCount;
        internal int PostResyncDesyncCount => _postResyncDesyncCount;

        private bool _hasCompletedResync;

        // Corrective Reset
        private long _lastCorrectiveResetMs;

        /// <summary>
        /// Event handler for ReplaySystem.OnInitialStateSnapshotSet.
        /// When the game code calls SetInitialStateSnapshot(data, hash), fills _cachedFullState* based on tick 0.
        /// The SD Server later reuses this cache in BroadcastFullState at the tail of HandleGameStart.
        /// On P2P / SD Client the cache is not broadcast, but it is always set for consistency and future reuse.
        /// </summary>
        private void HandleInitialStateSnapshotSet(byte[] snapshot, long hash)
        {
            _cachedFullState = snapshot;
            _cachedFullStateHash = hash;
            _cachedFullStateTick = 0;
        }

        #region Full State Resync

        private void TryCorrectiveReset(int divergenceTick)
        {
            if (!_networkService.IsHost) return;
            if (State.IsEnded()) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastCorrectiveResetMs < _sessionConfig.CorrectiveResetCooldownMs)
            {
                _logger?.ZLogWarning($"[KlothoEngine][CorrectiveReset] cooldown active, skip (elapsed={now - _lastCorrectiveResetMs}ms)");
                return;
            }
            _lastCorrectiveResetMs = now;

            if (_cachedFullStateTick != CurrentTick)
            {
                var (data, hash) = _simulation.SerializeFullStateWithHash();
                _cachedFullState = data;
                _cachedFullStateHash = hash;
                _cachedFullStateTick = CurrentTick;
            }

            _logger?.ZLogWarning($"[KlothoEngine][CorrectiveReset] broadcast: tick={CurrentTick}, divergenceTick={divergenceTick}");
            _networkService.BroadcastFullState(CurrentTick, _cachedFullState, _cachedFullStateHash, FullStateKind.CorrectiveReset);

            // Host self-apply — mirrors HandleFullStateReceived post-processing (host does not receive its own broadcast).
            ApplyFullState(CurrentTick, _cachedFullState, _cachedFullStateHash, ApplyReason.CorrectiveReset);
            _inputBuffer.Clear();
            _pendingCommands.Clear();
            _lastVerifiedTick = CurrentTick - 1;
            _pendingSyncCheckTick = -1;
            _desyncDetectedForPending = false;
            _hasPendingRollback = false;
            _pendingRollbackTick = -1;
            _lastMatchedSyncTick = CurrentTick;
            _consecutiveDesyncCount = 0;
            _resyncRetryCount = 0;
        }

        private void HandleHashMismatchForCorrectiveReset(int tick, long localHash, long remoteHash)
        {
            TryCorrectiveReset(tick);
        }

        private void RequestFullStateResync()
        {
            if (_resyncState != ResyncState.None)
                return;

            if (_networkService.IsHost)
                return;

            _resyncRetryCount++;
            _resyncRequestTotalCount++;

            if (_resyncRetryCount > _sessionConfig.ResyncMaxRetries)
            {
                _logger?.ZLogError($"[KlothoEngine][FullStateResync] failed: max retry count exceeded");
                OnResyncFailed?.Invoke();
                return;
            }

            _resyncState = ResyncState.Requested;
            _resyncElapsedMs = 0;

            _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] requested (attempt {_resyncRetryCount}/{_sessionConfig.ResyncMaxRetries})");
            _networkService.SendFullStateRequest(CurrentTick);
        }

        private void CheckResyncTimeout(float deltaTime)
        {
            _resyncElapsedMs += deltaTime * 1000f;
            if (_resyncElapsedMs >= RESYNC_TIMEOUT_MS)
            {
                _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] timed out, retrying...");
                _resyncState = ResyncState.None;
                RequestFullStateResync();
            }
        }

        public void ExpectFullState()
        {
            _expectingFullState = true;
        }

        public void CancelExpectFullState()
        {
            _expectingFullState = false;
        }

        /// <summary>
        /// SD client only: arms the routing flag so the next FullState arrival is treated as the
        /// initial-state broadcast (HandleInitialFullStateReceived path) rather than a determinism resync.
        /// Called from the SD client transport layer when it learns the game is starting — works for both
        /// countdown-enabled (covered by HandleCountdownStarted) and countdown-skip configurations.
        /// </summary>
        public void MarkExpectingInitialFullState()
        {
            _expectingInitialFullState = true;
        }

        /// <summary>
        /// Common full-state restoration for P2P/SD. The caller performs mode-specific post-processing.
        /// Returns true when the post-restore hash matches the advertised remote hash.
        /// </summary>
        private bool ApplyFullState(int tick, byte[] stateData, long stateHash, ApplyReason reason)
        {
            // Retreat guard — CorrectiveReset/LateJoin/InitialFullState allow retreat; others do not.
            bool allowRetreat = reason == ApplyReason.CorrectiveReset
                               || reason == ApplyReason.LateJoin
                               || reason == ApplyReason.InitialFullState;
            if (!allowRetreat && _lastVerifiedTick >= tick)
            {
                _logger?.ZLogWarning($"[KlothoEngine][ApplyFullState] skip retreat: _lastVerifiedTick={_lastVerifiedTick} >= tick={tick}, reason={reason}");
                return true;
            }

            // 1. Replace local state
            _simulation.RestoreFromFullState(stateData);

            // 1.5. Hash verification
            long localHash = _simulation.GetStateHash();
            bool hashMatched = localHash == stateHash;
            _logger?.ZLogInformation($"[KlothoEngine][FullStateResync] hash check: tick={tick} local=0x{localHash:X16} remote=0x{stateHash:X16} match={hashMatched}");
            if (!hashMatched)
            {
                _resyncHashMismatchCount++;
                _logger?.ZLogError($"[KlothoEngine][FullStateResync] hash mismatch: local=0x{localHash:X16}, remote=0x{stateHash:X16}. Deserialization may be non-deterministic - resync state unreliable");

                // Diagnostic — per-component hash to identify which component(s) diverged.
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                    ecsSimDiag.LogComponentHashes(_logger, "ClientApplyMismatch");

                OnHashMismatch?.Invoke(tick, localHash, stateHash);
                // Bridge into the mid-match desync pipeline so HandleNetworkDesync's
                // _consecutiveDesyncCount accumulation can escalate via the shared path.
                OnDesyncDetected?.Invoke(localHash, stateHash);
            }

            // 2. Reset snapshot manager
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.ClearSnapshots();
            else if (_snapshotManager is RingSnapshotManager ringMgr)
                ringMgr.ClearAll();

            // 3. Tick synchronization
            CurrentTick = tick;

            // 4. Save new snapshot
            SaveSnapshot(CurrentTick);

            // 5. Clear event buffer
            _eventBuffer.ClearAll();
            EventPool.ClearAll();

            // Watermark cascade: ClearAll discards all buffered Synced events, so previously-
            // dispatched ticks no longer have buffered evidence. Lower watermark below tick so
            // future Synced events at tick or later can dispatch.
            if (_syncedDispatchHighWaterMark >= tick)
                _syncedDispatchHighWaterMark = tick - 1;

            // 6. Reset accumulator
            _accumulator = 0.0f;

            // Clamp _lastBatchedTick on corrective reset to prevent FireVerifiedInputBatch under-fire.
            if (reason == ApplyReason.CorrectiveReset)
                _lastBatchedTick = Math.Min(_lastBatchedTick, tick - 1);

            return hashMatched;
        }

        private void HandleFullStateReceived(int tick, byte[] stateData, long stateHash, FullStateKind kind)
        {
            bool isResync = _resyncState == ResyncState.Requested;
            bool isCorrectiveReset = kind == FullStateKind.CorrectiveReset;

            if (!isResync && !_expectingFullState && !isCorrectiveReset)
            {
                _unexpectedFullStateDropCount++;
                _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] FullStateResponse received but not in Requested state, ignoring (drops={_unexpectedFullStateDropCount})");
                return;
            }

            if (isResync)
                _resyncState = ResyncState.Applying;
            _expectingFullState = false;

            // Common restoration
            ApplyReason reason = isCorrectiveReset
                ? ApplyReason.CorrectiveReset
                : (isResync ? ApplyReason.ResyncRequest : ApplyReason.LateJoin);
            bool hashMatched = ApplyFullState(tick, stateData, stateHash, reason);

            // P2P-only post-processing

            // Clean up input buffers (always — new state is the baseline regardless of hash)
            _inputBuffer.Clear();
            _pendingCommands.Clear();

            // Verified-chain reset always anchors on the new tick; the matched-sync baseline
            // only advances when the hash agrees so a mismatched tick does not become a
            // known-good rollback target for HandleNetworkDesync.
            _lastVerifiedTick = tick - 1;
            _pendingSyncCheckTick = -1;
            _desyncDetectedForPending = false;

            // Reset rollback-related state
            _hasPendingRollback = false;
            _pendingRollbackTick = -1;

            if (hashMatched)
            {
                _lastMatchedSyncTick = tick;
                _consecutiveDesyncCount = 0;
                _resyncRetryCount = 0;
            }
            // else: preserve _lastMatchedSyncTick / _consecutiveDesyncCount / _resyncRetryCount
            // so the mid-match desync path can keep accumulating toward ResyncMaxRetries and
            // fire OnResyncFailed when the divergence is unrecoverable.

            // Resync path: state restoration + event firing
            if (isResync)
            {
                _resyncState = ResyncState.None;
                if (hashMatched)
                {
                    _hasCompletedResync = true;
                    _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] complete: tick={tick}");
                    OnResyncCompleted?.Invoke(tick);
                }
                else
                {
                    _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] applied with hash mismatch at tick={tick}; OnResyncCompleted suppressed, mid-match desync path will re-attempt");
                }
            }

            // Corrective reset path: emit OnMatchReset on successful state restoration.
            if (isCorrectiveReset && hashMatched)
            {
                _logger?.ZLogWarning($"[KlothoEngine][CorrectiveReset] state restored at tick={tick}, firing OnMatchReset");
                OnMatchReset?.Invoke(ResetReason.StateDivergence);
            }
        }

        private void HandleFullStateRequested(int peerId, int requestTick)
        {
            if (!_networkService.IsHost) return;

            // Reuse the serialized result for the same tick (caching)
            if (_cachedFullStateTick != CurrentTick)
            {
                var (data, hash) = _simulation.SerializeFullStateWithHash();
                _cachedFullState = data;
                _cachedFullStateHash = hash;
                _cachedFullStateTick = CurrentTick;
            }

            _networkService.SendFullStateResponse(peerId, CurrentTick, _cachedFullState, _cachedFullStateHash);

            _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] FullState sent: peer={peerId}, tick={CurrentTick}, size={_cachedFullState.Length}");
        }

        #endregion

        private void HandleNetworkDesync(int playerId, int tick, long localHash, long remoteHash)
        {
            _logger?.ZLogError($"[KlothoEngine][FullStateResync] Desync detected at tick {tick}! Player {playerId}: local={localHash}, remote={remoteHash}");
            OnDesyncDetected?.Invoke(localHash, remoteHash);

            _desyncDetectedForPending = true;

            // Suppress further processing if resync is in progress
            if (_resyncState != ResyncState.None)
                return;

            _consecutiveDesyncCount++;
            if (_consecutiveDesyncCount > _consecutiveDesyncPeak)
                _consecutiveDesyncPeak = _consecutiveDesyncCount;
            if (_hasCompletedResync)
                _postResyncDesyncCount++;
            _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] Desync consecutiveCount={_consecutiveDesyncCount}/{_sessionConfig.DesyncThresholdForResync}, lastMatchedSyncTick={_lastMatchedSyncTick}, currentTick={CurrentTick}");

            if (_consecutiveDesyncCount >= _sessionConfig.DesyncThresholdForResync)
            {
                _consecutiveDesyncCount = 0;
                RequestFullStateResync();
                return;
            }

            // Keep the existing rollback attempt
            int rollbackTarget = _lastMatchedSyncTick > 0 ? _lastMatchedSyncTick : tick;
            _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] Desync recovery: rolling back to lastMatchedSyncTick={_lastMatchedSyncTick} (desync tick={tick})");
            RequestRollback(rollbackTarget);
        }
    }
}
