using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public partial class ServerNetworkService
    {
        private const int LATE_JOIN_HANDSHAKE_TIMEOUT_MS = 10000;

        /// <summary>
        /// Starts the Late Join handshake when a new peer connects during Playing state.
        /// </summary>
        private void HandleLateJoin(int peerId)
        {
            if (_sessionConfig != null && !_sessionConfig.AllowLateJoin)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] Late join not allowed, peer {peerId} rejected");
                SendJoinReject(peerId, 4); // LateJoinDisabled
                _transport.DisconnectPeer(peerId);
                return;
            }

            // Pending-aware capacity gate. Redundant with the gate in HandleDataReceived,
            // but kept as second-line defense for callers that may not pass through that dispatch.
            if (EffectivePlayerCount >= MaxPlayerCapacity)
            {
                _logger?.ZLogWarning($"[ServerNetworkService][HandleLateJoin] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                SendJoinReject(peerId, 2); // RoomFull
                _transport.DisconnectPeer(peerId);
                return;
            }

            _logger?.ZLogInformation($"[ServerNetworkService] Late join handshake started: peerId={peerId}");
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

        /// <summary>
        /// Synchronizes after Late Join handshake completes (section 3.8.3).
        /// CompleteLateJoinSync: assign PlayerId → send FullState → register for broadcast.
        /// </summary>
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
                PlayerName = $"Player{newPlayerId}",
                Ping = avgRtt,
                IsReady = true
            };
            _players.Add(newPlayer);
            _peerToPlayer[peerId] = newPlayerId;

            // 2. Calculate joinTick
            int joinTick = _engine.CurrentTick + _sessionConfig.LateJoinDelayTicks;

            // Send SimulationConfig first. The client's KlothoConnection must receive it before initialization
            // so the handshake completes in the same order as the Normal Join path.
            SendSimulationConfig(peerId);

            // 4. Send LateJoinAcceptMessage
            var accept = new LateJoinAcceptMessage
            {
                PlayerId = newPlayerId,
                CurrentTick = _engine.CurrentTick,
                Magic = _sessionMagic,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                PlayerCount = _players.Count,
                RandomSeed = _randomSeed,
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
            };
            for (int i = 0; i < _players.Count; i++)
            {
                accept.PlayerIds.Add(_players[i].PlayerId);
                accept.PlayerConnectionStates.Add((byte)_players[i].ConnectionState);
            }

            // Concat existing player PlayerConfigs — new player is excluded as config is not yet received.
            // accept.PlayerIds order and PlayerConfigLengths order must match (client parses i-th length ↔ PlayerIds[i]).
            int totalConfigBytes = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                int pid = _players[i].PlayerId;
                if (pid == newPlayerId) { accept.PlayerConfigLengths.Add(0); continue; }
                if (_playerConfigBytes.TryGetValue(pid, out var bytes))
                {
                    accept.PlayerConfigLengths.Add(bytes.Length);
                    totalConfigBytes += bytes.Length;
                }
                else
                {
                    accept.PlayerConfigLengths.Add(0);
                }
            }
            if (totalConfigBytes > 0)
            {
                accept.PlayerConfigData = new byte[totalConfigBytes];
                int writeOffset = 0;
                for (int i = 0; i < _players.Count; i++)
                {
                    int pid = _players[i].PlayerId;
                    if (pid == newPlayerId) continue;
                    if (!_playerConfigBytes.TryGetValue(pid, out var bytes)) continue;
                    Buffer.BlockCopy(bytes, 0, accept.PlayerConfigData, writeOffset, bytes.Length);
                    writeOffset += bytes.Length;
                }
            }
            else
            {
                accept.PlayerConfigData = Array.Empty<byte>();
            }

            using (var serialized = _messageSerializer.SerializePooled(accept))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // 5. Send FullState (must be before broadcast registration)
            OnFullStateRequested?.Invoke(peerId, _engine.CurrentTick);

            // 6. Peer state: Playing — immediately included in BroadcastVerifiedState targets.
            //    Previously: CatchingUp → promoted via TryPromoteCatchingUpPeer (on first accepted input).
            //    Bug: late-join guest's initial inputs are outside the server's Hard Tolerance window and get rejected,
            //    causing the peer to never get promoted. Now goes directly to Playing like Normal Join.
            _peerStates[peerId] = new ServerPeerInfo
            {
                PeerId = peerId,
                PlayerId = newPlayerId,
                State = ServerPeerState.Playing,
                LastAckedTick = -1
            };

            // 7. Add player to InputCollector
            _inputCollector.AddPlayer(newPlayerId);

            // 8. PlayerJoinCommand → store directly in InputBuffer (unlike P2P: instead of SendCommand broadcast)
            OnLateJoinPlayerAdded?.Invoke(newPlayerId, joinTick);

            // 9. Immediately resend the missed VerifiedState (lastVerifiedTick) — avoids hitting the guest client's Hard limit.
            //    The late-join client starts from fullStateTick but the server is already ahead,
            //    so if the latest VerifiedState is not sent immediately, `leadTicks` can accumulate
            //    until the next broadcast and hit the Hard limit.
            if (_lastVerifiedTick > 0 && _lastVerifiedBytes != null)
            {
                _transport.Send(peerId, _lastVerifiedBytes, _lastVerifiedBytesLength, DeliveryMethod.ReliableOrdered);
                _logger?.ZLogInformation(
                    $"[ServerNetworkService] Initial VerifiedState sent to late-join peer {peerId}: tick={_lastVerifiedTick}");
            }

            _logger?.ZLogInformation(
                $"[ServerNetworkService] Late join complete: peerId={peerId}, playerId={newPlayerId}, joinTick={joinTick}");
            OnPlayerJoined?.Invoke(newPlayer);
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
    }
}
