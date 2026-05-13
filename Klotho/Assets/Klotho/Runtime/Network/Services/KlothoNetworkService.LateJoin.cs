using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // Late join
        private const int LATE_JOIN_HANDSHAKE_TIMEOUT_MS = 10000;
        private readonly Dictionary<int, LateJoinCatchupInfo> _lateJoinCatchups = new Dictionary<int, LateJoinCatchupInfo>();

        // Late join: guest state machine
        private enum LateJoinState { None, WaitingForAccept, WaitingForFullState, CatchingUp, Active }
        private LateJoinState _lateJoinState;

        // ── Late Join (Guest) ──────────────────────

        /// <summary>
        /// Restores _players / RandomSeed from a LateJoinAcceptMessage received via KlothoConnection on the Late Join path.
        /// Must be called by KlothoSession.Create before engine.Initialize so that the _activePlayerIds copy loop
        /// inside Engine.Initialize is populated correctly. Mirrors ServerDrivenClientService.SeedLateJoinPlayers.
        /// _sessionMagic / LocalPlayerId / _sharedClock are already set by InitializeFromConnection.
        /// </summary>
        public void SeedLateJoinPlayers(LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            SeedPlayersFromCatchupPayload(msg.RandomSeed, msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);
        }

        /// <summary>
        /// Cold-start Reconnect counterpart of SeedLateJoinPlayers. The host echoes the existing
        /// PlayerId via ReconnectAcceptMessage.PlayerId rather than allocating a new one. _sessionMagic is
        /// restored from persisted credentials at InitializeFromConnection.
        /// </summary>
        public void SeedReconnectPlayers(ReconnectPayload payload)
        {
            var msg = payload.AcceptMessage;
            SeedPlayersFromCatchupPayload(msg.RandomSeed, msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);
        }

        /// <summary>
        /// Common seed helper shared by Late Join and cold-start Reconnect.
        /// Restores _players / RandomSeed, sets Phase = Playing, and switches to CatchingUp so subsequent
        /// catchup input batches flow into _inputBuffer.
        /// </summary>
        private void SeedPlayersFromCatchupPayload(int randomSeed, int playerCount, List<int> playerIds, List<byte> playerConnectionStates)
        {
            RandomSeed = randomSeed;
            _players.Clear();
            for (int i = 0; i < playerCount && i < playerIds.Count; i++)
            {
                var p = new PlayerInfo { PlayerId = playerIds[i], IsReady = true };
                if (playerConnectionStates != null && i < playerConnectionStates.Count)
                    p.ConnectionState = (PlayerConnectionState)playerConnectionStates[i];
                _players.Add(p);
            }
            Phase = SessionPhase.Playing;
            SaveReconnectCredentialsIfApplicable();

            // KlothoConnection path bypasses the standard handshake message flow
            // (WaitingForAccept → WaitingForFullState → CatchingUp). The Accept + FullState are
            // pre-delivered via the payload, so jump straight to CatchingUp. Without this,
            // _lateJoinState stays at None and HandleCatchupComplete / HandleCatchupInputMessage
            // guards drop all subsequent input batches.
            _lateJoinState = LateJoinState.CatchingUp;
        }

        private void HandleCatchupComplete()
        {
            if (_lateJoinState != LateJoinState.CatchingUp)
                return;

            _lateJoinState = LateJoinState.Active;

            // Prefill (InputDelay + RecommendedExtraDelay) ticks worth of empty inputs.
            // RecommendedExtraDelay shifts the guest's first real cmd.Tick to currentTick + InputDelay + extraDelay,
            // so without extended prefill the local chain has a permanent gap at [currentTick + InputDelay, ...)
            // until the first real cmd arrives. Host side is unaffected because its roster activates this player
            // only at joinTick; the gap only manifests on the guest's own chain.
            int inputDelay = _engine.InputDelay;
            int extraDelay = _engine.RecommendedExtraDelay;
            int totalPrefill = inputDelay + extraDelay;
            int currentTick = _engine.CurrentTick;
            for (int i = 0; i < totalPrefill; i++)
            {
                int tick = currentTick + i;
                _commandFactory.PopulateEmpty(_emptyCommandCache, LocalPlayerId, tick);
                SendCommand(_emptyCommandCache);
            }

            _logger?.ZLogInformation($"[KlothoNetworkService][LateJoin] Transition to active: tick={currentTick}, prefilled {totalPrefill} empty commands (InputDelay={inputDelay}, extraDelay={extraDelay})");
        }

        private void HandleCatchupInputMessage(SpectatorInputMessage msg)
        {
            // Accept input batches in BOTH CatchingUp and Active states. With ticksAdvanced=0
            // catchup (host CurrentTick already at _catchupLastConfirmedTick + 1), state
            // transitions to Active immediately, but the gap-tick input batches still need to
            // flow into _inputBuffer for chain advance to proceed past the LateJoin gap.
            // ConfirmCatchupTick is a no-op once catchup ended.
            if (_lateJoinState != LateJoinState.CatchingUp && _lateJoinState != LateJoinState.Active)
                return;

            var reader = new SpanReader(msg.InputData, 0, msg.InputDataLength);
            for (int tick = msg.StartTick; tick < msg.StartTick + msg.TickCount; tick++)
            {
                int commandCount = reader.ReadInt32();
                for (int i = 0; i < commandCount; i++)
                {
                    var cmd = _commandFactory.DeserializeCommandRaw(ref reader);
                    _engine.ReceiveConfirmedCommand(cmd);
                }
                _engine.ConfirmCatchupTick(tick);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _logger?.ZLogTrace($"[KlothoNetworkService][CatchupInput] state={_lateJoinState}, ticks=[{msg.StartTick}..{msg.StartTick + msg.TickCount - 1}], dataLen={msg.InputDataLength}");
#endif
        }

        // DEAD in the current P2P flow: KlothoConnection bypass jumps _lateJoinState directly to CatchingUp
        // via SeedPlayersFromCatchupPayload (see line 64-69 of this file), so the WaitingForAccept guard
        // below is always taken. RecommendedExtraDelay application is handled by the pending buffer in
        // KlothoNetworkService.InitializeFromConnection + SubscribeEngine. Retained for future
        // non-Connection path scenarios.
        private void HandleLateJoinAccept(LateJoinAcceptMessage msg)
        {
            if (_lateJoinState != LateJoinState.WaitingForAccept)
                return;

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            RebuildPlayerList(msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);
            RandomSeed = msg.RandomSeed;

            _lateJoinState = LateJoinState.WaitingForFullState;
            _engine.ExpectFullState();
            _logger?.ZLogInformation($"[KlothoNetworkService][HandleLateJoinAccept] playerId={msg.PlayerId}, playerCount={msg.PlayerCount}, waiting for FullState");
        }

        // ── Late Join (Host) ──────────────────────

        private void CompleteLateJoinSync(int peerId, PeerSyncState state)
        {
            // PlayerId allocation goes through TryReservePlayerSlot — the Post-GameStart
            // path bumps _nextPlayerId and _assignedPlayerIdCount; on overflow it rejects + cleans up.
            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;

            state.GetBestSample(out int avgRtt, out long avgOffset);

            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                PlayerName = "",
                Ping = avgRtt,
                IsReady = true
            };
            _players.Add(newPlayer);
            _peerToPlayer[peerId] = newPlayerId;

            // 2. Target tick for PlayerJoinCommand
            int joinTick = _engine.CurrentTick + _sessionConfig.LateJoinDelayTicks;

            // Send SimulationConfig first so the guest's KlothoConnection can build
            // the Late Join result. Mirrors the SD path order.
            SendSimulationConfig(peerId);

            // 3. Send LateJoinAcceptMessage
            int lateJoinSeedExtraDelay = ComputeRecommendedExtraDelay(avgRtt, newPlayerId, peerId, "LateJoin");
            var accept = new LateJoinAcceptMessage
            {
                PlayerId = newPlayerId,
                CurrentTick = _engine.CurrentTick,
                Magic = _sessionMagic,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                PlayerCount = _players.Count,
                RandomSeed = RandomSeed,
                MaxPlayers = _sessionConfig.MaxPlayers,
                MinPlayers = _sessionConfig.MinPlayers,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ResyncMaxRetries = _sessionConfig.ResyncMaxRetries,
                DesyncThresholdForResync = _sessionConfig.DesyncThresholdForResync,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                CatchupMaxTicksPerFrame = _sessionConfig.CatchupMaxTicksPerFrame,
                RecommendedExtraDelay = lateJoinSeedExtraDelay,
            };
            for (int i = 0; i < _players.Count; i++)
            {
                accept.PlayerIds.Add(_players[i].PlayerId);
                accept.PlayerConnectionStates.Add((byte)_players[i].ConnectionState);
            }
            using (var serialized = _messageSerializer.SerializePooled(accept))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // 4. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine.CurrentTick);

            // 5. Register the catchup target
            _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
            {
                PeerId = peerId,
                PlayerId = newPlayerId,
                LastSentTick = _engine.CurrentTick,
                JoinTick = joinTick,
            };

            // 6. Insert PlayerJoinCommand + broadcast
            OnLateJoinPlayerAdded?.Invoke(newPlayerId, joinTick);

            // 7. Notify existing peers
            _logger?.ZLogInformation($"[KlothoNetworkService][CompleteLateJoinSync] Late join sync complete: peerId={peerId}, playerId={newPlayerId}, joinTick={joinTick}");
            OnPlayerJoined?.Invoke(newPlayer);
        }

        private void HandleLateJoin(int peerId)
        {
            if (_sessionConfig != null && !_sessionConfig.AllowLateJoin)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleLateJoin] Late join not allowed, peer {peerId} rejected");
                _transport.DisconnectPeer(peerId);
                return;
            }

            // HandleDataReceived dispatches here only when _gameStarted is true,
            // so the previous `Phase != Playing → StartHandshake` self-redirect is unreachable.
            // Kept as a defensive guard against future callers that bypass HandleDataReceived.
            if (!_gameStarted)
            {
                StartHandshake(peerId);
                return;
            }

            // Pending-aware capacity gate. Redundant with the gate in HandleDataReceived,
            // but kept as second-line defense for callers that may not pass through that dispatch.
            if (EffectivePlayerCount >= MaxPlayerCapacity)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleLateJoin] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                _transport.DisconnectPeer(peerId);
                return;
            }

            _logger?.ZLogInformation($"[KlothoNetworkService][HandleLateJoin] Late join handshake started: peerId={peerId}");
            var state = new PeerSyncState
            {
                PeerId = peerId,
                SyncPacketsSent = 0,
                RttSamples = new long[NUM_SYNC_PACKETS],
                ClockOffsetSamples = new long[NUM_SYNC_PACKETS],
                Attempt = 0,
                Completed = false,
                IsLateJoin = true
            };
            _peerSyncStates[peerId] = state;
            SendSyncRequest(peerId, state);
        }

        // Pending handshake count (LateJoin + general — both use _peerSyncStates).
        private int CountPendingHandshakes()
        {
            int count = 0;
            foreach (var kvp in _peerSyncStates)
            {
                if (!kvp.Value.Completed)
                    count++;
            }
            return count;
        }

        // Instance wrapper — config injection + path-tagged logging + structured metrics emission.
        // playerId = game-level player identifier (reported in metrics JSON for telemetry analysis).
        // peerId = connection-level identifier (kept in operational logs for connection debugging).
        // Pure computation lives in xpTURN.Klotho.Core.RecommendedExtraDelayCalculator (shared with SD path).
        private int ComputeRecommendedExtraDelay(int avgRtt, int playerId, int peerId, string pathTag)
        {
            var (extraDelay, fallback, rttTicks, raw, clamped) = RecommendedExtraDelayCalculator.Compute(
                avgRtt,
                _simConfig.TickIntervalMs,
                _simConfig.LateJoinDelaySafety,
                _simConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            if (fallback)
                _logger?.ZLogWarning($"[KlothoNetworkService][{pathTag}] FallbackPath: avgRtt={avgRtt}ms invalid, peerId={peerId}, clamped={extraDelay}");
            else
                _logger?.ZLogDebug($"[KlothoNetworkService][{pathTag}] RecommendedExtraDelay computed: peerId={peerId}, avgRtt={avgRtt}ms, clamped={extraDelay}");

            // Structured JSON-line metrics — single source of truth for verification scripts and
            // production telemetry. Bool literals lowercased for JSON validity. Path tag is controlled
            // ("LateJoin" / "Reconnect" / "Sync") so no escaping needed.
            string clampedStr = clamped ? "true" : "false";
            string fallbackStr = fallback ? "true" : "false";
            int safety = _simConfig.LateJoinDelaySafety;
            _logger?.ZLogInformation($"[Metrics][{pathTag}] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"{pathTag}\",\"avgRtt\":{avgRtt},\"rttTicks\":{rttTicks},\"safety\":{safety},\"raw\":{raw},\"clamped\":{clampedStr},\"extraDelay\":{extraDelay},\"fallback\":{fallbackStr}}}");

            return extraDelay;
        }
    }
}
