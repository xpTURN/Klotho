using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // Full state resync rate limit (host side)
        private readonly Dictionary<int, long> _lastResyncResponseTime = new Dictionary<int, long>();

        // ── Full state resync ─────────────────────────────────────

        public void SendFullStateRequest(int currentTick)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][SendFullStateRequest] Full state request: currentTick={currentTick}");

            var msg = new FullStateRequestMessage { RequestTick = currentTick };
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
        }

        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
        {
            _logger?.ZLogInformation($"[KlothoNetworkService][SendFullStateResponse] Full state response: peerId={peerId}, tick={tick}, stateSize={stateData?.Length ?? 0}, stateHash=0x{stateHash:X16}");

            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateHash = stateHash,
                StateData = stateData
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
                    _logger?.ZLogInformation($"[KlothoNetworkService][SendFullState] Spectator {peerId}: tick={tick}, LastSentTick={tick - 1}, LastVerifiedTick={_engine?.LastVerifiedTick}");
                    SendSpectatorCatchupInputs(_spectators[i]);
                    break;
                }
            }
        }

        private void HandleFullStateRequest(int peerId, FullStateRequestMessage msg)
        {
            if (!IsHost) return;

            _logger?.ZLogInformation($"[KlothoNetworkService][HandleFullStateRequest] Full state request received: peerId={peerId}, requestTick={msg.RequestTick}");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastResyncResponseTime.TryGetValue(peerId, out long lastTime)
                && now - lastTime < RESYNC_RESPONSE_COOLDOWN_MS)
            {
                _logger?.ZLogWarning($"[KlothoNetworkService] FullStateRequest from peer {peerId} throttled (cooldown)");
                return;
            }

            _lastResyncResponseTime[peerId] = now;
            OnFullStateRequested?.Invoke(peerId, msg.RequestTick);
        }

        private void HandleFullStateResponse(FullStateResponseMessage msg)
        {
            if (IsHost) return;

            _logger?.ZLogInformation($"[KlothoNetworkService] FullStateResponse received: tick={msg.Tick}, size={msg.StateData?.Length ?? 0}");
            OnFullStateReceived?.Invoke(msg.Tick, msg.StateData, msg.StateHash);

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
                _logger?.ZLogInformation($"[KlothoNetworkService][Reconnect] Reconnect complete: tick={msg.Tick}");
            }

            // Late join: FullState received → enter CatchingUp
            if (_lateJoinState == LateJoinState.WaitingForFullState)
            {
                _lateJoinState = LateJoinState.CatchingUp;
                Phase = SessionPhase.Playing;
                _engine.StartCatchingUp();
                _logger?.ZLogInformation($"[KlothoNetworkService][LateJoin] Catchup start: tick={msg.Tick}");
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
