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

        // Returns true ONLY for actively reconnecting peers (IsReconnect=true). Cold-start
        // LateJoin peers (IsReconnect=false) return false — their watchdog activation during
        // the longer JoinTick wait is preserved as intentional behavior.
        private bool IsPlayerInActiveCatchup(int playerId)
        {
            foreach (var kvp in _lateJoinCatchups)
            {
                if (kvp.Value.PlayerId == playerId && kvp.Value.IsReconnect)
                    return true;
            }
            return false;
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
                // Reconnect: inject from the tick following LastSentTick (= reconnect entry tick).
                // Cold-start LateJoin: defer to JoinTick (existing behavior — pre-join input is
                // semantically undefined for a never-joined player).
                int injectStartTick = info.IsReconnect ? info.LastSentTick + 1 : info.JoinTick;
                if (_localTick >= injectStartTick)
                {
                    _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, _localTick);
                    SendCommand(_emptyCommandCache);
                }
            }
        }

        /// <summary>
        /// Path B: reactive insertion — synchronous callback on engine CanAdvanceTick() failure.
        /// Range-fills [_lastVerifiedTick+1, CurrentTick] for each disconnected/presumed-dropped
        /// player so the chain can advance past the stall window in a single pass. Sealing inside
        /// ForceInsertEmptyCommandsRange protects against late real packet overwrites.
        /// </summary>
        private void HandleDisconnectedInputNeeded(int tick)
        {
            // Host-only — pool is host-only managed. Also guards SendCommand inside
            // FillAndBroadcastDisconnectedRange from running on guests.
            if (!IsHost || _engine == null) return;

            int rangeFrom = _engine.LastVerifiedTick + 1;
            int rangeTo = tick; // caller passes CurrentTick — fill the entire stall window
            if (rangeTo < rangeFrom) return;

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                FillAndBroadcastDisconnectedRange(info.PlayerId, rangeFrom, rangeTo);
                // Range fill increments by 1 per call (not per tick filled) to avoid false
                // timeout trigger when stall window is large.
                info.PredictedTickCount++;
            }
        }

        // Shared by CheckQuorumMissPresumedDrop (activation-time direct fill) and
        // HandleDisconnectedInputNeeded (reactive fill on CanAdvanceTick failure). Host-only.
        // Performs local fill + seal via ForceInsertEmptyCommandsRange, then broadcasts the
        // same empty cmds so other guests fill their InputBuffer too (P2P star — host is the
        // single source for this player's input during presumed-drop / disconnected state).
        private void FillAndBroadcastDisconnectedRange(int playerId, int fromTick, int toTick)
        {
            if (toTick < fromTick || _engine == null || _commandFactory == null) return;

            _engine.ForceInsertEmptyCommandsRange(playerId, fromTick, toTick);

            for (int t = fromTick; t <= toTick; t++)
            {
                _commandFactory.PopulateEmpty(_emptyCommandCache, playerId, t);
                SendCommand(_emptyCommandCache);
            }
        }

        #endregion

        #region Reconnect: quorum-miss watchdog

        // Telemetry counters — emitted at match end via EmitPresumedDropMetrics().
        private int _presumedDropFalsePositiveCount;
        private int _presumedDropTrueCount;
        // Counts cmds dropped from relay due to local seal (Phase 2-1 ②-B). High count
        // signals frequent late retransmits that would have caused cross-peer divergence
        // without the seal guard.
        private int _relaySealDropCount;

        internal int PresumedDropFalsePositiveCount => _presumedDropFalsePositiveCount;
        internal int PresumedDropTrueCount => _presumedDropTrueCount;
        internal int RelaySealDropCount => _relaySealDropCount;

        // Each frame: if a remote peer's input is missing at _lastVerifiedTick + 1 for >=
        // QuorumMissDropTicks, mark them as presumed-dropped so reactive empty-fill activates
        // before the transport-level DisconnectTimeout fires (~5s).
        private void CheckQuorumMissPresumedDrop()
        {
            if (!IsHost || _engine == null || _simConfig == null)
                return;

            int threshold = _simConfig.QuorumMissDropTicks;
            if (threshold <= 0)
                return;

            int verifiedTick = _engine.LastVerifiedTick;
            int currentTick = _engine.CurrentTick;
            int lag = currentTick - verifiedTick;
            if (lag < threshold)
                return;

            int stallTick = verifiedTick + 1;

            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                int playerId = player.PlayerId;

                // Local player's input is always present (auto-injected empty).
                if (playerId == LocalPlayerId)
                    continue;

                // Already tracked as disconnected (transport-detected or previously presumed).
                if (IsPlayerDisconnected(playerId))
                    continue;

                // Peer has provided input at the stall tick — no presumed drop needed.
                if (_engine.HasCommand(stallTick, playerId))
                    continue;

                // Skip activation for players currently in active reconnect-catchup. Host has
                // already initiated the catchup flow (peerId<->playerId remapped in
                // HandleReconnectRequest, catchup batches in transit), and presuming a drop here
                // would emit a false positive every reconnect whose first-real-input latency
                // exceeds QuorumMissDropTicks.
                if (IsPlayerInActiveCatchup(playerId))
                    continue;

                var info = RentDisconnectedInfo();
                if (info == null)
                    continue;

                info.PlayerId = playerId;
                info.PeerId = -1;
                info.DisconnectTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                info.LastConfirmedTick = verifiedTick;
                info.PredictedTickCount = 0;
                info.IsPresumedDrop = true;
                _disconnectedPlayerCount++;

                _engine.NotifyPlayerDisconnected(playerId);

                _logger?.ZLogInformation($"[KlothoNetworkService][PresumedDrop] Activated quorum-miss watchdog: playerId={playerId}, stallTick={stallTick}, lag={lag}, threshold={threshold}");

                // Directly fill the stall window. CanAdvanceTick may never fail if peer is
                // sending late inputs at currentTick, so cannot rely on OnDisconnectedInputNeeded
                // callback alone. Fill is host-local + broadcast to keep all peers in sync.
                FillAndBroadcastDisconnectedRange(playerId, stallTick, currentTick);
            }
        }

        // Called from HandleCommandMessage when a real command arrives. If the player was
        // previously marked as presumed-dropped by the watchdog AND the cmd is AT the current
        // stall tick (= _lastVerifiedTick + 1), treat as recovery and clear presumed-drop state.
        // Late inputs for ticks past the stall don't recover the gap — keep presumed-drop active
        // so empty fill / broadcast continues.
        // Host-only: the disconnected player pool is managed by host only.
        private void OnRealCommandReceivedDuringPresumedDrop(ICommand command)
        {
            if (!IsHost) return;
            if (command == null) return;
            int playerId = command.PlayerId;
            var info = FindDisconnectedInfo(playerId);
            if (info == null || !info.IsPresumedDrop)
                return;

            // Only the exact stall tick is a recovery signal. Use dynamic stall tick from engine
            // since _lastVerifiedTick may advance between activation and this callback.
            int stallTick = (_engine?.LastVerifiedTick ?? info.LastConfirmedTick) + 1;
            if (command.Tick != stallTick)
                return;

            int presumedDuration = _engine != null ? _engine.CurrentTick - info.LastConfirmedTick : 0;

            info.Reset();
            _disconnectedPlayerCount--;
            _presumedDropFalsePositiveCount++;

            // Sync engine state. Without this, _disconnectedPlayerIds stays populated while
            // the pool entry is gone — next watchdog iteration would re-add via RentDisconnectedInfo.
            _engine?.NotifyPlayerReconnected(playerId);

            _logger?.ZLogInformation($"[KlothoNetworkService][PresumedDrop] False positive — real input arrived at stallTick: playerId={playerId}, stallTick={stallTick}, presumedDurationTicks={presumedDuration}");
        }

        // Promote a presumed-drop entry to a confirmed transport disconnect. Called from
        // HandlePeerDisconnected when the entry already exists. Returns true if the entry
        // was a presumed-drop (so caller skips the normal Add path).
        private bool PromotePresumedDropToConfirmed(int playerId, int peerId)
        {
            var info = FindDisconnectedInfo(playerId);
            if (info == null || !info.IsPresumedDrop)
                return false;

            info.IsPresumedDrop = false;
            info.PeerId = peerId;
            _presumedDropTrueCount++;
            return true;
        }

        internal void EmitPresumedDropMetrics(string role, int playerId)
        {
            // Host-only: watchdog and relay seal guard increment counters only on host.
            if (!IsHost) return;
            int total = _presumedDropFalsePositiveCount + _presumedDropTrueCount;
            float ratio = total > 0 ? (float)_presumedDropFalsePositiveCount / total : 0f;
            var engine = _engine as KlothoEngine;
            int unexpectedFullStateDrops = engine?.UnexpectedFullStateDropCount ?? 0;
            int resyncHashMismatches = engine?.ResyncHashMismatchCount ?? 0;
            int consecutiveDesyncPeak = engine?.ConsecutiveDesyncPeak ?? 0;
            int resyncRequestTotal = engine?.ResyncRequestTotalCount ?? 0;
            int postResyncDesync = engine?.PostResyncDesyncCount ?? 0;
            _logger?.ZLogInformation($"[Metrics][PresumedDrop] role={role} playerId={playerId} falsePositive={_presumedDropFalsePositiveCount} truePositive={_presumedDropTrueCount} ratio={ratio:F3} relaySealDrop={_relaySealDropCount} unexpectedFullStateDrop={unexpectedFullStateDrops} resyncHashMismatch={resyncHashMismatches} desyncPeak={consecutiveDesyncPeak} resyncRequestTotal={resyncRequestTotal} postResyncDesync={postResyncDesync}");
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

        internal void CheckChainStallTimeout()
        {
            if (_engine == null || _simConfig == null) return;
            if (_engine.State.IsEnded()) return;
            if (Phase != SessionPhase.Playing) return;
            if (_simConfig.TickIntervalMs <= 0) return;

            int lag = _engine.CurrentTick - _engine.LastVerifiedTick;
            int reconnectTimeoutTicks = _sessionConfig.ReconnectTimeoutMs / _simConfig.TickIntervalMs;
            int threshold = System.Math.Max(reconnectTimeoutTicks + 100, _simConfig.MinStallAbortTicks);
            if (lag < threshold) return;

            _logger?.ZLogWarning($"[KlothoNetworkService][ChainStallWatchdog] Aborting match — lag={lag} >= threshold={threshold} " +
                                 $"(ReconnectTimeoutMs={_sessionConfig.ReconnectTimeoutMs}, MinStallAbortTicks={_simConfig.MinStallAbortTicks}, " +
                                 $"TickIntervalMs={_simConfig.TickIntervalMs}, CurrentTick={_engine.CurrentTick}, LastVerifiedTick={_engine.LastVerifiedTick})");

            _engine.AbortMatch(AbortReason.ChainStallTimeout);

            if (_reconnectState != ReconnectState.None)
                _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;
        }

        #endregion

        #region Reconnect: Guest transport reconnect

        private void TryReconnectTransport()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastTransportReconnectTime < TRANSPORT_RECONNECT_INTERVAL_MS)
                return;

            _lastTransportReconnectTime = now;
            if (!_transport.Connect(_transport.RemoteAddress, _transport.RemotePort))
            {
                _logger?.ZLogError($"[KlothoNetworkService] Reconnect transport start failed — aborting reconnect");
                _reconnectState = ReconnectState.None;
                Phase = SessionPhase.Disconnected;
            }
        }

        private void SendReconnectRequest()
        {
            _reconnectRequestCache.SessionMagic = _sessionMagic;
            _reconnectRequestCache.PlayerId = LocalPlayerId;
            _reconnectRequestCache.DeviceId = GetDeviceId();
            _logger?.ZLogInformation($"[KlothoNetworkService] Sending ReconnectRequestMessage (playerId={_reconnectRequestCache.PlayerId}, deviceId='{_reconnectRequestCache.DeviceId}')");

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
                return;   // KlothoConnection bypass possible — primary apply via InitializeFromConnection pending buffer.

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            RebuildPlayerList(msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);

            _reconnectState = ReconnectState.WaitingForFullState;
            _fullStateRequestTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Defensive — primary application happens via InitializeFromConnection pending buffer.
            // Warm Reconnect (this handler reached): engine reused → overwrites prior value.
            // Caller responsibility: this handler is reached only when engine is wired.
            _engine.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Reconnect);
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
                _transport.DisconnectPeer(stalePeerId);
            }

            // 5. Accept the reconnect
            // Snapshot RTT sample before info.Reset() clears LastAvgRtt — used below to seed RecommendedExtraDelay.
            int reconnectAvgRtt = info.LastAvgRtt;
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
            _reconnectAcceptCache.RecommendedExtraDelay =
                ComputeRecommendedExtraDelay(reconnectAvgRtt, msg.PlayerId, peerId, "Reconnect");

            using (var serialized = _messageSerializer.SerializePooled(_reconnectAcceptCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            // 8. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine?.CurrentTick ?? 0);

            // 9. Register cold-start catchup so SpectatorInputMessage batches flow to the reconnecting peer.
            //    JoinTick = CurrentTick (immediate — existing PlayerId, no PlayerJoinCommand).
            //    IsReconnect=true skips OnLateJoinPlayerAdded (= PlayerJoinCommand insertion).
            //    LastSentTick = currentTick - 1 so the catchup batch starts at currentTick
            //    (= FullStateTick). Reconnect receiver's _lastVerifiedTick lands at FullStateTick - 1,
            //    so it needs FullStateTick's quorum to advance — but the receiver is still in
            //    _activePlayerIds (unlike a brand-new LateJoin player), so without this off-by-one
            //    fix the catchup misses sending FullStateTick's [other-player inputs] entirely and
            //    the receiver's chain stalls permanently at FullStateTick.
            int currentTick = _engine?.CurrentTick ?? 0;
            // JoinTick extends by InputDelay (+ host's RecommendedExtraDelay) to cover the
            // input slots whose P2P broadcasts the reconnect peer missed during disconnect.
            // Other players' input for tick T is broadcast at host tick (T - InputDelay -
            // host_extraDelay); the reconnect peer must catch up that window or its chain
            // stalls at JoinTick + 1 waiting for those inputs.
            int inputDelay = _engine?.InputDelay ?? 0;
            int recommendedExtraDelay = _engine?.RecommendedExtraDelay ?? 0;
            int joinTick = currentTick + inputDelay + recommendedExtraDelay;
            _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
            {
                PeerId = peerId,
                PlayerId = msg.PlayerId,
                LastSentTick = currentTick - 1,
                JoinTick = joinTick,
                IsReconnect = true,
            };

            // Zombie cleanup: disconnect port-retry sibling sockets from the same reconnecting
            // client. KlothoConnection's reconnect pattern accepts multiple transport sockets;
            // only one carries the ReconnectRequest. Others linger as zombies, polluting
            // _transport.Broadcast delivery to the legit peer.
            //
            // Keep criteria (conservative — wrong cleanup permanently kills legit peers, and
            // client-side does NOT auto-retry):
            //   - _peerToPlayer: registered players (incl. just-reconnected one above)
            //   - _peerSyncStates: mid-handshake peers (cold-start sync / LateJoin sync)
            //   - _spectators: registered spectators (no _peerToPlayer entry by design)
            //
            // NOTE: _pendingPeers is intentionally NOT a keep collection. Zombie port-retry
            // peers also occupy _pendingPeers (they never deliver app data, so the Remove
            // at first-msg never fires). Protecting _pendingPeers would shield zombies and
            // defeat this cleanup. Legit Step 1 cold-start peers are protected only by the
            // bundle window narrowness below.
            //
            // Bundle filter: peer accepted within +/-BUNDLE_WINDOW_MS of reconnect peer's
            // accept time. Tighter than age-based — port-retry burst is ~tens of ms; unrelated
            // cold-start peers virtually never accept within the window of a reconnect event.
            const long BUNDLE_WINDOW_MS = 200;
            if (!_peerConnectedAtMs.TryGetValue(peerId, out var reconnectAcceptedAtMs))
            {
                reconnectAcceptedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            // Snapshot before iterating — DisconnectPeer may trigger synchronous unregister
            // on the transport side, mutating its internal peer collection.
            _zombieScanSnapshot.Clear();
            foreach (var id in _transport.GetConnectedPeerIds())
                _zombieScanSnapshot.Add(id);
            int cleanupCount = 0;
            for (int idx = 0; idx < _zombieScanSnapshot.Count; idx++)
            {
                int connectedPeerId = _zombieScanSnapshot[idx];
                if (connectedPeerId == peerId) continue;
                if (_peerToPlayer.ContainsKey(connectedPeerId)) continue;
                if (_peerSyncStates.ContainsKey(connectedPeerId)) continue;
                bool isSpectator = false;
                for (int i = 0; i < _spectators.Count; i++)
                {
                    if (_spectators[i].PeerId == connectedPeerId) { isSpectator = true; break; }
                }
                if (isSpectator) continue;
                if (!_peerConnectedAtMs.TryGetValue(connectedPeerId, out var connAt))
                    continue;
                long deltaMs = System.Math.Abs(connAt - reconnectAcceptedAtMs);
                if (deltaMs > BUNDLE_WINDOW_MS) continue;
                _logger?.ZLogDebug($"[KlothoNetworkService][ZombieCleanup] peerId={connectedPeerId}, reason=bundle, deltaMs={deltaMs}");
                _transport.DisconnectPeer(connectedPeerId);
                cleanupCount++;
            }
            if (cleanupCount > 0)
                _logger?.ZLogDebug($"[KlothoNetworkService][ZombieCleanupSummary] reconnectPeerId={peerId}, disconnected={cleanupCount}, bundleMs={BUNDLE_WINDOW_MS}");

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
