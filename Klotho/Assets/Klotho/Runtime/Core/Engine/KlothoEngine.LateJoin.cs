using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Late Join: catchup mode (guest side)
        private const int LATE_JOIN_CATCHUP_THRESHOLD_TICKS = 4;
        private bool _isCatchingUp;
        private int _catchupLastConfirmedTick = -1;
        public event Action OnCatchupComplete;

        // SD-only: ratchet flag to delay firing OnLateJoinActivated until the warmup burst finishes right after catchup ends.
        // Checked and fired in UpdateServerDrivenClient after ProcessVerifiedBatch and before FlushSendQueue.
        // Same pattern as _consumePendingDeltaTime.
        private bool _pendingLateJoinActivation;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic — catch-up duration measurement.
        private long _catchupStartWallMs;
        private int _catchupStartTick;
#endif

        public void StartCatchingUp()
        {
            _isCatchingUp = true;
            _catchupLastConfirmedTick = CurrentTick - 1;
            _pendingLateJoinActivation = false; // reset for reconnect/consecutive Late Join

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _catchupStartWallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _catchupStartTick = CurrentTick;
            _logger?.ZLogInformation($"[KlothoEngine][LateJoin] Catchup START: tick={CurrentTick}, lastConfirmed={_catchupLastConfirmedTick}");
#endif

            if (State == KlothoState.WaitingForPlayers)
                State = KlothoState.Running;
        }

        public void StopCatchingUp()
        {
            _isCatchingUp = false;
        }

        public void ConfirmCatchupTick(int tick)
        {
            if (tick > _catchupLastConfirmedTick)
                _catchupLastConfirmedTick = tick;
        }

        private void HandleCatchupUpdate()
        {
            // Run quickly using only Verified input (no prediction, ignore accumulator)
            int maxTicksPerFrame = _sessionConfig.CatchupMaxTicksPerFrame;
            int executed = 0;
            while (CurrentTick <= _catchupLastConfirmedTick && executed < maxTicksPerFrame)
            {
                var commands = _inputBuffer.GetCommandList(CurrentTick);
                SaveSnapshot(CurrentTick);

                _eventCollector.BeginTick(CurrentTick);
                _simulation.Tick(commands);

                // Replay recording - based on verified input, so record as confirmed commands
                if (_replaySystem.IsRecording)
                    _replaySystem.RecordTick(CurrentTick, commands);

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

                executed++;
            }

            // Save a snapshot of the current state after catchup ends
            // Ensures ProcessVerifiedBatch can rollback to this tick
            if (executed > 0)
                SaveSnapshot(CurrentTick);

            // CatchingUp -> Active transition condition
            if (_catchupLastConfirmedTick - CurrentTick <= LATE_JOIN_CATCHUP_THRESHOLD_TICKS
                && _catchupLastConfirmedTick >= 0)
            {
                _isCatchingUp = false;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                long catchupDurationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _catchupStartWallMs;
                int ticksAdvanced = CurrentTick - _catchupStartTick;
                _logger?.ZLogInformation($"[KlothoEngine][LateJoin] Catchup COMPLETE: durationMs={catchupDurationMs}, ticksAdvanced={ticksAdvanced}, finalTick={CurrentTick}");
#endif

                // SD mode: when catchup completes, reset the lead-tick control baseline and acquire warm-up lead
                if (_simConfig.Mode == NetworkMode.ServerDriven)
                {
                    _lastServerVerifiedTick = CurrentTick;
                    _consumePendingDeltaTime = true;
                    ApplySDWarmUpLead();
                }

                OnCatchupComplete?.Invoke();

                // SD mode: OnLateJoinActivated fires after the warmup burst completes.
                //   Game code sends Spawn etc. with the CurrentTick at the callback moment;
                //   if it is a pre-burst value, the server rejects it as a past tick. Single-flag ratchet.
                // P2P has no ApplySDWarmUpLead path and therefore no burst, so it fires immediately.
                if (_simConfig.Mode == NetworkMode.ServerDriven)
                    _pendingLateJoinActivation = true;
                else
                    _viewCallbacks?.OnLateJoinActivated(this);
            }
        }

        private void HandleLateJoinPlayerAdded(int playerId, int joinTick)
        {
            if (!_networkService.IsHost)
                return;

            var cmd = CommandPool.Get<PlayerJoinCommand>();
            cmd.PlayerId = _networkService.LocalPlayerId;
            cmd.Tick = joinTick;
            cmd.JoinedPlayerId = playerId;
            _networkService.SendCommand(cmd);
        }

        private void HandlePlayerCountChanged(int newPlayerCount, int changedPlayerId)
        {
            _playerCount = newPlayerCount;
            if (!_activePlayerIds.Contains(changedPlayerId))
                _activePlayerIds.Add(changedPlayerId);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Diagnostic — roster snapshot when player count changes.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _activePlayerIds.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_activePlayerIds[i]);
            }
            _logger?.ZLogInformation($"[KlothoEngine][Roster] PlayerCountChanged: newCount={newPlayerCount}, changed={changedPlayerId}, active=[{sb}], CurrentTick={CurrentTick}, _lastVerifiedTick={_lastVerifiedTick}");
#endif
        }

        /// <summary>
        /// Entry point for the Late Join path. Injects the FullState into the engine from ConnectionResult.LateJoinPayload.
        /// Must be called immediately after engine.Initialize (State==WaitingForPlayers) so that the subsequent StartCatchingUp transitions correctly to Running.
        /// HandleGameStart is not called on this path, so _randomSeed synchronization is performed here as well.
        /// </summary>
        public void SeedLateJoinFullState(LateJoinPayload payload)
        {
            _randomSeed = _networkService.RandomSeed;
            _expectingFullState = true;

            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                HandleServerDrivenFullStateReceived(
                    payload.FullStateTick, payload.FullStateData, payload.FullStateHash);
            }
            else
            {
                ApplyP2PLateJoinFullState(
                    payload.FullStateTick, payload.FullStateData, payload.FullStateHash);
            }
        }

        /// <summary>
        /// Entry point for the cold-start Reconnect path. Same FullState injection mechanics as
        /// SeedLateJoinFullState — only the payload source differs.
        /// </summary>
        public void SeedReconnectFullState(ReconnectPayload payload)
        {
            _randomSeed = _networkService.RandomSeed;
            _expectingFullState = true;

            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                HandleServerDrivenFullStateReceived(
                    payload.FullStateTick, payload.FullStateData, payload.FullStateHash);
            }
            else
            {
                ApplyP2PLateJoinFullState(
                    payload.FullStateTick, payload.FullStateData, payload.FullStateHash);
            }
        }

        /// <summary>
        /// P2P Late Join full-state application. Mirrors the SD path's catchup entry but skips
        /// SD-only fields (_serverDrivenNetwork, _pendingVerifiedQueue, _lastServerVerifiedTick).
        /// </summary>
        private void ApplyP2PLateJoinFullState(int tick, byte[] stateData, long stateHash)
        {
            _expectingFullState = false;
            ApplyFullState(tick, stateData, stateHash);

            _inputBuffer.Clear();
            _pendingCommands.Clear();

            _lastVerifiedTick = tick;
            _lastMatchedSyncTick = tick;
            _pendingSyncCheckTick = -1;
            _desyncDetectedForPending = false;

            _consecutiveDesyncCount = 0;
            _resyncRetryCount = 0;

            _hasPendingRollback = false;
            _pendingRollbackTick = -1;

            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();

            StartCatchingUp();
            _logger?.ZLogInformation($"[KlothoEngine][P2P] Late Join FullState received, starting catchup: tick={tick}");
        }
    }
}
