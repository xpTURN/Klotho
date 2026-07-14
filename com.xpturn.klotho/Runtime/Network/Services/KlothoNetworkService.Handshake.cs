using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // ── Handshake: connection start ──────────────────────────────────

        private void HandlePeerConnected(int peerId)
        {
            if (!IsHost) return;

            _pendingPeers.Add(peerId);
            _peerConnectedAtMs[peerId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Phase < SessionPhase.Countdown)
            {
                Phase = SessionPhase.Syncing;
            }
        }

        private void StartHandshake(int peerId)
        {
            _logger?.KInformation($"[KlothoNetworkService][StartHandshake] Handshake start: peerId={peerId}");

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
            _logger?.KInformation($"[KlothoNetworkService][SendSyncRequest] Sync request sent: peerId={peerId}, seq={state.SyncPacketsSent}, attempt={state.Attempt}");

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
            _logger?.KInformation($"[KlothoNetworkService][HandleSyncReply] Sync reply received: peerId={peerId}, seq={msg.Sequence}, attempt={msg.Attempt}");

            if (msg.Magic != _sessionMagic)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncReply] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
                return;
            }

            if (!_peerSyncStates.TryGetValue(peerId, out var state))
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncReply] No sync state: peerId={peerId}");
                return;
            }

            if (state.Completed)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncReply] Already completed: peerId={peerId}");
                return;
            }

            if (msg.Sequence != state.SyncPacketsSent)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncReply] Sequence mismatch: peerId={peerId}, expected={state.SyncPacketsSent}, received={msg.Sequence}");
                return;
            }

            if (msg.Attempt != state.Attempt)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncReply] Attempt mismatch: peerId={peerId}, expected={state.Attempt}, received={msg.Attempt}");
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
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncRequest] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
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
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncComplete] Magic mismatch: peerId={peerId}, expected={_sessionMagic}, received={msg.Magic}");
                return;
            }

            if (Phase >= SessionPhase.Countdown)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleSyncComplete] Ignored: peerId={peerId}, phase={Phase} (already Countdown or above)");
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
                _logger?.KWarning($"[KlothoNetworkService][CompletePeerSync] Standard handshake completed after GameStart (race): peer={peerId}, dropping for LateJoin retry");
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode()); // client retries via LateJoin
                _peerSyncStates.Remove(peerId);
                return;
            }

            // The duplicate-peer guard must remain BEFORE TryReservePlayerSlot — otherwise a duplicate peer
            // would consume a slot before being rejected, leaking _assignedPlayerIdCount in the Post-GameStart path.
            if (_peerToPlayer.ContainsKey(peerId))
            {
                _logger?.KError($"[KlothoNetworkService][CompletePeerSync] Duplicate peer sync detected: peerId={peerId}");
                return;
            }

            // Identity validation gate — before slot reservation so a reject consumes no slot.
            // P2P validation is synchronous: on accept we proceed inline with the validated identity.
            if (!TryValidateIdentityP2P(peerId, isLateJoin: false, out bool validatorRan, out string vAccount, out string vDisplayName, out byte[] vEntitlement))
                return;

            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;
            state.GetBestSample(out int avgRtt, out long avgOffset);

            _peerToPlayer[peerId] = newPlayerId;

            // Identity: validated value (claimed name ignored) when a validator ran, else the claimed
            // name, else a fabricated fallback. Account stays empty unless validated.
            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                DisplayName = ResolveJoinDisplayName(peerId, newPlayerId, validatorRan, vDisplayName),
                Account = validatorRan ? (vAccount ?? string.Empty) : string.Empty,
                // Promote the original ticket the host validated (already captured in
                // _peerTickets at PlayerJoin receipt) onto the durable PlayerInfo so it survives for
                // re-propagation. "" when the propagation gate is off.
                OriginalTicket = _propagateOriginalTickets && _peerTickets.TryGetValue(peerId, out var capturedTicket)
                    ? (capturedTicket ?? string.Empty) : string.Empty,
                // Store the guest's lobby-signed entitlement (host-side, never exposed on the roster wire).
                Entitlement = vEntitlement,
                IsReady = false,
                Ping = avgRtt
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            if (newPlayer.Entitlement != null && newPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via NormalJoin(host): playerId={newPlayerId}, bytes={newPlayer.Entitlement.Length}");
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);

            // Sync (normal join) path: NO extraDelay seed in P2P. Unlike SD where the server tolerates
            // late cmds within a window, P2P's host chain requires every peer's cmd at every tick to
            // advance verification. Adding extraDelay shifts the guest's cmd.Tick beyond the host's
            // initial chain head (ticks [0..extraDelay-1] will never receive a cmd) → permanent break.
            // LateJoin/Reconnect are OK because their catchup gap absorbs the lead.
            var msg = new SyncCompleteMessage
            {
                Magic = _sessionMagic,
                PlayerId = newPlayerId,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                RecommendedExtraDelay = 0,
            };
            // Attach the pre-game roster snapshot (including the just-added newPlayer) so the joining
            // guest builds its full player list immediately. KlothoConnection forwards these into
            // ConnectionResult, then InitializeFromConnection rebuilds the player list from them.
            for (int i = 0; i < _players.Count; i++)
            {
                msg.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger, (byte)_players[i].ConnectionState,
                    _players[i].IsReady ? (byte)1 : (byte)0));
                // Index-parallel original tickets so the joining guest re-verifies every
                // existing player. Only when the gate is on → empty list otherwise (guest skips).
                if (_propagateOriginalTickets)
                    msg.RosterTickets.Add(_players[i].OriginalTicket ?? string.Empty);
            }
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // Send SimulationConfig (host authority model)
            SendSimulationConfig(peerId);

            // Notify existing peers (and spectators) of the new player so their player list stays
            // consistent before StartGame. Exclude the new joiner, which already received the full
            // roster via the SyncCompleteMessage above.
            var joinNotification = new PlayerJoinNotificationMessage
            {
                PlayerId = newPlayerId,
                ConnectionState = (byte)newPlayer.ConnectionState,
                IsReady = newPlayer.IsReady,
                Account = newPlayer.Account ?? string.Empty,
                DisplayName = newPlayer.DisplayName ?? string.Empty,
                // The new player's original ticket so existing peers re-verify it.
                // Already "" when the gate is off (newPlayer.OriginalTicket gated above).
                OriginalTicket = newPlayer.OriginalTicket ?? string.Empty,
            };
            RelayMessage(joinNotification, excludePeerId: peerId, DeliveryMethod.ReliableOrdered);
            // RelayMessage only reaches _peerToPlayer; spectators (_spectators, disjoint) need a separate send.
            if (_spectators.Count > 0)
            {
                using (var serialized = _messageSerializer.SerializePooled(joinNotification))
                {
                    for (int i = 0; i < _spectators.Count; i++)
                        _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }

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
                _logger?.KInformation($"[KlothoNetworkService][HandleConnected] Reconnect mode: sending ReconnectRequest");
                _reconnectState = ReconnectState.SendingRequest;
                SendReconnectRequest();
                return;
            }

            _lateJoinState = LateJoinState.WaitingForAccept;
            _playerJoinMessageCache.DeviceId = GetDeviceId();
            _logger?.KInformation($"[KlothoNetworkService][HandleConnected] Normal mode: sending PlayerJoinMessage (deviceId='{_playerJoinMessageCache.DeviceId}')");
            
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
            _logger?.KInformation($"[KlothoNetworkService][HandlePeerDisconnected] Peer disconnected: peerId={peerId}");

            _pendingPeers.Remove(peerId);
            _peerConnectedAtMs.Remove(peerId);
            _lateJoinCatchups.Remove(peerId);

            // Rate-limit windows are keyed by peerId, and peerIds are recycled. Leaving an entry
            // behind hands the next peer to get this id the previous peer's window, so its FIRST
            // request is dropped — a reconnecting guest would then stall until its own resync timeout.
            // Unconditional (spectators and pending peers are throttled too, and are not in _peerToPlayer).
            _lastResyncResponseTime.Remove(peerId);
            _probeServeState.Remove(peerId);

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = FindPlayerById(playerId);
                if (player != null)
                {
                    if (IsHost && Phase == SessionPhase.Playing)
                    {
                        // Host: guest disconnected during Playing → wait for reconnect
                        // Guests do not take this path — guest reconnect starts in HandleDisconnected
                        _logger?.KInformation($"[KlothoNetworkService][HandlePeerDisconnected] Host: player {playerId} disconnected during Playing, waiting for reconnect");
                        player.ConnectionState = PlayerConnectionState.Disconnected;

                        // Quorum-miss watchdog may have already added this player as
                        // presumed-dropped. Promote the existing entry instead of renting a new one.
                        if (PromotePresumedDropToConfirmed(playerId, peerId))
                        {
                            _peerDeviceIds.TryGetValue(peerId, out var promotedDeviceId);
                            var promoted = FindDisconnectedInfo(playerId);
                            if (promoted != null)
                            {
                                promoted.DeviceId = promotedDeviceId ?? string.Empty;
                                if (_peerSyncStates.TryGetValue(peerId, out var promotedSyncState))
                                    promotedSyncState.GetBestSample(out promoted.LastAvgRtt, out _);
                            }
                            OnPlayerDisconnected?.Invoke(player);
                            // Confirmed disconnect via presumed→transport promote. The
                            // engine was already notified at the watchdog (:251, not propagated),
                            // so this is the first point guests learn the confirmed drop.
                            BroadcastPlayerState(playerId, PlayerStateChange.Disconnected);
                        }
                        else
                        {
                            var info = RentDisconnectedInfo();
                            if (info == null)
                            {
                                _logger?.KWarning($"[KlothoNetworkService][HandlePeerDisconnected] Disconnected player pool exhausted, removing player {playerId}");
                                int prevPlayerCount = _players.Count;
                                bool prevAllReady = AllPlayersReady;
                                _players.Remove(player);
                                _engine?.NotifyPlayerLeft(playerId);
                                OnPlayerLeft?.Invoke(player);
                                RaisePlayerCountIfChanged(prevPlayerCount);
                                RaiseAllPlayersReadyIfChanged(prevAllReady);
                                BroadcastPlayerState(playerId, PlayerStateChange.Left);
                            }
                            else
                            {
                                info.PlayerId = playerId;
                                info.PeerId = peerId;
                                info.DisconnectTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                info.LastConfirmedTick = _engine?.CurrentTick ?? 0;
                                info.PredictedTickCount = 0;
                                // Snapshot last good RTT sample before peer sync state is discarded —
                                // used by Reconnect path to seed RecommendedExtraDelay (matches SD pattern).
                                if (_peerSyncStates.TryGetValue(peerId, out var disconnSyncState))
                                    disconnSyncState.GetBestSample(out info.LastAvgRtt, out _);
                                _peerDeviceIds.TryGetValue(peerId, out var disconnectedDeviceId);
                                info.DeviceId = disconnectedDeviceId ?? string.Empty;
                                _disconnectedPlayerCount++;
                                _engine?.NotifyPlayerDisconnected(playerId);
                                OnPlayerDisconnected?.Invoke(player);
                                BroadcastPlayerState(playerId, PlayerStateChange.Disconnected);
                                // Do not remove from _players — keep the slot
                            }
                        }
                    }
                    else
                    {
                        // Disconnect before Playing or guest-side → remove immediately (existing logic)
                        _logger?.KInformation($"[KlothoNetworkService][HandlePeerDisconnected] Player {playerId} removed (IsHost={IsHost}, Phase={Phase})");
                        int prevPlayerCount = _players.Count;
                        bool prevAllReady = AllPlayersReady;
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                        RaisePlayerCountIfChanged(prevPlayerCount);
                        RaiseAllPlayersReadyIfChanged(prevAllReady);

                        // Notify remaining peers of the lobby leave so their player list stays consistent
                        // before StartGame. BroadcastMessagePooled (host → _transport.Broadcast) reaches
                        // players and spectators in one call, and the leaving peer is already disconnected.
                        // This runs host-only; the branch also runs guest-side, where IsHost is false.
                        if (IsHost)
                        {
                            BroadcastMessagePooled(
                                new PlayerLeaveNotificationMessage { PlayerId = playerId },
                                DeliveryMethod.ReliableOrdered);

                            // A not-ready player leaving can make the remaining roster all-ready.
                            // Re-evaluate the start condition here so the room is not stuck waiting
                            // for another PlayerReadyMessage (the only other trigger).
                            if (!_gameStarted && AllPlayersReady && _players.Count >= _sessionConfig.MinPlayers)
                                StartGame();
                        }
                    }
                }
                _peerToPlayer.Remove(peerId);
            }
            _peerSyncStates.Remove(peerId);
            _peerDeviceIds.Remove(peerId);
            _peerClaimedDisplayNames.Remove(peerId);
            _peerTickets.Remove(peerId);
            // Drop the departed peer's dynamic-delay state (aggregate hygiene).
            _reportedEffective.Remove(peerId);
            _peerTargetBaseline.Remove(peerId);

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
                // During Playing the phase is owned by match end/abort logic — an empty-room
                // reset here would silently drop a live match. Guests start reconnect in
                // HandleDisconnected; hosts keep playing (matches the dead-transport
                // timeout-leave behavior, where this block never runs). The
                // timeout-leave transport cut removes the player and resets the pool entry
                // BEFORE disconnecting the peer, so every count above is zero when the cut's
                // disconnect event lands — previously only the pool-exhaustion immediate
                // leave could reach here while Playing.
                if (Phase == SessionPhase.Playing || Phase == SessionPhase.Countdown)
                {
                    // Skip — see above. Countdown is also skipped: a lobby-leave re-eval may have
                    // just called StartGame() above, and resetting Phase here would revert it.
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
