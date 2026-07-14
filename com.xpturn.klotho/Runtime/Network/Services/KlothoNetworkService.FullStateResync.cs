using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // Full state resync rate limit (host side)
        private readonly Dictionary<int, long> _lastResyncResponseTime = new Dictionary<int, long>();

        // Set when this guest sends a FullStateRequest (desync-resync), consumed in
        // HandleFullStateResponse to flip _lateJoinState to Active. Without it, the resync path
        // leaves _lateJoinState unchanged and HandleCatchupInputMessage silent-drops the host's
        // catchup batch for the resync tick — verified chain stalls permanently.
        private bool _expectingResyncFullState;

        // ── Full state resync ─────────────────────────────────────

        public void SendFullStateRequest(int currentTick)
        {
            _logger?.KInformation($"[KlothoNetworkService][SendFullStateRequest] Full state request: currentTick={currentTick}");

            _expectingResyncFullState = true;
            var msg = new FullStateRequestMessage { RequestTick = currentTick };
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
        }

        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
        {
            if (!IsHost)
            {
                _logger?.KWarning($"[KlothoNetworkService][BroadcastFullState] Ignored — guest cannot broadcast (tick={tick})");
                return;
            }
            _logger?.KInformation($"[KlothoNetworkService][BroadcastFullState] tick={tick}, stateSize={stateData?.Length ?? 0}, stateHash=0x{stateHash:X16}");

            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateHash = stateHash,
                StateData = stateData,
                KindEnum = kind,
                StaticFingerprint = (_engine as KlothoEngine)?.GetLocalStaticFingerprint() ?? 0,
            };
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
        }

        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
        {
            _logger?.KInformation($"[KlothoNetworkService][SendFullStateResponse] Full state response: peerId={peerId}, tick={tick}, stateSize={stateData?.Length ?? 0}, stateHash=0x{stateHash:X16}");

            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateHash = stateHash,
                StateData = stateData,
                StaticFingerprint = (_engine as KlothoEngine)?.GetLocalStaticFingerprint() ?? 0,
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            for (int i = 0; i < _spectators.Count; i++)
            {
                if (_spectators[i].PeerId == peerId)
                {
                    _spectators[i].LastSentTick = tick - 1;
                    _logger?.KInformation($"[KlothoNetworkService][SendFullState] Spectator {peerId}: tick={tick}, LastSentTick={tick - 1}, LastVerifiedTick={_engine?.LastVerifiedTick}");
                    SendSpectatorCatchupInputs(_spectators[i]);
                    break;
                }
            }
        }

        private void HandleFullStateRequest(int peerId, FullStateRequestMessage msg)
        {
            if (!IsHost) return;

            _logger?.KInformation($"[KlothoNetworkService][HandleFullStateRequest] Full state request received: peerId={peerId}, requestTick={msg.RequestTick}");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastResyncResponseTime.TryGetValue(peerId, out long lastTime)
                && now - lastTime < ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS)
            {
                _logger?.KWarning($"[KlothoNetworkService] FullStateRequest from peer {peerId} throttled (cooldown)");
                return;
            }

            _lastResyncResponseTime[peerId] = now;
            OnFullStateRequested?.Invoke(peerId, msg.RequestTick);

            // Register an input catchup for the resyncing peer, mirroring the reconnect
            // path (HandleReconnectRequest). desync-resync clears the guest's input buffer and seeds
            // _lastVerifiedTick = servedTick-1, so the guest needs servedTick's other-player inputs
            // replayed or its verified chain stalls permanently at servedTick. The snapshot is served
            // at the host's CurrentTick (above, via OnFullStateRequested), NOT msg.RequestTick — so
            // the catchup starts there: LastSentTick = CurrentTick-1. IsReconnect=true skips the
            // PlayerJoinCommand insertion (the peer is an existing player).
            if (_engine != null && _peerToPlayer.TryGetValue(peerId, out int resyncPlayerId))
            {
                int currentTick = _engine.CurrentTick;
                _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
                {
                    PeerId = peerId,
                    PlayerId = resyncPlayerId,
                    LastSentTick = currentTick - 1,
                    JoinTick = currentTick + _engine.InputDelay + _engine.RecommendedExtraDelay,
                    IsReconnect = true,
                };
            }
        }

        private void HandleFullStateResponse(FullStateResponseMessage msg)
        {
            if (IsHost) return;

            _logger?.KInformation($"[KlothoNetworkService] FullStateResponse received: tick={msg.Tick}, size={msg.StateData?.Length ?? 0}");
            (_engine as KlothoEngine)?.CheckStaticGeometryFingerprint(msg.StaticFingerprint);
            OnFullStateReceived?.Invoke(msg.Tick, msg.StateData, msg.StateHash, msg.KindEnum);

            // If a reconnect was in progress, transition to completion
            if (_reconnectState == ReconnectState.WaitingForFullState)
            {
                _reconnectState = ReconnectState.None;
                Phase = SessionPhase.Playing;
                // Reconnect path bypasses the cold-start LateJoin transition chain
                // (WaitingForFullState → CatchingUp → Active), leaving _lateJoinState at None.
                // HandleCatchupInputMessage's state guard then silent-drops the host's catchup
                // batch for FullStateTick, leaving _inputBuffer[FullStateTick] empty for other
                // players and stalling chain advance permanently. Set to Active here so the
                // batch passes the guard and normal P2P broadcast also flows in.
                _lateJoinState = LateJoinState.Active;
                OnReconnected?.Invoke();
                _logger?.KInformation($"[KlothoNetworkService][Reconnect] Reconnect complete: tick={msg.Tick}");
            }

            // Late join: FullState received → enter CatchingUp.
            // A CorrectiveReset broadcast is applied via the engine OnFullStateReceived
            // path above but MUST NOT drive the WaitingForFullState -> CatchingUp transition, else catchup
            // bookkeeping anchors on the corrective-reset tick instead of the late-join response tick.
            // Blacklist the known-bad kind (not a Unicast whitelist) so a future revived non-Connection
            // late-join that seeds via InitialState still completes the join. (Latent: the current
            // KlothoConnection flow seeds via SeedPlayersFromCatchupPayload and never sets WaitingForFullState.)
            if (_lateJoinState == LateJoinState.WaitingForFullState
                && msg.KindEnum != FullStateKind.CorrectiveReset)
            {
                _lateJoinState = LateJoinState.CatchingUp;
                Phase = SessionPhase.Playing;
                _engine.StartCatchingUp();
                _logger?.KInformation($"[KlothoNetworkService][LateJoin] Catchup start: tick={msg.Tick}");
            }

            // In-session desync-resync. Unlike reconnect/late-join, this path sets no
            // handshake state, leaving _lateJoinState unchanged (None on a cold-start guest), so
            // HandleCatchupInputMessage silent-drops the host's resync catchup batch. Flip to Active
            // here — mirroring the reconnect branch above — so the batch passes the ingest guard.
            // Recovery itself proceeds via the engine's normal predict/verify loop (no StartCatchingUp).
            // Guarded so a concurrent late-join (CatchingUp) is not downgraded.
            if (_expectingResyncFullState)
            {
                _expectingResyncFullState = false;
                if (_lateJoinState != LateJoinState.CatchingUp && _lateJoinState != LateJoinState.Active)
                    _lateJoinState = LateJoinState.Active;
            }
        }

        /// <summary>
        /// Clear data older than the specified tick.
        /// </summary>
        public void ClearOldData(int tick)
        {
            // Remove stale hashes (uses a cached list to avoid GC)
            _hashKeysToRemoveCache.Clear();
            foreach (var key in _syncHashes.Keys)
            {
                if (key.tick < tick)
                    _hashKeysToRemoveCache.Add(key);
            }
            for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
            {
                _syncHashes.Remove(_hashKeysToRemoveCache[i]);
            }
        }
    }
}
