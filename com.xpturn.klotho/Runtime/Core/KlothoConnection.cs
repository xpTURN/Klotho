using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Class responsible for the early initialization phase. Performs connection, handshake, and SimulationConfig reception.
    /// Used on the guest side before KlothoSession.Create; not used by the host.
    /// Three guest entry paths (selected via factory method):
    ///   - Normal Join: SyncComplete + SimulationConfig.
    ///   - Late Join: SimulationConfig + LateJoinAccept + FullStateResponse.
    ///   - cold-start Reconnect: ReconnectAccept + SimulationConfig + FullStateResponse.
    /// warm reconnect (process-survival reconnect) is out of scope — handled in
    /// KlothoNetworkService / ServerDrivenClientService.HandleReconnectAccept.
    /// </summary>
    public class KlothoConnection
    {
        /// <summary>
        /// Default handshake upper bound for the Connect path (normal / late join), in milliseconds.
        /// Public so the Runtime.Unity async wrappers can reference it as a default argument.
        /// </summary>
        public const int DEFAULT_CONNECT_TIMEOUT_MS = 15000;

        private readonly INetworkTransport _transport;
        private readonly MessageSerializer _messageSerializer = new MessageSerializer();
        private readonly IKLogger _logger;

        private ConnectionResult _result;
        private Action<ConnectionResult> _onCompleted;
        private Action<Exception> _onFailed;
        private bool _completed;
        private NetworkMessageBase _preJoinMessage;

        // Late Join path buffering
        private SimulationConfigMessage _pendingSimConfig;
        private LateJoinAcceptMessage _pendingLateJoin;
        private (int tick, byte[] data, long hash)? _pendingFullState;

        // cold-start Reconnect path buffering
        private PersistedReconnectCredentials _reconnectCreds;        // null = Normal/LateJoin mode
        private ReconnectAcceptMessage _pendingReconnect;
        private string _deviceId = string.Empty;
        // Lobby identity carriage: loaded at Connect and sent in PlayerJoinMessage.
        private string _ticket = string.Empty;
        private string _claimedDisplayName = string.Empty;

        // Timeout
        private long _connectStartMs;
        private int _connectTimeoutMs = DEFAULT_CONNECT_TIMEOUT_MS;

        /// <summary>
        /// Whether the handshake has completed or failed. The external pump loop uses this as a termination condition.
        /// </summary>
        public bool IsCompleted => _completed;

        private KlothoConnection(INetworkTransport transport, IKLogger logger)
        {
            _transport = transport;
            _logger = logger;
        }

        /// <summary>
        /// Connects to the host and performs handshake + SimulationConfig reception.
        /// On completion, returns ConnectionResult via the onCompleted callback.
        /// On failure, invokes the onFailed callback.
        /// preJoinMessage: a prefix message sent before PlayerJoinMessage (e.g. RoomHandshakeMessage in SD multi-room).
        ///   If null, only PlayerJoinMessage is sent (P2P / SD single-room).
        /// </summary>
        public static KlothoConnection Connect(
            INetworkTransport transport, string host, int port,
            Action<ConnectionResult> onCompleted, Action<Exception> onFailed = null,
            IKLogger logger = null,
            NetworkMessageBase preJoinMessage = null,
            IDeviceIdProvider deviceIdProvider = null,
            int connectTimeoutMs = DEFAULT_CONNECT_TIMEOUT_MS,
            IPlayerIdentityProvider identityProvider = null,
            string claimedDisplayName = null)
        {
            if (connectTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs), connectTimeoutMs, "connectTimeoutMs must be positive.");

            var connection = new KlothoConnection(transport, logger);
            connection._onCompleted = onCompleted;
            connection._onFailed = onFailed;
            connection._preJoinMessage = preJoinMessage;
            connection._deviceId = deviceIdProvider?.GetDeviceId() ?? string.Empty;
            // The lobby ticket and the no-lobby claimed display name are independent client inputs.
            connection._ticket = identityProvider?.GetTicket() ?? string.Empty;
            connection._claimedDisplayName = claimedDisplayName ?? string.Empty;
            connection._connectTimeoutMs = connectTimeoutMs;

            transport.OnConnected += connection.HandleConnected;
            transport.OnDataReceived += connection.HandleDataReceived;
            transport.OnDisconnected += connection.HandleDisconnected;

            connection._connectStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!transport.Connect(host, port))
            {
                connection._completed = true;
                connection.Dispose();
                onFailed?.Invoke(new JoinFailedException(JoinFailReason.TransportStartFailed));
            }

            return connection;
        }

        /// <summary>
        /// Cold-start Reconnect factory.
        /// Connects to creds.RemoteAddress:RemotePort and presents the persisted credentials via
        /// ReconnectRequestMessage. On success, returns ConnectionResult { Kind = JoinKind.Reconnect, ReconnectPayload }.
        /// On reject (reason 1/2/3/4) or timeout, invokes onFailed.
        /// Handshake upper bound = creds.ReconnectTimeoutMs — the host slot lifetime.
        /// </summary>
        public static KlothoConnection Reconnect(
            INetworkTransport transport, PersistedReconnectCredentials creds,
            Action<ConnectionResult> onCompleted, Action<Exception> onFailed = null,
            IKLogger logger = null)
        {
            var connection = new KlothoConnection(transport, logger);
            connection._onCompleted = onCompleted;
            connection._onFailed = onFailed;

            // Defensive: a reconnect trigger fired with no persisted credentials — e.g. a second
            // cold-start attempt after the first consumed/cleared them, or a test driver re-firing.
            // Fail gracefully via onFailed instead of NRE'ing on creds.ReconnectTimeoutMs below.
            if (creds == null)
            {
                logger?.KWarning($"[KlothoConnection] Reconnect called with null credentials — failing gracefully");
                connection._completed = true;
                connection.Dispose();
                onFailed?.Invoke(new ReconnectFailedException(ReconnectRejectReason.Unknown));
                return connection;
            }

            connection._reconnectCreds = creds;
            connection._connectTimeoutMs = creds.ReconnectTimeoutMs;

            transport.OnConnected += connection.HandleConnected;
            transport.OnDataReceived += connection.HandleDataReceived;
            transport.OnDisconnected += connection.HandleDisconnected;

            connection._connectStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!transport.Connect(creds.RemoteAddress, creds.RemotePort))
            {
                connection._completed = true;
                connection.Dispose();
                onFailed?.Invoke(new ReconnectFailedException(ReconnectRejectReason.TransportStartFailed));
            }

            return connection;
        }

        /// <summary>
        /// Called every frame to process network events.
        /// </summary>
        public void Update()
        {
            if (_completed) return;

            _transport.PollEvents();

            // Connect timeout. For Normal/LateJoin: connectTimeoutMs (default DEFAULT_CONNECT_TIMEOUT_MS, 15s).
            // For cold-start Reconnect: creds.ReconnectTimeoutMs — the host slot lifetime.
            if (!_completed)
            {
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _connectStartMs;
                if (elapsed >= _connectTimeoutMs)
                {
                    _completed = true;
                    Dispose();
                    _logger?.KWarning($"[KlothoConnection] Connect timeout after {elapsed}ms (result={(_result != null ? "partial" : "null")}, pendingLateJoin={_pendingLateJoin != null}, pendingReconnect={_pendingReconnect != null}, pendingFullState={_pendingFullState != null})");
                    _onFailed?.Invoke(_reconnectCreds != null
                        ? (Exception)new ReconnectFailedException(ReconnectRejectReason.TimedOut)
                        : new JoinFailedException(JoinFailReason.TimedOut));
                }
            }
        }

        /// <summary>
        /// Cancels the connection and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            _transport.OnConnected -= HandleConnected;
            _transport.OnDataReceived -= HandleDataReceived;
            _transport.OnDisconnected -= HandleDisconnected;
        }

        private void HandleConnected()
        {
            // Inert once terminal — a completed/disposed connection must not re-send a join on a
            // later transport OnConnected (mirrors HandleDataReceived's guard).
            if (_completed) return;

            // cold-start Reconnect path: present persisted credentials directly.
            if (_reconnectCreds != null)
            {
                // SD (single/multi room): the server's RoomRouter requires RoomHandshakeMessage as the first
                // message and disconnects when it is missing. For P2P (creds.RoomId == -1), no handshake is sent —
                // same convention as Connect's preJoinMessage.
                if (_reconnectCreds.RoomId >= 0)
                {
                    var hs = new RoomHandshakeMessage { RoomId = _reconnectCreds.RoomId };
                    using (var serialized = _messageSerializer.SerializePooled(hs))
                    {
                        _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }

                var req = new ReconnectRequestMessage
                {
                    SessionMagic = _reconnectCreds.SessionMagic,
                    PlayerId = _reconnectCreds.LocalPlayerId,
                    DeviceId = _reconnectCreds.DeviceId ?? string.Empty,
                };
                _logger?.KInformation($"[KlothoConnection] Reconnect: sending ReconnectRequestMessage (playerId={req.PlayerId}, deviceId='{req.DeviceId}')");

                using (var serialized = _messageSerializer.SerializePooled(req))
                {
                    _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
                return;
            }

            // If there is a prefix message to send before PlayerJoinMessage (e.g. SD multi-room), send it first.
            // For example, when the server's RoomRouter expects RoomHandshakeMessage as the first message.
            if (_preJoinMessage != null)
            {
                using (var serialized = _messageSerializer.SerializePooled(_preJoinMessage))
                {
                    _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }

            // PlayerJoinMessage must arrive first so the host can determine the pending-peer role
            // (KlothoNetworkService.HandleDataReceived → triggers StartHandshake).
            // The host then initiates SyncRequest, and the guest only responds in HandleSyncRequest.
            var msg = new PlayerJoinMessage { DeviceId = _deviceId, Ticket = _ticket, ClaimedDisplayName = _claimedDisplayName };
            _logger?.KInformation($"[KlothoConnection] Connect: sending PlayerJoinMessage (deviceId='{msg.DeviceId}', hasTicket={!string.IsNullOrEmpty(msg.Ticket)}, claimedName='{msg.ClaimedDisplayName}')");
            
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandleDisconnected(DisconnectReason reason)
        {
            if (_completed) return;
            _completed = true;

            // Detach from the (shared) transport on every terminal path — a failed handshake must not
            // leave this connection subscribed, or it re-sends a join on the next OnConnected (e.g. an
            // in-match reconnect on the reused transport, which the host then mis-routes as a late-join).
            Dispose();

            if (reason == DisconnectReason.LocalDisconnect)
            {
                // User-initiated disconnect (e.g. cancel during handshake) — not a failure.
                return;
            }

            if (_reconnectCreds != null)
            {
                _onFailed?.Invoke(new ReconnectFailedException(
                    reason == DisconnectReason.NetworkFailure ? ReconnectRejectReason.NetworkFailure : ReconnectRejectReason.Unknown));
                return;
            }

            // Join path: a server reject reason rides on the disconnect packet payload (>= 0 = present).
            // Fall back to the transport reason when there is no payload.
            int payload = _transport.LastDisconnectPayload;
            JoinFailReason joinReason = payload >= 0
                ? JoinFailReasonExtensions.FromJoinReject((byte)payload)
                : JoinFailReasonExtensions.FromDisconnect(reason);
            _onFailed?.Invoke(new JoinFailedException(joinReason));
        }

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            if (_completed) return;

            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[KlothoConnection] Malformed payload from peerId={peerId}, length={length}");
                return;
            }

            switch (message)
            {
                case SyncRequestMessage syncReq:
                    HandleSyncRequest(syncReq);
                    break;
                case SyncCompleteMessage syncComplete:
                    HandleSyncComplete(syncComplete);
                    break;
                case SimulationConfigMessage simConfigMsg:
                    HandleSimulationConfig(simConfigMsg);
                    break;
                case LateJoinAcceptMessage lateJoinMsg:
                    HandleLateJoinAccept(lateJoinMsg);
                    break;
                case ReconnectAcceptMessage reconnectAccept:
                    HandleReconnectAccept(reconnectAccept);
                    break;
                case ReconnectRejectMessage reconnectReject:
                    HandleReconnectReject(reconnectReject);
                    break;
                case FullStateResponseMessage fullStateMsg:
                    HandleFullStateResponse(fullStateMsg);
                    break;
            }
        }

        // ── SyncRequest/Reply: the guest only responds with Reply to the host's SyncRequest ──
        // RTT/ClockOffset are authoritatively pushed down from the host via SyncCompleteMessage,
        // so the guest performs no separate sampling or calculation.

        private void HandleSyncRequest(SyncRequestMessage msg)
        {
            var reply = new SyncReplyMessage
            {
                Magic = msg.Magic,
                Sequence = msg.Sequence,
                Attempt = msg.Attempt,
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            using (var serialized = _messageSerializer.SerializePooled(reply))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandleSyncComplete(SyncCompleteMessage msg)
        {
            _result = new ConnectionResult
            {
                Transport = _transport,
                LocalPlayerId = msg.PlayerId,
                SessionMagic = msg.Magic,
                SharedEpoch = msg.SharedEpoch,
                ClockOffset = msg.ClockOffset,
                RecommendedExtraDelay = msg.RecommendedExtraDelay,
                // Forward the pre-game roster snapshot (incl. ReadyState + authoritative identity) so
                // InitializeFromConnection can build the player list.
                Roster = msg.Roster,
                // Forward per-player original tickets (index-parallel to Roster) so
                // InitializeFromConnection re-verifies each peer independently. Empty when the gate is off.
                RosterTickets = msg.RosterTickets,
                // Forward per-player server-verified entitlement bytes (SD normal join, index-parallel to
                // Roster) so InitializeFromConnection sets each player's entitlement. Empty on P2P.
                RosterEntitlementData = msg.RosterEntitlementData,
                RosterEntitlementLengths = msg.RosterEntitlementLengths,
            };
            _logger?.KInformation($"[KlothoConnection] SyncComplete: playerId={msg.PlayerId}, players={msg.Roster.Count}, waiting for SimulationConfig");
        }

        private void HandleSimulationConfig(SimulationConfigMessage msg)
        {
            _pendingSimConfig = msg;

            // Diagnostic: confirm the per-component maxCount overrides reached this client over the wire
            // (ServerDriven server push). The client's ComponentMemoryPeakSampling is not wire-propagated,
            // so its component-memory report is off by default — this log is the direct signal that the
            // reduced-slot config was received and will be applied to the client's layout.
            if (msg.MaxCountOverrideTypeIds != null && msg.MaxCountOverrideTypeIds.Count > 0)
                _logger?.KInformation(
                    $"[KlothoConnection] maxCount overrides received: {msg.MaxCountOverrideTypeIds.Count} entries — " +
                    $"typeIds=[{string.Join(",", msg.MaxCountOverrideTypeIds)}] values=[{string.Join(",", msg.MaxCountOverrideValues)}]");

            if (_result != null)
            {
                // Normal Join: SyncComplete already processed → augment _result immediately
                _result.SimulationConfig = msg.ToSimulationConfig();
            }
            else if (_pendingLateJoin != null)
            {
                // Late Join: LateJoinAccept arrived first, SimConfig arrived now → build result
                BuildLateJoinResult();
            }
            else if (_pendingReconnect != null)
            {
                // cold-start Reconnect: ReconnectAccept arrived first, SimConfig arrived now → build result
                BuildReconnectResult();
            }
            // else: corresponding Accept has not yet arrived → buffer only; BuildXxxResult is called when it arrives

            TryComplete();
        }

        private void HandleLateJoinAccept(LateJoinAcceptMessage msg)
        {
            _pendingLateJoin = msg;
            if (_pendingSimConfig != null)
                BuildLateJoinResult();
            TryComplete();
        }

        private void HandleReconnectAccept(ReconnectAcceptMessage msg)
        {
            _pendingReconnect = msg;
            if (_pendingSimConfig != null)
                BuildReconnectResult();
            TryComplete();
        }

        private void HandleReconnectReject(ReconnectRejectMessage msg)
        {
            if (_completed) return;
            _completed = true;
            Dispose();

            _logger?.KWarning($"[KlothoConnection] Reconnect rejected: reason={((ReconnectRejectReason)msg.Reason).ToName()}");
            _onFailed?.Invoke(new ReconnectFailedException((ReconnectRejectReason)msg.Reason));
        }

        private void HandleFullStateResponse(FullStateResponseMessage msg)
        {
            // FullStateResponseMessage.StateData is allocated as a fresh copy by the generator via ToArray(),
            // so MessageSerializer._messageCache singleton reuse has no impact (primitives + byte[] reference copy).
            _pendingFullState = (msg.Tick, msg.StateData, msg.StateHash);
            TryComplete();
        }

        private void BuildLateJoinResult()
        {
            _result = new ConnectionResult
            {
                Transport = _transport,
                LocalPlayerId = _pendingLateJoin.PlayerId,
                SessionMagic = _pendingLateJoin.Magic,
                SharedEpoch = _pendingLateJoin.SharedEpoch,
                ClockOffset = _pendingLateJoin.ClockOffset,
                SimulationConfig = _pendingSimConfig.ToSimulationConfig(),
                Kind = JoinKind.LateJoin,
                LateJoinPayload = new LateJoinPayload
                {
                    AcceptMessage = _pendingLateJoin,
                    // FullState is filled in from _pendingFullState at the TryComplete stage
                },
            };
        }

        private void BuildReconnectResult()
        {
            _result = new ConnectionResult
            {
                Transport = _transport,
                LocalPlayerId = _pendingReconnect.PlayerId,
                SessionMagic = _reconnectCreds.SessionMagic,
                SharedEpoch = _pendingReconnect.SharedEpoch,
                ClockOffset = _pendingReconnect.ClockOffset,
                SimulationConfig = _pendingSimConfig.ToSimulationConfig(),
                Kind = JoinKind.Reconnect,
                ReconnectPayload = new ReconnectPayload
                {
                    AcceptMessage = _pendingReconnect,
                    // FullState is filled in from _pendingFullState at the TryComplete stage
                },
            };
        }

        private void TryComplete()
        {
            if (_completed) return;
            if (_result == null) return;                // Waiting for SyncComplete or (LateJoinAccept + SimConfig) or (ReconnectAccept + SimConfig)
            if (_result.SimulationConfig == null) return;
            if (_pendingLateJoin != null && _pendingFullState == null) return; // Waiting for Late Join FullState
            if (_pendingReconnect != null && _pendingFullState == null) return; // Waiting for cold-start Reconnect FullState

            if (_pendingLateJoin != null)
            {
                _result.LateJoinPayload.FullStateTick = _pendingFullState.Value.tick;
                _result.LateJoinPayload.FullStateData = _pendingFullState.Value.data;
                _result.LateJoinPayload.FullStateHash = _pendingFullState.Value.hash;
            }
            else if (_pendingReconnect != null)
            {
                _result.ReconnectPayload.FullStateTick = _pendingFullState.Value.tick;
                _result.ReconnectPayload.FullStateData = _pendingFullState.Value.data;
                _result.ReconnectPayload.FullStateHash = _pendingFullState.Value.hash;
            }

            _completed = true;
            Dispose();

            switch (_result.Kind)
            {
                case JoinKind.LateJoin:
                    _logger?.KInformation($"[KlothoConnection] Late Join connected: playerId={_result.LocalPlayerId}, fullStateTick={_result.LateJoinPayload.FullStateTick}");
                    break;
                case JoinKind.Reconnect:
                    _logger?.KInformation($"[KlothoConnection] Cold-start Reconnect: playerId={_result.LocalPlayerId}, fullStateTick={_result.ReconnectPayload.FullStateTick}");
                    break;
                default:
                    _logger?.KInformation($"[KlothoConnection] Connected: playerId={_result.LocalPlayerId}");
                    break;
            }

            var sc = _result.SimulationConfig;
            _logger?.KInformation(
                $"[KlothoConnection] SimulationConfig: " +
                $"TickIntervalMs={sc.TickIntervalMs}, " +
                $"InputDelayTicks={sc.InputDelayTicks}, " +
                $"MaxRollbackTicks={sc.MaxRollbackTicks}, " +
                $"SyncCheckInterval={sc.SyncCheckInterval}, " +
                $"UsePrediction={sc.UsePrediction}, " +
                $"MaxEntities={sc.MaxEntities}, " +
                $"Mode={sc.Mode}, " +
                $"HardToleranceMs={sc.HardToleranceMs}, " +
                $"InputResendIntervalMs={sc.InputResendIntervalMs}, " +
                $"MaxUnackedInputs={sc.MaxUnackedInputs}, " +
                $"ServerSnapshotRetentionTicks={sc.ServerSnapshotRetentionTicks}, " +
                $"SDInputLeadTicks={sc.SDInputLeadTicks}, " +
                $"EnableErrorCorrection={sc.EnableErrorCorrection}, " +
                $"InterpolationDelayTicks={sc.InterpolationDelayTicks}, " +
                $"EventDispatchWarnMs={sc.EventDispatchWarnMs}, " +
                $"TickDriftWarnMultiplier={sc.TickDriftWarnMultiplier}");

            _onCompleted?.Invoke(_result);
        }
    }
}
