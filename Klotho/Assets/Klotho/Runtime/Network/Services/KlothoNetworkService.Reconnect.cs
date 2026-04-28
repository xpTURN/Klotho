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
        // Reconnect: disconnected-player pool + empty-command cache
        private const int DISCONNECT_INPUT_PREDICTION_LIMIT = 0; // 0 = unlimited until timeout
        private const int TRANSPORT_RECONNECT_INTERVAL_MS = 1000;
        private const int RECONNECT_REQUEST_TIMEOUT_MS = 5000;
        private const int RECONNECT_FULLSTATE_TIMEOUT_MS = 5000;
        private DisconnectedPlayerInfo[] _disconnectedPlayerInfoPool;
        private int _disconnectedPlayerCount;
        private ICommand _emptyCommandCache;

        // Reconnect: guest reconnect state
        private enum ReconnectState { None, WaitingForTransport, SendingRequest, WaitingForFullState, Failed }
        private ReconnectState _reconnectState;
        private long _reconnectStartTimeMs;
        private long _reconnectRequestSentTime;
        private long _fullStateRequestTime;
        private int _reconnectRetryCount;
        private long _lastTransportReconnectTime;

        #region Reconnect: DisconnectedPlayerInfo pool

        private void InitDisconnectedPlayerPool(int maxPlayers)
        {
            _disconnectedPlayerInfoPool = new DisconnectedPlayerInfo[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
                _disconnectedPlayerInfoPool[i] = new DisconnectedPlayerInfo();
            _disconnectedPlayerCount = 0;
        }

        private DisconnectedPlayerInfo RentDisconnectedInfo()
        {
            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                if (!_disconnectedPlayerInfoPool[i].IsActive)
                    return _disconnectedPlayerInfoPool[i];
            }
            return null;
        }

        private DisconnectedPlayerInfo FindDisconnectedInfo(int playerId)
        {
            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                if (_disconnectedPlayerInfoPool[i].PlayerId == playerId)
                    return _disconnectedPlayerInfoPool[i];
            }
            return null;
        }

        private bool IsPlayerDisconnected(int playerId)
        {
            return FindDisconnectedInfo(playerId) != null;
        }

        #endregion

        #region Reconnect: empty input injection

        /// <summary>
        /// Path A: pre-insertion — invoked once per frame from Update().
        /// Sends an empty command at _localTick locally and via broadcast through SendCommand().
        /// </summary>
        private void InjectDisconnectedPlayerInputs()
        {
            if (!IsHost || _disconnectedPlayerCount == 0)
                return;

            int targetTick = _localTick;

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                if (DISCONNECT_INPUT_PREDICTION_LIMIT > 0
                    && info.PredictedTickCount >= DISCONNECT_INPUT_PREDICTION_LIMIT)
                    continue;

                _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, targetTick);
                SendCommand(_emptyCommandCache);
                info.PredictedTickCount++;
            }
        }

        private void InjectCatchupPlayerInputs()
        {
            if (!IsHost || _lateJoinCatchups.Count == 0)
                return;

            foreach (var kvp in _lateJoinCatchups)
            {
                var info = kvp.Value;
                if (_localTick >= info.JoinTick)
                {
                    _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, _localTick);
                    SendCommand(_emptyCommandCache);
                }
            }
        }

        /// <summary>
        /// Path B: reactive insertion — synchronous callback on engine CanAdvanceTick() failure.
        /// Inserts directly via ForceInsertCommand() at Engine.CurrentTick, no broadcast.
        /// </summary>
        private void HandleDisconnectedInputNeeded(int tick)
        {
            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                if (_engine.HasCommand(tick, info.PlayerId))
                    continue;

                _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, tick);
                _engine.ForceInsertCommand(_emptyCommandCache);
                info.PredictedTickCount++;
            }
        }

        #endregion

        #region Reconnect: timeout

        private void CheckDisconnectedPlayerTimeout()
        {
            if (_disconnectedPlayerCount == 0)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                if (now - info.DisconnectTimeMs <= _sessionConfig.ReconnectTimeoutMs)
                    continue;

                int playerId = info.PlayerId;
                info.Reset();
                _disconnectedPlayerCount--;

                var player = _players.Find(p => p.PlayerId == playerId);
                if (player != null)
                {
                    _players.Remove(player);
                    _engine?.NotifyPlayerLeft(playerId);
                    OnPlayerLeft?.Invoke(player);
                }
            }
        }

        #endregion

        #region Reconnect: Guest transport reconnect

        private void TryReconnectTransport()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastTransportReconnectTime < TRANSPORT_RECONNECT_INTERVAL_MS)
                return;

            _lastTransportReconnectTime = now;
            _transport.Connect(_transport.RemoteAddress, _transport.RemotePort);
        }

        private void SendReconnectRequest()
        {
            _reconnectRequestCache.SessionMagic = _sessionMagic;
            _reconnectRequestCache.PlayerId = LocalPlayerId;

            using (var serialized = _messageSerializer.SerializePooled(_reconnectRequestCache))
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            _reconnectRequestSentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void UpdateReconnect()
        {
            if (_reconnectState == ReconnectState.None)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long elapsed = now - _reconnectStartTimeMs;

            if (elapsed > _sessionConfig.ReconnectTimeoutMs)
            {
                _reconnectState = ReconnectState.Failed;
                Phase = SessionPhase.Disconnected;
                OnReconnectFailed?.Invoke("Timeout");
                return;
            }

            switch (_reconnectState)
            {
                case ReconnectState.WaitingForTransport:
                    if (_transport.IsConnected)
                    {
                        _reconnectState = ReconnectState.SendingRequest;
                        SendReconnectRequest();
                    }
                    else
                    {
                        TryReconnectTransport();
                    }
                    break;

                case ReconnectState.SendingRequest:
                    if (now - _reconnectRequestSentTime > RECONNECT_REQUEST_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke("MaxRetries");
                            return;
                        }
                        SendReconnectRequest();
                    }
                    break;

                case ReconnectState.WaitingForFullState:
                    if (now - _fullStateRequestTime > RECONNECT_FULLSTATE_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke("MaxRetries");
                            return;
                        }
                        if (_transport.IsConnected)
                        {
                            _reconnectState = ReconnectState.SendingRequest;
                            SendReconnectRequest();
                        }
                        else
                        {
                            _reconnectState = ReconnectState.WaitingForTransport;
                        }
                    }
                    break;
            }
        }

        #endregion

        #region Reconnect: Guest reconnect accept/reject handling

        private void HandleReconnectAccept(ReconnectAcceptMessage msg)
        {
            if (_reconnectState != ReconnectState.SendingRequest)
                return;

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            RebuildPlayerList(msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);

            _reconnectState = ReconnectState.WaitingForFullState;
            _fullStateRequestTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void RebuildPlayerList(int playerCount, List<int> playerIds, List<byte> connectionStates)
        {
            _players.Clear();
            for (int i = 0; i < playerCount; i++)
            {
                var player = new PlayerInfo
                {
                    PlayerId = playerIds[i],
                    ConnectionState = (PlayerConnectionState)connectionStates[i]
                };
                _players.Add(player);
            }
        }

        private void HandleReconnectReject(ReconnectRejectMessage msg)
        {
            if (_reconnectState == ReconnectState.None)
                return;

            _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;

            // Any reject reason invalidates persisted credentials — discard.
            _reconnectCredentialsStore?.Clear();

            OnReconnectFailed?.Invoke(ReconnectRejectReason.ToName(msg.Reason));
        }

        #endregion

        #region Reconnect: Host reconnect request handling

        private void SendReconnectReject(int peerId, byte reason)
        {
            _reconnectRejectCache.Reason = reason;
            using (var serialized = _messageSerializer.SerializePooled(_reconnectRejectCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
        }

        private void HandleReconnectRequest(int peerId, ReconnectRequestMessage msg)
        {
            // 1. Validate magic
            if (msg.SessionMagic != _sessionMagic)
            {
                SendReconnectReject(peerId, 1); // Invalid magic
                return;
            }

            // 2. Validate PlayerId
            var info = FindDisconnectedInfo(msg.PlayerId);
            if (info == null)
            {
                SendReconnectReject(peerId, 2); // Invalid player
                return;
            }

            // 3. Validate timeout
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - info.DisconnectTimeMs > _sessionConfig.ReconnectTimeoutMs)
            {
                info.Reset();
                _disconnectedPlayerCount--;
                SendReconnectReject(peerId, 3); // Timed out
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
                _transport.DisconnectPeer(stalePeerId);
            }

            // 5. Accept the reconnect
            _peerToPlayer[peerId] = msg.PlayerId;
            info.Reset();
            _disconnectedPlayerCount--;
            _engine?.NotifyPlayerReconnected(msg.PlayerId);

            // 6. Send SimulationConfig (always sent regardless of cold-start vs warm reconnect).
            SendSimulationConfig(peerId);

            // 7. Send ReconnectAcceptMessage
            // Note: step 5 calls info.Reset() → IsPlayerDisconnected(msg.PlayerId) == false
            //       → the reconnect requester is reported as Connected (intentional)
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
            _reconnectAcceptCache.RandomSeed = RandomSeed;
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

            using (var serialized = _messageSerializer.SerializePooled(_reconnectAcceptCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            // 8. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine?.CurrentTick ?? 0);

            // 9. Register cold-start catchup so SpectatorInputMessage batches flow to the reconnecting peer.
            //    JoinTick = CurrentTick (immediate — existing PlayerId, no PlayerJoinCommand).
            //    IsReconnect=true skips OnLateJoinPlayerAdded (= PlayerJoinCommand insertion).
            int currentTick = _engine?.CurrentTick ?? 0;
            _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
            {
                PeerId = peerId,
                PlayerId = msg.PlayerId,
                LastSentTick = currentTick,
                JoinTick = currentTick,
                IsReconnect = true,
            };

            // 10. Restore player state + raise events
            var player = _players.Find(p => p.PlayerId == msg.PlayerId);
            if (player != null)
                player.ConnectionState = PlayerConnectionState.Connected;
            OnPlayerReconnected?.Invoke(player);

            _logger?.ZLogInformation($"[KlothoNetworkService][HandleReconnectRequest] Player {msg.PlayerId} reconnected: peerId={peerId}");
        }

        #endregion
    }
}
