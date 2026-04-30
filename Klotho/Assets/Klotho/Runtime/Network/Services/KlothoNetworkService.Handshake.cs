using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // ── Handshake: connection start ──────────────────────────────────

        private void HandlePeerConnected(int peerId)
        {
            if (!IsHost) return;

            _pendingPeers.Add(peerId);
            if (Phase < SessionPhase.Countdown)
            {
                Phase = SessionPhase.Syncing;
            }
        }

        private void StartHandshake(int peerId)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][StartHandshake] Handshake start: peerId={peerId}");

            var state = new PeerSyncState
            {
                PeerId = peerId,
                SyncPacketsSent = 0,
                RttSamples = new long[NUM_SYNC_PACKETS],
                ClockOffsetSamples = new long[NUM_SYNC_PACKETS],
                Completed = false
            };
            _peerSyncStates[peerId] = state;
            SendSyncRequest(peerId, state);
        }

        // ── Handshake: SyncRequest/Reply round-trip (5 times) ────────────────

        private void SendSyncRequest(int peerId, PeerSyncState state)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][SendSyncRequest] Sync request sent: peerId={peerId}, seq={state.SyncPacketsSent}, attempt={state.Attempt}");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state.LastSyncSentTime = now;
            var msg = new SyncRequestMessage
            {
                Magic = _sessionMagic,
                Sequence = state.SyncPacketsSent,
                Attempt = state.Attempt,
                HostTime = now
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        private void HandleSyncReply(int peerId, SyncReplyMessage msg)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][HandleSyncReply] Sync reply received: peerId={peerId}, seq={msg.Sequence}, attempt={msg.Attempt}");

            if (msg.Magic != _sessionMagic)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncReply] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
                return;
            }

            if (!_peerSyncStates.TryGetValue(peerId, out var state))
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncReply] No sync state: peerId={peerId}");
                return;
            }

            if (state.Completed)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncReply] Already completed: peerId={peerId}");
                return;
            }

            if (msg.Sequence != state.SyncPacketsSent)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncReply] Sequence mismatch: peerId={peerId}, expected={state.SyncPacketsSent}, received={msg.Sequence}");
                return;
            }

            if (msg.Attempt != state.Attempt)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncReply] Attempt mismatch: peerId={peerId}, expected={state.Attempt}, received={msg.Attempt}");
                return; // Reply from a previous Attempt — discard
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rtt = now - state.LastSyncSentTime;
            long offset = msg.ClientTime - state.LastSyncSentTime - rtt / 2;

            state.RttSamples[state.SyncPacketsSent] = rtt;
            state.ClockOffsetSamples[state.SyncPacketsSent] = offset;
            state.SyncPacketsSent++;

            if (state.SyncPacketsSent >= NUM_SYNC_PACKETS)
            {
                CompletePeerSync(peerId, state);
            }
            else
            {
                SendSyncRequest(peerId, state);
            }
        }

        private void HandleSyncRequest(int peerId, SyncRequestMessage msg)
        {
            if (_sessionMagic == 0)
            {
                _sessionMagic = msg.Magic;
            }
            else if (msg.Magic != _sessionMagic)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncRequest] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
                return;
            }

            var reply = new SyncReplyMessage
            {
                Magic = msg.Magic,
                Sequence = msg.Sequence,
                Attempt = msg.Attempt,
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            using (var serialized = _messageSerializer.SerializePooled(reply))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        // ── Handshake: completion (player creation + SharedTimeClock finalization) ──

        private void HandleSyncComplete(int peerId, SyncCompleteMessage msg)
        {
            if (msg.Magic != _sessionMagic)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncComplete] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
                return;
            }

            if (Phase >= SessionPhase.Countdown)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][HandleSyncComplete] Ignored: peerId={peerId}, phase={Phase} (already Countdown or above)");
                return;
            }

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            _lateJoinState = LateJoinState.None;
            Phase = SessionPhase.Synchronized;
            OnLocalPlayerIdAssigned?.Invoke(LocalPlayerId);
        }

        private void CompletePeerSync(int peerId, PeerSyncState state)
        {
            if (state.IsLateJoin)
            {
                CompleteLateJoinSync(peerId, state);
                return;
            }

            // Race guard for a standard handshake completing after StartGame().
            // _gameStarted flips at StartGame() entry before Phase changes, so a standard
            // handshake completing past this point lands in a wire/PlayerId mismatch (the peer never
            // received GameStartMessage). Drop and let the client retry via the LateJoin path.
            // !state.IsLateJoin is guaranteed by the dispatch above; kept explicit for clarity.
            if (_gameStarted && !state.IsLateJoin)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][CompletePeerSync] Standard handshake completed after GameStart (race): peer={peerId}, dropping for LateJoin retry");
                _transport.DisconnectPeer(peerId);
                _peerSyncStates.Remove(peerId);
                return;
            }

            // The duplicate-peer guard must remain BEFORE TryReservePlayerSlot — otherwise a duplicate peer
            // would consume a slot before being rejected, leaking _assignedPlayerIdCount in the Post-GameStart path.
            if (_peerToPlayer.ContainsKey(peerId))
            {
                _logger?.ZLogError($"[KlothoNetworkService][CompletePeerSync] Duplicate peer sync detected: peerId={peerId}");
                return;
            }

            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;
            state.GetBestSample(out int avgRtt, out long avgOffset);

            _peerToPlayer[peerId] = newPlayerId;

            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                PlayerName = $"Player{newPlayerId}",
                IsReady = false,
                Ping = avgRtt
            };
            _players.Add(newPlayer);

            var msg = new SyncCompleteMessage
            {
                Magic = _sessionMagic,
                PlayerId = newPlayerId,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // Send SimulationConfig (host authority model)
            SendSimulationConfig(peerId);

            Phase = SessionPhase.Synchronized;
            OnPlayerJoined?.Invoke(newPlayer);
        }

        private bool HasActiveSyncingPeers()
        {
            foreach (var kvp in _peerSyncStates)
            {
                if (!kvp.Value.Completed)
                    return true;
            }
            return false;
        }

        private void HandleConnected()
        {
            if (_reconnectState == ReconnectState.WaitingForTransport)
            {
                // Reconnect mode — send ReconnectRequest and transition state
                _logger?.ZLogInformation($"[KlothoNetworkService][HandleConnected] Reconnect mode: sending ReconnectRequest");
                _reconnectState = ReconnectState.SendingRequest;
                SendReconnectRequest();
                return;
            }

            _lateJoinState = LateJoinState.WaitingForAccept;
            _playerJoinMessageCache.DeviceId = GetDeviceId();
            _logger?.ZLogInformation($"[KlothoNetworkService][HandleConnected] Normal mode: sending PlayerJoinMessage (deviceId='{_playerJoinMessageCache.DeviceId}')");
            
            using (var serialized = _messageSerializer.SerializePooled(_playerJoinMessageCache))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandleDisconnected(DisconnectReason reason)
        {
            if (IsHost) return;

            bool reconnectEligible = reason == DisconnectReason.NetworkFailure
                                  || reason == DisconnectReason.ReconnectRequested;

            if (Phase == SessionPhase.Playing && reconnectEligible)
            {
                // Disconnected mid-game by network failure → enter reconnect-attempt mode
                _reconnectState = ReconnectState.WaitingForTransport;
                _reconnectStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _reconnectRetryCount = 0;
                OnReconnecting?.Invoke();
                _engine?.PauseForReconnect();
            }
            else
            {
                if (_lateJoinState != LateJoinState.None)
                {
                    _engine?.CancelExpectFullState();
                    _lateJoinState = LateJoinState.None;
                }
                Phase = SessionPhase.Disconnected;
            }
        }

        private void HandlePeerDisconnected(int peerId)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][HandlePeerDisconnected] Peer disconnected: peerId={peerId}");

            _pendingPeers.Remove(peerId);
            _lateJoinCatchups.Remove(peerId);

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = _players.Find(p => p.PlayerId == playerId);
                if (player != null)
                {
                    if (IsHost && Phase == SessionPhase.Playing)
                    {
                        // Host: guest disconnected during Playing → wait for reconnect
                        // Guests do not take this path — guest reconnect starts in HandleDisconnected
                        _logger?.ZLogInformation($"[KlothoNetworkService][HandlePeerDisconnected] Host: player {playerId} disconnected during Playing, waiting for reconnect");
                        player.ConnectionState = PlayerConnectionState.Disconnected;
                        var info = RentDisconnectedInfo();
                        if (info == null)
                        {
                            _logger?.ZLogWarning($"[KlothoNetworkService][HandlePeerDisconnected] Disconnected player pool exhausted, removing player {playerId}");
                            _players.Remove(player);
                            _engine?.NotifyPlayerLeft(playerId);
                            OnPlayerLeft?.Invoke(player);
                        }
                        else
                        {
                            info.PlayerId = playerId;
                            info.PeerId = peerId;
                            info.DisconnectTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            info.LastConfirmedTick = _engine?.CurrentTick ?? 0;
                            info.PredictedTickCount = 0;
                            _peerDeviceIds.TryGetValue(peerId, out var disconnectedDeviceId);
                            info.DeviceId = disconnectedDeviceId ?? string.Empty;
                            _disconnectedPlayerCount++;
                            _engine?.NotifyPlayerDisconnected(playerId);
                            OnPlayerDisconnected?.Invoke(player);
                            // Do not remove from _players — keep the slot
                        }
                    }
                    else
                    {
                        // Disconnect before Playing or guest-side → remove immediately (existing logic)
                        _logger?.ZLogInformation($"[KlothoNetworkService][HandlePeerDisconnected] Player {playerId} removed (IsHost={IsHost}, Phase={Phase})");
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                    }
                }
                _peerToPlayer.Remove(peerId);
            }
            _peerSyncStates.Remove(peerId);
            _peerDeviceIds.Remove(peerId);

            for (int i = _spectators.Count - 1; i >= 0; i--)
            {
                if (_spectators[i].PeerId == peerId)
                {
                    _spectators.RemoveAt(i);
                    break;
                }
            }

            if (_peerToPlayer.Count == 0 && _peerSyncStates.Count == 0
                && _pendingPeers.Count == 0 && _disconnectedPlayerCount == 0)
            {
                // Guest: when Playing, reconnect is started in HandleDisconnected, so do not change Phase
                if (!IsHost && Phase == SessionPhase.Playing)
                {
                    // Skip — reconnect is handled in HandleDisconnected
                }
                else
                {
                    Phase = IsHost ? SessionPhase.Lobby : SessionPhase.Disconnected;
                }
            }
            else if (Phase == SessionPhase.Syncing && !HasActiveSyncingPeers() && _pendingPeers.Count == 0)
            {
                Phase = SessionPhase.Synchronized;
            }
        }
    }
}
