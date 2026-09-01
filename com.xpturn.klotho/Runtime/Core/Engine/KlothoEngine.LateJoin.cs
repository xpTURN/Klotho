using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.ECS;

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

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic — catch-up duration measurement.
        private long _catchupStartWallMs;
        private int _catchupStartTick;
#endif

        public void StartCatchingUp()
        {
            _isCatchingUp = true;
            _catchupLastConfirmedTick = CurrentTick - 1;
            _pendingLateJoinActivation = false; // reset for reconnect/consecutive Late Join

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            _catchupStartWallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _catchupStartTick = CurrentTick;
            _logger?.KInformation($"[KlothoEngine][LateJoin] Catchup START: tick={CurrentTick}, lastConfirmed={_catchupLastConfirmedTick}");
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
            int maxTicksPerFrame = _simConfig.CatchupMaxTicksPerFrame;
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

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                long catchupDurationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _catchupStartWallMs;
                int ticksAdvanced = CurrentTick - _catchupStartTick;
                _logger?.KInformation($"[KlothoEngine][LateJoin] Catchup COMPLETE: durationMs={catchupDurationMs}, ticksAdvanced={ticksAdvanced}, finalTick={CurrentTick}");
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
            // Assigned unconditionally, null included: the pool hands back instances without clearing
            // fields, so skipping this on a player with no entitlement would carry the PREVIOUS joiner's
            // bytes into this join — and every node would seed that player's world from them.
            cmd.Entitlement = GetPlayerEntitlement(playerId);

            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                // SD server: SendCommand is a no-op (no local input on the server), so schedule the
                // command directly into the input collector's system lane. It then enters the tick-J
                // command list that ServerTick executes, replay-records, and broadcasts in one pass —
                // every node (server sim + client replay) sees the join at the same tick.
                if (_serverNetwork == null)
                    _serverNetwork = _serverDrivenNetwork as xpTURN.Klotho.Network.ServerNetworkService;
                if (_serverNetwork != null)
                {
                    _serverNetwork.InputCollector.AddSystemCommand(joinTick, cmd);
                }
                else
                {
                    _logger?.KWarning($"[KlothoEngine][LateJoin] PlayerJoinCommand dropped: SD network service is not a ServerNetworkService (playerId={playerId}, joinTick={joinTick})");
                    CommandPool.Return(cmd);
                }
                return;
            }

            _networkService.SendCommand(cmd);
        }

        private void HandlePlayerJoinedNotification(int joinedPlayerId)
        {
            // Engine roster: idempotent add (semantics unchanged).
            if (!_activePlayerIds.Contains(joinedPlayerId))
                _activePlayerIds.Add(joinedPlayerId);

            // Gate the deterministic slot CreateEntity on the FRAME, not _activePlayerIds.
            // A late-join guest's _activePlayerIds already contains its own id (Initialize seeds it
            // from the live roster), so the old `!_activePlayerIds.Contains` gate evaluated false on
            // the joiner and true on the peers — the joiner SKIPPED the slot CreateEntity while peers
            // created it, an off-by-one entity cursor → permanent hash desync. The frame is hash-locked
            // (the deterministic truth, identical on every node and under rollback re-sim): create the
            // slot iff no SessionParticipantComponent for this player exists yet, so every node creates
            // it exactly once. Player count is small → linear scan, no allocation.
            if (_simulation is EcsSimulation ecsSim && !HasParticipantSlot(ecsSim.Frame, joinedPlayerId))
            {
                var frame = ecsSim.Frame;
                var slotEntity = frame.CreateEntity();
                frame.Add(slotEntity, new SessionParticipantComponent
                {
                    PlayerId = joinedPlayerId,
                });

                // Late-join analog of OnInitializeWorld's per-player setup. Invoked inside the
                // create-iff-not-exists slot guard, right after the participant slot, so it fires exactly once
                // per join and is rollback-safe (re-sim re-creates the slot iff absent, re-firing this; if the
                // snapshot already holds the slot, the whole block is skipped). The game writes deterministic
                // per-player world state (e.g. an entitlement loadout) via the live frame; engine is for reads
                // only (InitFrame is init-only, hence the frame is passed explicitly).
                _simulationCallbacks?.OnPlayerJoinedWorld(this, frame, joinedPlayerId);
            }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _activePlayerIds.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_activePlayerIds[i]);
            }
            _logger?.KInformation($"[KlothoEngine][Roster] PlayerJoined: playerId={joinedPlayerId}, rosterCount={_activePlayerIds.Count}, active=[{sb}], CurrentTick={CurrentTick}, _lastVerifiedTick={_lastVerifiedTick}");
#endif
        }

        // Does the frame already hold a SessionParticipantComponent slot for this player?
        // Components are dense-packed (ComponentStorageFlat.Add: ComponentsSpan[count]; Remove: swap-back),
        // so ComponentsSpan[0..Count) are the live slots — a PlayerId match scan, order-independent, no alloc.
        private static bool HasParticipantSlot(Frame frame, int playerId)
        {
            var slots = frame.GetStorage<SessionParticipantComponent>();
            var components = slots.ComponentsSpan;
            for (int i = 0; i < slots.Count; i++)
            {
                if (components[i].PlayerId == playerId)
                    return true;
            }
            return false;
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
            ApplyFullState(tick, stateData, stateHash, ApplyReason.LateJoin);

            _inputBuffer.Clear();
            _pendingCommands.Clear();

            _lastVerifiedTick = tick;
            _lastMatchedSyncTick = tick;
            _prevMatchedSyncTick = 0;
            _lastMismatchedSyncTick = -1; // new baseline — stale mismatch records no longer apply

            ClearPerPeerDesyncState();
            _resyncRetryCount = 0;

            _hasPendingRollback = false;
            _pendingRollbackTick = -1;

            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();

            StartCatchingUp();

            // Late-join/reconnect guests get a fresh engine and never pass
            // HandleGameStart, so EnableTimeSync (the sole P2P caller) was never reached —
            // they ran with _timeSyncEnabled false forever and never self-throttled, an
            // asymmetry vs start-join guests. Enable it
            // here, the P2P-only merge point of both seed paths. Ordering: after the state is
            // seeded (EnableTimeSync clears _remoteTicks; _activePlayerIds was restored by
            // ApplyFullState above, so the throttle has live inputs once catchup feeds them).
            EnableTimeSync();

            _logger?.KInformation($"[KlothoEngine][P2P] Late Join FullState received, starting catchup: tick={tick}");
        }
    }
}
