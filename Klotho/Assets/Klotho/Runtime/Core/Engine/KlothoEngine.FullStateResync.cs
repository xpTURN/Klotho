using System;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Input;
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

        private void RequestFullStateResync()
        {
            if (_resyncState != ResyncState.None)
                return;

            if (_networkService.IsHost)
                return;

            _resyncRetryCount++;

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
        /// Common full-state restoration for P2P/SD. The caller performs mode-specific post-processing.
        /// </summary>
        private void ApplyFullState(int tick, byte[] stateData, long stateHash)
        {
            // 1. Replace local state
            _simulation.RestoreFromFullState(stateData);

            // 1.5. Hash verification
            long localHash = _simulation.GetStateHash();
            if (localHash != stateHash)
            {
                _logger?.ZLogError($"[KlothoEngine][FullStateResync] hash mismatch: local=0x{localHash:X16}, remote=0x{stateHash:X16}. Deserialization may be non-deterministic - resync state unreliable");
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

            // 6. Reset accumulator
            _accumulator = 0.0f;
        }

        private void HandleFullStateReceived(int tick, byte[] stateData, long stateHash)
        {
            bool isResync = _resyncState == ResyncState.Requested;

            if (!isResync && !_expectingFullState)
            {
                _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] FullStateResponse received but not in Requested state, ignoring");
                return;
            }

            if (isResync)
                _resyncState = ResyncState.Applying;
            _expectingFullState = false;

            // Common restoration
            ApplyFullState(tick, stateData, stateHash);

            // P2P-only post-processing

            // Clean up input buffers
            _inputBuffer.Clear();
            _pendingCommands.Clear();

            // Reset verification chain
            _lastVerifiedTick = tick - 1;
            _lastMatchedSyncTick = tick;
            _pendingSyncCheckTick = -1;
            _desyncDetectedForPending = false;

            // Reset consecutive desync counter
            _consecutiveDesyncCount = 0;
            _resyncRetryCount = 0;

            // Reset rollback-related state
            _hasPendingRollback = false;
            _pendingRollbackTick = -1;

            // Resync path: state restoration + event firing
            if (isResync)
            {
                _resyncState = ResyncState.None;
                _logger?.ZLogWarning($"[KlothoEngine][FullStateResync] complete: tick={tick}");
                OnResyncCompleted?.Invoke(tick);
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
