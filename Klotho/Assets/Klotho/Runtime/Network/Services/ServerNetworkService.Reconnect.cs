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
        // Message cache
        private readonly ReconnectAcceptMessage _reconnectAcceptCache = new ReconnectAcceptMessage();
        private readonly ReconnectRejectMessage _reconnectRejectCache = new ReconnectRejectMessage();

        /// <summary>
        /// Handles a reconnect request (reuses the P2P HandleReconnectRequest pattern).
        /// </summary>
        private void HandleReconnectRequest(int peerId, ReconnectRequestMessage msg)
        {
            // 1. Validate magic
            if (msg.SessionMagic != _sessionMagic)
            {
                SendReconnectReject(peerId, 1);
                return;
            }

            // 2. Validate PlayerId
            var info = FindDisconnectedInfo(msg.PlayerId);
            if (info == null)
            {
                SendReconnectReject(peerId, 2);
                return;
            }

            // 3. Validate timeout
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - info.DisconnectTimeMs > _sessionConfig.ReconnectTimeoutMs)
            {
                info.Reset();
                _disconnectedPlayerCount--;
                SendReconnectReject(peerId, 3);
                return;
            }

            // 3.5. Validate deviceId (skip when not bound — info or msg empty)
            if (!string.IsNullOrEmpty(info.DeviceId) && info.DeviceId != msg.DeviceId)
            {
                SendReconnectReject(peerId, ReconnectRejectReason.DeviceMismatch);
                return;
            }

            // 4. Clean up stale peerId
            int stalePeerId = -1;
            foreach (var kvp in _peerToPlayer)
            {
                if (kvp.Value == msg.PlayerId)
                {
                    stalePeerId = kvp.Key;
                    break;
                }
            }
            if (stalePeerId >= 0)
            {
                _peerToPlayer.Remove(stalePeerId);
                _peerStates.Remove(stalePeerId);
                _transport.DisconnectPeer(stalePeerId);
            }

            // Snapshot RTT sample before info.Reset() — used by RecommendedExtraDelay below.
            int disconnectedAvgRtt = info.LastAvgRtt;

            // 5. Accept reconnect
            _peerToPlayer[peerId] = msg.PlayerId;
            info.Reset();
            _disconnectedPlayerCount--;
            _engine?.NotifyPlayerReconnected(msg.PlayerId);

            // 6. Send SimulationConfig (always sent regardless of cold-start vs warm reconnect).
            SendSimulationConfig(peerId);

            // 7. Send ReconnectAcceptMessage
            _reconnectAcceptCache.PlayerId = msg.PlayerId;
            _reconnectAcceptCache.CurrentTick = _engine?.CurrentTick ?? 0;
            _reconnectAcceptCache.SharedEpoch = _sharedClock.SharedEpoch;
            _reconnectAcceptCache.ClockOffset = 0;
            _reconnectAcceptCache.PlayerCount = _players.Count;
            _reconnectAcceptCache.PlayerIds.Clear();
            _reconnectAcceptCache.PlayerConnectionStates.Clear();
            for (int i = 0; i < _players.Count; i++)
            {
                _reconnectAcceptCache.PlayerIds.Add(_players[i].PlayerId);
                bool isDisconnected = IsPlayerDisconnected(_players[i].PlayerId);
                _reconnectAcceptCache.PlayerConnectionStates.Add(
                    isDisconnected ? (byte)PlayerConnectionState.Disconnected
                                   : (byte)PlayerConnectionState.Connected);
            }

            // SessionConfig block — used by the cold-start guest to rebuild SessionConfig.
            _reconnectAcceptCache.RandomSeed = _randomSeed;
            _reconnectAcceptCache.MaxPlayers = _sessionConfig.MaxPlayers;
            _reconnectAcceptCache.MinPlayers = _sessionConfig.MinPlayers;
            _reconnectAcceptCache.AllowLateJoin = _sessionConfig.AllowLateJoin;
            _reconnectAcceptCache.ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs;
            _reconnectAcceptCache.ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries;
            _reconnectAcceptCache.LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks;
            _reconnectAcceptCache.ResyncMaxRetries = _sessionConfig.ResyncMaxRetries;
            _reconnectAcceptCache.DesyncThresholdForResync = _sessionConfig.DesyncThresholdForResync;
            _reconnectAcceptCache.CountdownDurationMs = _sessionConfig.CountdownDurationMs;
            _reconnectAcceptCache.CatchupMaxTicksPerFrame = _sessionConfig.CatchupMaxTicksPerFrame;

            // Reuse stale RTT sample. Reconnect identity is keyed on playerId (preserved across the
            // disconnect/reconnect cycle), but RTT samples were keyed on the disconnected peerId.
            // The new peerId is different, so consult disconnectedAvgRtt (snapshot of info.LastAvgRtt
            // captured before info.Reset() above). Fallback to the new peerId's PeerSyncStates entry —
            // populated only if a fresh handshake ran before this point, which is unusual for warm Reconnect.
            int avgRtt = disconnectedAvgRtt;
            if (avgRtt <= 0 && _peerSyncStates.TryGetValue(peerId, out var syncState))
                syncState.GetBestSample(out avgRtt, out _);
            int reconnectSeedExtraDelay = ComputeRecommendedExtraDelay(avgRtt, msg.PlayerId, peerId, "Reconnect");
            _reconnectAcceptCache.RecommendedExtraDelay = reconnectSeedExtraDelay;
            // Seed push baseline so the first mid-match recompute does not redundantly push the same value.
            _lastPushedExtraDelay[peerId] = reconnectSeedExtraDelay;

            using (var serialized = _messageSerializer.SerializePooled(_reconnectAcceptCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            // 8. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine?.CurrentTick ?? 0);

            // 8. Register peer state (Playing — immediately Playing after reconnect)
            _peerStates[peerId] = new ServerPeerInfo
            {
                PeerId = peerId,
                PlayerId = msg.PlayerId,
                State = ServerPeerState.Playing,
                LastAckedTick = -1
            };

            // 9. Restore player state
            var player = _players.Find(p => p.PlayerId == msg.PlayerId);
            if (player != null)
                player.ConnectionState = PlayerConnectionState.Connected;
            OnPlayerReconnected?.Invoke(player);

            _logger?.ZLogInformation(
                $"[ServerNetworkService] Reconnect accepted: peerId={peerId}, playerId={msg.PlayerId}");
        }

        private DisconnectedPlayerInfo FindDisconnectedInfo(int playerId)
        {
            if (_disconnectedPlayerPool == null) return null;
            for (int i = 0; i < _disconnectedPlayerPool.Length; i++)
            {
                if (_disconnectedPlayerPool[i].PlayerId == playerId)
                    return _disconnectedPlayerPool[i];
            }
            return null;
        }

        private bool IsPlayerDisconnected(int playerId)
        {
            if (_disconnectedPlayerPool == null) return false;
            for (int i = 0; i < _disconnectedPlayerPool.Length; i++)
            {
                if (_disconnectedPlayerPool[i].IsActive && _disconnectedPlayerPool[i].PlayerId == playerId)
                    return true;
            }
            return false;
        }

        private void SendReconnectReject(int peerId, byte reason)
        {
            _reconnectRejectCache.Reason = reason;
            using (var serialized = _messageSerializer.SerializePooled(_reconnectRejectCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
        }
    }
}
