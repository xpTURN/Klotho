using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client-side IServerDrivenNetworkService implementation for ServerDriven mode.
    /// Handles server connection, handshake, GameStart, and receiving/firing server messages.
    /// SubscribeEngine(): subscribes to engine.OnCatchupComplete.
    /// </summary>
    public class ServerDrivenClientService : IServerDrivenNetworkService, IDesyncProbeNetwork
    {
        private IKLogger _logger;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

        private IKlothoEngine _engine;
        // Buffered extra-delay value when handshake handlers (Sync/LateJoin/Reconnect) fire before
        // SubscribeEngine is called. Applied on SubscribeEngine. Cleared after apply.
        private int? _pendingExtraDelayApply;
        private ExtraDelaySource _pendingExtraDelaySource;
        private ISimulationConfig _simConfig;
        private ISessionConfig _sessionConfig;
        private IReconnectCredentialsStore _reconnectCredentialsStore;
        private string _appVersion;
        private IDeviceIdProvider _deviceIdProvider;

        // Player management
        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();
        // Cached scratch for preserving DisplayName across the GameStart roster rebuild — one-shot per match, reused.
        private readonly List<string> _gameStartNameCache = new List<string>();
        // Parallel cache preserving Account across the GameStart roster rebuild.
        private readonly List<string> _gameStartAccountCache = new List<string>();

        // Session
        private long _sessionMagic;
        private SharedTimeClock _sharedClock;
        private int _roomId = -1;
        private SessionPhase _phase;
        private int _localPlayerId;
        private int _localTick;
        private int _randomSeed;
        private long _gameStartTime;

        // Input resend queue
        private readonly Queue<UnackedInput> _unackedInputs = new Queue<UnackedInput>();
        private long _lastResendTime;

        // Reconnect state
        private enum ReconnectState { None, WaitingForTransport, SendingRequest, WaitingForFullState, Failed }
        private ReconnectState _reconnectState;
        private long _reconnectStartTimeMs;
        private long _reconnectRequestSentTime;
        private int _reconnectRetryCount;
        private long _lastTransportReconnectTime;
        private const int RECONNECT_REQUEST_TIMEOUT_MS = 5000;
        private const int TRANSPORT_RECONNECT_INTERVAL_MS = 1000;

        // Message cache (GC avoidance)
        private readonly ClientInputMessage _clientInputCache = new ClientInputMessage();
        private readonly ClientInputBundleMessage _bundleCache = new ClientInputBundleMessage();
        private readonly PlayerReadyMessage _playerReadyCache = new PlayerReadyMessage();
        private readonly RoomHandshakeMessage _roomHandshakeCache = new RoomHandshakeMessage();
        private readonly PongMessage _pongMessageCache = new PongMessage();
        private readonly ReactiveExtraDelayReportMessage _reactiveExtraDelayCache = new ReactiveExtraDelayReportMessage();

        // ── IKlothoNetworkService properties ────────────────────

        public SessionPhase Phase
        {
            get => _phase;
            private set
            {
                var prev = _phase;
                _phase = value;
                _logger?.KInformation($"[SDClientService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");

                if (prev != value)
                    OnPhaseChanged?.Invoke(value);
            }
        }

        public SharedTimeClock SharedClock => _sharedClock;
        public int PlayerCount => _players.Count;
        public int SpectatorCount => 0;
        public int PendingLateJoinCatchupCount => 0;
        public bool AllPlayersReady => _players.TrueForAll(p => p.IsReady);
        public int LocalPlayerId => _localPlayerId;
        public bool IsHost => false;
        public int RandomSeed => _randomSeed;
        public IReadOnlyList<IPlayerInfo> Players => _players;

        // Capture-free equivalent of _players.Find(p => p.PlayerId == id) (no closure allocation).
        private PlayerInfo FindPlayerById(int playerId)
        {
            for (int i = 0; i < _players.Count; i++)
                if (_players[i].PlayerId == playerId)
                    return _players[i];
            return null;
        }

        // ── IServerDrivenNetworkService properties ────────────────

        public bool IsServer => false;

        // ── Events ──────────────────────────────────────────

        public event Action OnGameStart;
        public event Action<long> OnCountdownStarted;
        public event Action<IPlayerInfo> OnPlayerJoined;
        public event Action<IPlayerInfo> OnPlayerLeft;
        public event Action<ICommand> OnCommandReceived;
        public event Action<int, int, long, long> OnDesyncDetected;
        public event Action<int, int, bool> OnSyncHashCompared;
        public event Action<int, int> OnResyncFailureReported;
        public event Action<int> OnMatchAbortReceived;
        public event Action<int, int, int> OnFrameAdvantageReceived;
        public event Action<int> OnLocalPlayerIdAssigned;
        public event Action<int, int> OnFullStateRequested;
        public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
        public event Action<IPlayerInfo> OnPlayerDisconnected;
        public event Action<IPlayerInfo> OnPlayerReconnected;
        public event Action OnReconnecting;
        public event Action<ReconnectRejectReason> OnReconnectFailed;
        public event Action OnReconnected;
        public event Action<int, int> OnLateJoinPlayerAdded;

        // SD-only events
        public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
        public event Action<int> OnInputAckReceived;
        public event Action<int, byte[], long> OnServerFullStateReceived;
        public event Action<int, long> OnBootstrapBegin;
        public event Action<int, int, RejectionReason> OnCommandRejected;

        // State-change events (fired on transition only).
        public event Action<SessionPhase> OnPhaseChanged;
        public event Action<int> OnPlayerCountChanged;
        public event Action<bool> OnAllPlayersReadyChanged;

        private void RaisePlayerCountIfChanged(int prevCount)
        {
            int newCount = _players.Count;
            if (prevCount != newCount)
                OnPlayerCountChanged?.Invoke(newCount);
        }

        private void RaiseAllPlayersReadyIfChanged(bool prevValue)
        {
            bool newValue = AllPlayersReady;
            if (prevValue != newValue)
                OnAllPlayersReadyChanged?.Invoke(newValue);
        }

        // ── Initialization ─────────────────────────────────────────

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        /// <summary>
        /// Inject the cold-start Reconnect credentials store. Optional — when null, cold-start
        /// credentials are not persisted.
        /// </summary>
        public void SetReconnectCredentialsStore(IReconnectCredentialsStore store, string appVersion, IDeviceIdProvider deviceIdProvider = null)
        {
            _reconnectCredentialsStore = store;
            _appVersion = appVersion;
            _deviceIdProvider = deviceIdProvider;
        }

        private string GetDeviceId() => _deviceIdProvider?.GetDeviceId() ?? string.Empty;

        private void SaveReconnectCredentialsIfApplicable()
        {
            if (_reconnectCredentialsStore == null || _transport == null)
                return;

            var creds = new PersistedReconnectCredentials
            {
                RemoteAddress = _transport.RemoteAddress,
                RemotePort = _transport.RemotePort,
                SessionMagic = _sessionMagic,
                LocalPlayerId = _localPlayerId,
                SavedAtUnixMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                RoomName = null,    // SD path uses RoomId; RoomName retained for P2P symmetry
                RoomId = _roomId,   // SD multi-room routing identifier
                AppVersion = _appVersion,
                DeviceId = GetDeviceId(),
            };
            _reconnectCredentialsStore.Save(creds);
        }

        /// <summary>
        /// Guest-only initialization — takes over a handshake completed by KlothoConnection,
        /// skipping JoinRoom + handshake and starting from Synchronized.
        /// roomId: the room ID assigned to the client in a multi-room setup (default -1 = single room).
        ///   Used in the SendFirstMessage → SendRoomHandshake resend path on Reconnect.
        /// </summary>
        public void InitializeFromConnection(ConnectionResult result, ICommandFactory commandFactory, IKLogger logger, int roomId = -1)
        {
            _logger = logger;
            _transport = result.Transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            // Apply handshake result directly
            _localPlayerId = result.LocalPlayerId;
            _sessionMagic = result.SessionMagic;
            _roomId = roomId;
            _sharedClock = new SharedTimeClock(result.SharedEpoch, result.ClockOffset);

            // Build the lobby roster from the SyncComplete snapshot forwarded via ConnectionResult on a
            // normal join. This service has no shared RebuildPlayerList helper, so build it inline. IsReady
            // comes from each entry's ReadyState (default false in the lobby) rather than being hardcoded
            // true as on the LateJoin path.
            if (result.Kind == JoinKind.Normal && result.Roster != null && result.Roster.Count > 0)
            {
                int prevCount = _players.Count;
                bool prevReady = AllPlayersReady;
                _players.Clear();
                for (int i = 0; i < result.Roster.Count; i++)
                {
                    var e = result.Roster[i];
                    // Use the authority-propagated identity instead of locally fabricating a name.
                    _players.Add(new PlayerInfo
                    {
                        PlayerId = e.PlayerId,
                        DisplayName = e.DisplayName.ToString(),
                        Account = e.Account.ToString(),
                        ConnectionState = (PlayerConnectionState)e.ConnectionState,
                        IsReady = e.ReadyState != 0,
                    });
                }
                RaisePlayerCountIfChanged(prevCount);
                RaiseAllPlayersReadyIfChanged(prevReady);
                // Server-verified entitlement bytes (index-parallel to the roster just rebuilt) so
                // GetPlayerEntitlement returns non-null for tick-0 / initial-entry players (UI reads).
                ApplyRosterEntitlements(result.RosterEntitlementData, result.RosterEntitlementLengths, "NormalJoin");
            }

            Phase = SessionPhase.Synchronized;

            // KlothoConnection consumes the handshake messages before this service is wired up, so the
            // per-kind handlers below never run in the guest path. Forward the server-recommended
            // extra delay explicitly per JoinKind (buffered until SubscribeEngine).
            int seedExtraDelay;
            ExtraDelaySource seedSource;
            switch (result.Kind)
            {
                case JoinKind.LateJoin:
                    seedExtraDelay = result.LateJoinPayload.AcceptMessage.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.LateJoin;
                    break;
                case JoinKind.Reconnect:
                    seedExtraDelay = result.ReconnectPayload.AcceptMessage.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.Reconnect;
                    break;
                default:
                    seedExtraDelay = result.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.Sync;
                    break;
            }
            ApplyOrPendExtraDelay(seedExtraDelay, seedSource);

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        public void SubscribeEngine(IKlothoEngine engine)
        {
            _engine = engine;
            _simConfig = engine.SimulationConfig;
            _sessionConfig = engine.SessionConfig;
            // Drain any extra-delay value buffered before the engine was ready (Sync/LateJoin/Reconnect
            // handshake handlers fire before SubscribeEngine in the SD client setup sequence).
            if (_pendingExtraDelayApply.HasValue)
            {
                engine.ApplyExtraDelay(_pendingExtraDelayApply.Value, _pendingExtraDelaySource);
                _pendingExtraDelayApply = null;
            }
            engine.OnCatchupComplete += HandleCatchupComplete;
            engine.OnExtraDelayChanged += HandleEngineExtraDelayChanged;
        }

        // Report the client's effective extra-delay to the server so it folds
        // the locally-observed reactive correction (PastTick) into its authoritative baseline. Changes
        // are inherently sparse (escalate cooldown / decay dwell), so no extra rate-limit is needed;
        // dedupe on unchanged value. Server drops reports outside Phase==Playing.
        private int _lastReportedEffective = -1;
        private void HandleEngineExtraDelayChanged(int effective)
        {
            if (effective == _lastReportedEffective) return;
            _lastReportedEffective = effective;
            _reactiveExtraDelayCache.EffectiveExtraDelay = effective;
            SendToServer(_reactiveExtraDelayCache, DeliveryMethod.ReliableOrdered);
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"sd-client\",\"dir\":\"send\",\"effective\":{effective}}}");
        }

        // Applies the value immediately if the engine is ready, otherwise buffers it for SubscribeEngine.
        // Called from handshake handlers (Sync/LateJoin/Reconnect) where the engine may not yet exist.
        // Mid-match RecommendedExtraDelayUpdate handler does not use this — engine is guaranteed ready
        // when that message is received (server gates push on Phase == Playing).
        internal void ApplyOrPendExtraDelay(int delay, ExtraDelaySource source)
        {
            if (_engine != null)
            {
                _engine.ApplyExtraDelay(delay, source);
            }
            else
            {
                _pendingExtraDelayApply = delay;
                _pendingExtraDelaySource = source;
            }
        }

        /// <summary>
        /// Restores `_players` / `_sessionMagic` / `_randomSeed` from a LateJoinAcceptMessage received via KlothoConnection on the Late Join path.
        /// Must be called by KlothoSession.Create **before** `engine.Initialize`
        /// so that the `_activePlayerIds` copy loop inside Engine.Initialize is populated correctly.
        /// </summary>
        public void SeedLateJoinPlayers(LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            SeedPlayersFromCatchupPayload(msg.Magic, msg.RandomSeed, msg.Roster);
            ApplyRosterEntitlements(msg.RosterEntitlementData, msg.RosterEntitlementLengths, "LateJoinSeed");
        }

        /// <summary>
        /// Cold-start Reconnect counterpart of SeedLateJoinPlayers. The host echoes the existing
        /// PlayerId via ReconnectAcceptMessage.PlayerId rather than allocating a new one.
        /// _sessionMagic is restored from the persisted credentials (already set in InitializeFromConnection).
        /// </summary>
        public void SeedReconnectPlayers(ReconnectPayload payload)
        {
            var msg = payload.AcceptMessage;
            // SD has no Magic field on ReconnectAcceptMessage — _sessionMagic is already set by InitializeFromConnection
            // from creds. Pass current _sessionMagic to keep helper signature symmetric.
            SeedPlayersFromCatchupPayload(_sessionMagic, msg.RandomSeed, msg.Roster);
            ApplyRosterEntitlements(msg.RosterEntitlementData, msg.RosterEntitlementLengths, "ReconnectSeed");
        }

        // roster carries authoritative identity + ConnectionState. IsReady stays hardcoded true on this
        // LateJoin / cold-start Reconnect path (entry.ReadyState is meaningful only on the normal-join
        // path; this preserves the prior behavior).
        private void SeedPlayersFromCatchupPayload(long sessionMagic, int randomSeed, List<RosterEntry> roster)
        {
            _sessionMagic = sessionMagic;
            _randomSeed = randomSeed;
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                _players.Add(new PlayerInfo
                {
                    PlayerId = e.PlayerId,
                    IsReady = true,
                    ConnectionState = (PlayerConnectionState)e.ConnectionState,
                    DisplayName = e.DisplayName.ToString(),
                    Account = e.Account.ToString(),
                });
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            Phase = SessionPhase.Playing;   // Session is already Playing at Late Join / cold-start Reconnect time
            SaveReconnectCredentialsIfApplicable();
        }

        // ── Session management ───────────────────────────────────────

        public void CreateRoom(string roomName, int maxPlayers)
        {
            throw new NotSupportedException("The client cannot call CreateRoom.");
        }

        public void JoinRoom(string roomName)
        {
            _roomId = int.TryParse(roomName, out var id) ? id : -1;
            _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            Phase = SessionPhase.Lobby;
        }

        /// <summary>
        /// Connects to the server. Call after JoinRoom.
        /// </summary>
        public void Connect(string address, int port)
        {
            if (!_transport.Connect(address, port))
            {
                _logger?.KError($"[SDClientService] Failed to start client transport for {address}:{port}");
                Phase = SessionPhase.Disconnected;
            }
        }

        public void LeaveRoom(bool keepReconnectCredentials = false)
        {
            _transport.OnDataReceived -= HandleDataReceived;
            _transport.OnConnected -= HandleConnected;
            _transport.OnDisconnected -= HandleDisconnected;

            // Discard cold-start Reconnect credentials on graceful session end.
            // Process-shutdown paths pass keepReconnectCredentials=true so a relaunch can Reconnect.
            if (!keepReconnectCredentials)
                _reconnectCredentialsStore?.Clear();

            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            DrainUnackedInputs();
            _sessionMagic = 0;
            _gameStartTime = 0;
            _localPlayerId = 0;
            Phase = SessionPhase.Disconnected;
            _sharedClock = default;
        }

        public void SetReady(bool ready)
        {
            var msg = _playerReadyCache;
            msg.PlayerId = _localPlayerId;
            msg.IsReady = ready;
            SendToServer(msg, DeliveryMethod.Reliable);
            
            // Apply locally too — the server relays a ready change to other clients only (it excludes
            // the sender), so the local player's own Ready would otherwise never reflect.
            HandlePlayerReadyMessage(msg);
        }

        public void SendCommand(ICommand command)
        {
            // SD client: delegate to SendClientInput
            SendClientInput(command.Tick, command);
        }

        public void RequestCommandsForTick(int tick)
        {
            // Not needed in SD mode
        }

        public void SendSyncHash(int tick, long hash, long cmdHash)
        {
            // no-op: compared against server hash via local resimulation
        }

        public void InvalidateLocalSyncHashes(int fromTick)
        {
            // no-op: P2P-only sync-hash exchange
        }

        public void InvalidateSyncHashes(int fromTick)
        {
            // no-op: P2P-only sync-hash exchange
        }

        public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash)
        {
            // no-op: P2P-only recovery ladder reporting
        }

        public void BroadcastMatchAbort(byte reason)
        {
            // no-op: P2P-only recovery ladder reporting
        }

        public void SetLocalTick(int tick) { _localTick = tick; }

        // Server-driven client does not piggyback frame-advantage on CommandMessage — no-op.
        public void SetLocalAdvantage(int advantage) { }

        public void ClearOldData(int tick)
        {
            // no-op: _syncHashes unused in SD, resend queue cleaned up via InputAckMessage
        }

        public void SendPlayerConfig(int playerId, Core.PlayerConfigBase playerConfig)
        {
            int size = playerConfig.GetSerializedSize();
            byte[] configData = new byte[size];
            var writer = new Serialization.SpanWriter(configData);
            playerConfig.Serialize(ref writer);

            var msg = new PlayerConfigMessage
            {
                PlayerId = playerId,
                ConfigData = configData,
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
        }

        // ── SD-only methods ──────────────────────────────────

        public void SendClientInput(int tick, ICommand command)
        {
            // Serialize command
            int cmdSize = command.GetSerializedSize();
            byte[] cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = _clientInputCache;
            msg.Tick = tick;
            msg.PlayerId = _localPlayerId;
            msg.CommandData = cmdBuf;
            msg.CommandDataLength = cmdWriter.Position;
            msg._sourceBuffer = null;

            // Unreliable send
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }

            // Store in resend queue (returned to StreamPool when server ACK is received)
            int len = cmdWriter.Position;
            byte[] cmdCopy = StreamPool.GetBuffer(len);
            Buffer.BlockCopy(cmdBuf, 0, cmdCopy, 0, len);
            if (_unackedInputs.Count == 0)
                _lastResendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _unackedInputs.Enqueue(new UnackedInput { Tick = tick, CommandData = cmdCopy, Length = len });

            StreamPool.ReturnBuffer(cmdBuf);
        }

        // Reliable command submit — tick-less, reliably-ordered. No _unackedInputs queue: the transport
        // guarantees delivery, and the command tracker re-submits across a reconnect to cover in-flight
        // loss. The server assigns the execution tick on arrival (its reliable inbox in ServerInputCollector).
        public void SendReliableCommand(ICommand command)
        {
            int cmdSize = command.GetSerializedSize();
            byte[] cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = new ReliableCommandSubmitMessage
            {
                PlayerId = _localPlayerId,
                CommandData = cmdBuf,
                CommandDataLength = cmdWriter.Position,
                _sourceBuffer = null,
            };

            using (var serialized = _messageSerializer.SerializePooled(msg))
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            StreamPool.ReturnBuffer(cmdBuf);
        }

        public void SendFullStateRequest(int currentTick)
        {
            var msg = new FullStateRequestMessage { RequestTick = currentTick };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        // ── online desync probe ───────────────────────────────────
        // The client asks (the server is the only peer it compares against) and reports its conclusion.
        // It never serves: nobody probes a client.

#pragma warning disable CS0067 // interface-mandated (IDesyncProbeNetwork); an SD client neither serves probe requests nor receives verdicts — see doc
        public event Action<int, DesyncProbeRequestMessage> OnDesyncProbeRequested;
        public event Action<int, DesyncProbeResponseMessage> OnDesyncProbeResponse;
        public event Action<int, DesyncVerdictReportMessage> OnDesyncVerdictReported;
#pragma warning restore CS0067

        public void SendDesyncProbeRequest(int targetPeerId, DesyncProbeRequestMessage msg)
        {
            if (msg == null) return;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>No-op: a client is never the responder.</summary>
        public void SendDesyncProbeResponse(int targetPeerId, DesyncProbeResponseMessage msg) { }

        public void SendDesyncVerdict(DesyncVerdictReportMessage msg)
        {
            if (msg == null) return;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>Always the server (peer 0) — the only peer whose hashes this client ever sees.</summary>
        public bool TryResolveProbePeer(int playerId, out int peerId)
        {
            peerId = 0;
            return true;
        }

        public void SendBootstrapReady(int playerId)
        {
            var msg = new PlayerBootstrapReadyMessage { PlayerId = playerId };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
        {
            throw new NotSupportedException("The client cannot call SendFullStateResponse.");
        }

        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
        {
            throw new NotSupportedException("The client cannot call BroadcastFullState.");
        }

        public void ClearUnackedInputs()
        {
            DrainUnackedInputs();
        }

        public int GetMinClientAckedTick()
        {
            throw new NotSupportedException("The client cannot call GetMinClientAckedTick.");
        }

        // Server-propagated entitlement bytes (late-join notification / accept roster-parallel data).
        // Deterministic input for join-time seeding: when a PlayerJoinCommand replays in the verified
        // stream, OnPlayerJoinedWorld reads this and must decode the same bytes the server decoded —
        // otherwise the seeded state diverges from the authority hash. Tick-0 remains covered by the
        // Initial FullState (players joined before this client see no callback re-execution).
        public byte[] GetPlayerEntitlement(int playerId) => FindPlayerById(playerId)?.Entitlement;

        // Adopts server-propagated roster-parallel entitlement bytes (index-parallel to the roster
        // that just rebuilt _players; concat+lengths — the PlayerConfigData encoding). Malformed
        // input degrades to null entries (defensive; the server is trusted, so this is belt-and-braces).
        private void ApplyRosterEntitlements(byte[] data, List<int> lengths, string via)
        {
            if (lengths == null || lengths.Count == 0)
                return;
            int count = Math.Min(lengths.Count, _players.Count);
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                int len = lengths[i];
                if (len <= 0) continue;
                if (data == null || offset + len > data.Length) break;
                var bytes = new byte[len];
                Buffer.BlockCopy(data, offset, bytes, 0, len);
                _players[i].Entitlement = bytes;
                offset += len;
                _logger?.KInformation($"[SDClientService][Entitlement] loaded via {via}: playerId={_players[i].PlayerId}, bytes={len}");
            }
        }

        // ── Update ──────────────────────────────────────────

        public void Update()
        {
            _transport?.PollEvents();

            UpdateReconnect();
            ResendUnackedInputs();

            if (Phase == SessionPhase.Countdown && _sharedClock.IsValid && _sharedClock.SharedNow >= _gameStartTime)
            {
                Phase = SessionPhase.Playing;
                SaveReconnectCredentialsIfApplicable();
                OnGameStart?.Invoke();
            }
        }

        public void FlushSendQueue()
        {
            _transport?.FlushSendQueue();
        }

        // ── Message handling ─────────────────────────────────────

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[ServerDrivenClientService] Malformed payload from peerId={peerId}, length={length}");
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

                case GameStartMessage gameStart:
                    HandleGameStartMessage(gameStart);
                    break;

                case PlayerReadyMessage readyMsg:
                    HandlePlayerReadyMessage(readyMsg);
                    break;

                case VerifiedStateMessage verifiedMsg:
                    HandleVerifiedStateMessage(verifiedMsg);
                    break;

                case InputAckMessage ackMsg:
                    HandleInputAckMessage(ackMsg);
                    break;

                case LateJoinAcceptMessage lateJoinMsg:
                    HandleLateJoinAccept(lateJoinMsg);
                    break;

                case LateJoinNotificationMessage lateJoinNotification:
                    HandleLateJoinNotification(peerId, lateJoinNotification);
                    break;

                case PlayerJoinNotificationMessage joinNotification:
                    HandlePlayerJoinNotification(peerId, joinNotification);
                    break;

                case PlayerLeaveNotificationMessage leaveNotification:
                    HandlePlayerLeaveNotification(peerId, leaveNotification);
                    break;

                case PlayerStateNotificationMessage stateNotification:
                    HandlePlayerStateNotification(peerId, stateNotification);
                    break;

                case FullStateResponseMessage fullStateMsg:
                    HandleFullStateResponse(fullStateMsg);
                    break;

                case ReconnectAcceptMessage reconnectAccept:
                    HandleReconnectAccept(reconnectAccept);
                    break;

                case ReconnectRejectMessage reconnectReject:
                    HandleReconnectReject(reconnectReject);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(playerConfigMsg);
                    break;

                case BootstrapBeginMessage bootBegin:
                    OnBootstrapBegin?.Invoke(bootBegin.FirstTick, bootBegin.TickStartTimeMs);
                    break;

                case CommandRejectedMessage rejectMsg:
                    _logger?.KInformation($"[SDClientService] CommandRejected received: tick={rejectMsg.Tick}, cmdTypeId={rejectMsg.CommandTypeId}, reason={rejectMsg.ReasonEnum}");
                    OnCommandRejected?.Invoke(rejectMsg.Tick, rejectMsg.CommandTypeId, rejectMsg.ReasonEnum);
                    break;

                case DesyncProbeResponseMessage probeRes:
                    OnDesyncProbeResponse?.Invoke(peerId, probeRes);
                    break;

                case RecommendedExtraDelayUpdateMessage extraDelayMsg:
                    HandleRecommendedExtraDelayUpdate(extraDelayMsg);
                    break;

                case PingMessage pingMsg:
                    HandlePingMessage(peerId, pingMsg);
                    break;
            }
        }

        private void HandlePingMessage(int peerId, PingMessage msg)
        {
            var pong = _pongMessageCache;
            pong.Timestamp = msg.Timestamp;
            pong.Sequence = msg.Sequence;
            using (var serialized = _messageSerializer.SerializePooled(pong))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        private void HandlePlayerConfigMessage(PlayerConfigMessage msg)
        {
            // Deserialize ConfigData and store in the client engine
            var configMsg = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            if (configMsg != null)
            {
                (_engine as KlothoEngine)?.HandlePlayerConfigReceived(msg.PlayerId, configMsg);
            }
        }

        private void HandleSyncRequest(SyncRequestMessage msg)
        {
            if (_sessionMagic == 0)
                _sessionMagic = msg.Magic;
            else if (msg.Magic != _sessionMagic)
                return;

            var reply = new SyncReplyMessage
            {
                Magic = msg.Magic,
                Sequence = msg.Sequence,
                Attempt = msg.Attempt,
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            using (var serialized = _messageSerializer.SerializePooled(reply))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        private void HandleSyncComplete(SyncCompleteMessage msg)
        {
            if (msg.Magic != _sessionMagic)
                return;

            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Sync);
            Phase = SessionPhase.Synchronized;
            OnLocalPlayerIdAssigned?.Invoke(_localPlayerId);
        }

        private void HandleRecommendedExtraDelayUpdate(RecommendedExtraDelayUpdateMessage msg)
        {
            _engine?.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.DynamicPush);
            _logger?.KInformation(
                $"[SDClientService][DynamicDelay] Update applied: newDelay={msg.RecommendedExtraDelay}, avgRtt={msg.AvgRttMs}ms");
        }

        private void HandleGameStartMessage(GameStartMessage msg)
        {
            _logger?.KInformation($"[SDClientService] Game start: seed={msg.RandomSeed}, players={msg.PlayerIds.Count}");

            // Apply server-authoritative SessionConfig fields in place. Engine and NetworkService
            // share the same SessionConfig reference, so mutating the instance propagates to both
            // readers automatically. Match-start one-shot; SessionConfig stays immutable afterward.
            if (_sessionConfig is SessionConfig cfg)
            {
                cfg.RandomSeed = msg.RandomSeed;
                cfg.MaxPlayers = msg.MaxPlayers;
                cfg.MinPlayers = msg.MinPlayers;
                cfg.MaxSpectators = msg.MaxSpectators;
                cfg.AllowLateJoin = msg.AllowLateJoin;
                cfg.LateJoinDelayTicks = msg.LateJoinDelayTicks;
                cfg.ReconnectTimeoutMs = msg.ReconnectTimeoutMs;
                cfg.ReconnectMaxRetries = msg.ReconnectMaxRetries;
                cfg.LateJoinDelaySafety = msg.LateJoinDelaySafety;
                cfg.RttSanityMaxMs = msg.RttSanityMaxMs;
                cfg.MinStallAbortTicks = msg.MinStallAbortTicks;
                cfg.CountdownDurationMs = msg.CountdownDurationMs;
                cfg.AbortGraceMs = msg.AbortGraceMs;
                cfg.EndGracePolicy = (EndGracePolicy)msg.EndGracePolicy;
                cfg.EndGraceMs = msg.EndGraceMs;
                cfg.ClientShutdownGraceMs = msg.ClientShutdownGraceMs;
            }

            // Preserve each existing DisplayName by PlayerId across the Clear+rebuild, capturing names
            // before Clear. Without this the locally derived "Player{id}" names are wiped to null at GameStart.
            int prevPlayerCount0 = _players.Count;
            bool prevAllReady0 = AllPlayersReady;
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                var existing = FindPlayerById(msg.PlayerIds[i]);
                _gameStartNameCache.Add(existing?.DisplayName);
                _gameStartAccountCache.Add(existing?.Account);  // preserve Account too
            }
            _players.Clear();
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                _players.Add(new PlayerInfo
                {
                    PlayerId = msg.PlayerIds[i],
                    DisplayName = _gameStartNameCache[i] ?? string.Empty,
                    Account = _gameStartAccountCache[i] ?? string.Empty,
                    IsReady = true
                });
            }
            _gameStartNameCache.Clear();
            _gameStartAccountCache.Clear();
            RaisePlayerCountIfChanged(prevPlayerCount0);
            RaiseAllPlayersReadyIfChanged(prevAllReady0);

            _randomSeed = msg.RandomSeed;

            if (msg.StartTime > 0)
            {
                _gameStartTime = msg.StartTime;
                Phase = SessionPhase.Countdown;
                OnCountdownStarted?.Invoke(msg.StartTime);
            }
            else
            {
                // No countdown: arm the initial-FullState routing flag now so the upcoming broadcast is
                // routed through HandleInitialFullStateReceived (the bootstrap-ready ack site). The
                // countdown path achieves the same via HandleCountdownStarted.
                (_engine as KlothoEngine)?.MarkExpectingInitialFullState();
                Phase = SessionPhase.Playing;
                SaveReconnectCredentialsIfApplicable();
                OnGameStart?.Invoke();
            }
        }

        private void HandlePlayerReadyMessage(PlayerReadyMessage msg)
        {
            var player = FindPlayerById(msg.PlayerId);
            if (player != null)
            {
                bool prevReady = AllPlayersReady;
                player.IsReady = msg.IsReady;
                RaiseAllPlayersReadyIfChanged(prevReady);
            }
        }

        private void HandleVerifiedStateMessage(VerifiedStateMessage msg)
        {
            // Deserialize confirmed inputs
            var commands = _commandFactory.DeserializeCommands(msg.ConfirmedInputsSpan);

            // Fire event to engine
            OnVerifiedStateReceived?.Invoke(msg.Tick, commands, msg.StateHash);
        }

        private void HandleInputAckMessage(InputAckMessage msg)
        {
            // Remove ticks before ACK from the resend queue
            while (_unackedInputs.Count > 0 && _unackedInputs.Peek().Tick <= msg.AckedTick)
            {
                StreamPool.ReturnBuffer(_unackedInputs.Peek().CommandData);
                _unackedInputs.Dequeue();
            }

            OnInputAckReceived?.Invoke(msg.AckedTick);
        }

        private void HandleLateJoinAccept(LateJoinAcceptMessage msg)
        {
            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            _randomSeed = msg.RandomSeed;

            // Rebuild player list
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.Roster.Count; i++)
            {
                var e = msg.Roster[i];
                // Authoritative identity from the accept roster. IsReady hardcoded true on this LateJoin
                // path (entry.ReadyState meaningful only on normal join).
                _players.Add(new PlayerInfo
                {
                    PlayerId = e.PlayerId,
                    IsReady = true,
                    ConnectionState = (PlayerConnectionState)e.ConnectionState,
                    DisplayName = e.DisplayName.ToString(),
                    Account = e.Account.ToString(),
                });
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            ApplyRosterEntitlements(msg.RosterEntitlementData, msg.RosterEntitlementLengths, "LateJoinAccept");

            OnLocalPlayerIdAssigned?.Invoke(_localPlayerId);
            _engine?.ExpectFullState();
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.LateJoin);
            _logger?.KInformation(
                $"[SDClientService] Late join accepted: playerId={msg.PlayerId}, playerCount={msg.PlayerCount}");
        }

        /// <summary>
        /// Server-broadcast notification that a new player completed late-join handshake. Adds the
        /// player to _players + fires PlayerCount / AllPlayersReady / OnPlayerJoined surfaces so UI
        /// stays consistent with the server's roster.
        /// </summary>
        private void HandleLateJoinNotification(int peerId, LateJoinNotificationMessage msg)
        {
            // SD client only talks to the server (peerId 0). Drop messages forged from any other source.
            if (peerId != 0)
            {
                _logger?.KWarning($"[SDClientService][HandleLateJoinNotification] Ignored from non-server peerId={peerId}");
                return;
            }

            // Duplicate notification (e.g. reliable retry path race) must not double-add.
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[SDClientService][HandleLateJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            // Use the authority-propagated identity instead of locally fabricating a name.
            var newPlayer = new PlayerInfo
            {
                PlayerId = msg.PlayerId,
                DisplayName = msg.DisplayName ?? string.Empty,
                Account = msg.Account ?? string.Empty,
                IsReady = true,
                ConnectionState = PlayerConnectionState.Connected,
                // Server-verified entitlement — deterministic input for the join-time seed when this
                // player's PlayerJoinCommand replays at joinTick (must precede it: same ReliableOrdered
                // stream as the verified state, and joinTick is LateJoinDelayTicks in the future).
                Entitlement = msg.Entitlement,
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            if (msg.Entitlement != null && msg.Entitlement.Length > 0)
                _logger?.KInformation($"[SDClientService][Entitlement] loaded via LateJoinNotification: playerId={msg.PlayerId}, bytes={msg.Entitlement.Length}");

            OnPlayerJoined?.Invoke(newPlayer);

            _logger?.KInformation($"[SDClientService][HandleLateJoinNotification] Late join player added: playerId={msg.PlayerId}, joinTick={msg.JoinTick}");
        }

        // Receiver for a new player that completed the normal-join (lobby) handshake on the server. Adds
        // it to the player list so the roster stays consistent before StartGame. Only the server (peerId 0)
        // is a valid source, and a duplicate is a no-op.
        private void HandlePlayerJoinNotification(int peerId, PlayerJoinNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SDClientService][HandlePlayerJoinNotification] Ignored from non-server peerId={peerId}");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[SDClientService][HandlePlayerJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            // Use the authority-propagated identity instead of locally fabricating a name.
            var newPlayer = new PlayerInfo
            {
                PlayerId = msg.PlayerId,
                DisplayName = msg.DisplayName ?? string.Empty,
                Account = msg.Account ?? string.Empty,
                IsReady = msg.IsReady,
                ConnectionState = (PlayerConnectionState)msg.ConnectionState,
                // Server-verified entitlement so GetPlayerEntitlement returns non-null for this player (UI reads).
                Entitlement = msg.Entitlement,
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            if (msg.Entitlement != null && msg.Entitlement.Length > 0)
                _logger?.KInformation($"[SDClientService][Entitlement] loaded via JoinNotification: playerId={msg.PlayerId}, bytes={msg.Entitlement.Length}");

            OnPlayerJoined?.Invoke(newPlayer);
            _logger?.KInformation($"[SDClientService][HandlePlayerJoinNotification] Lobby player added: playerId={msg.PlayerId}");
        }

        // Receiver for a player that left during the lobby. Removes it from the player list. Only the
        // server (peerId 0) is a valid source, and an unknown PlayerId is a no-op.
        private void HandlePlayerLeaveNotification(int peerId, PlayerLeaveNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SDClientService][HandlePlayerLeaveNotification] Ignored from non-server peerId={peerId}");
                return;
            }

            var player = FindPlayerById(msg.PlayerId);
            if (player == null)
                return;

            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Remove(player);
            OnPlayerLeft?.Invoke(player);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            _logger?.KInformation($"[SDClientService][HandlePlayerLeaveNotification] Lobby player removed: playerId={msg.PlayerId}");
        }

        // Receiver for the server's PlayerStateNotificationMessage — live connection-state of remote players
        // (disconnect / reconnect / leave). Display-only: SD is server-authoritative (verified state drives
        // the engine), so this updates the _players surface for UI without touching the engine. Only the
        // server (peerId 0) is a valid source; an unknown PlayerId is a no-op.
        private void HandlePlayerStateNotification(int peerId, PlayerStateNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SDClientService][HandlePlayerStateNotification] Ignored from non-server peerId={peerId}");
                return;
            }

            var player = FindPlayerById(msg.PlayerId);
            switch ((PlayerStateChange)msg.State)
            {
                case PlayerStateChange.Disconnected:
                    if (player != null)
                    {
                        player.ConnectionState = PlayerConnectionState.Disconnected;
                        OnPlayerDisconnected?.Invoke(player);
                    }
                    break;

                case PlayerStateChange.Reconnected:
                    if (player != null)
                    {
                        player.ConnectionState = PlayerConnectionState.Connected;
                        OnPlayerReconnected?.Invoke(player);
                    }
                    break;

                case PlayerStateChange.Left:
                    if (player != null)
                    {
                        int prevPlayerCount = _players.Count;
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                        RaisePlayerCountIfChanged(prevPlayerCount);
                    }
                    break;
            }
            _logger?.KInformation($"[SDClientService][HandlePlayerStateNotification] playerId={msg.PlayerId}, state={(PlayerStateChange)msg.State}");
        }

        private void HandleFullStateResponse(FullStateResponseMessage msg)
        {
            if (_reconnectState == ReconnectState.WaitingForFullState)
            {
                _reconnectState = ReconnectState.None;
                // Restore Phase lost on a self-initiated disconnect (fault-injection's
                // DisconnectPeerCalled is not reconnect-eligible → Phase=Disconnected). The
                // natural NetworkFailure path keeps Phase==Playing, so the setter's change-guard
                // makes this a no-op there. Mirrors P2P HandleFullStateResponse.
                Phase = SessionPhase.Playing;
                OnReconnected?.Invoke();
            }

            OnServerFullStateReceived?.Invoke(msg.Tick, msg.StateData, msg.StateHash);

            // AFTER the apply, not before. The invoke above is a plain event, so it runs the whole
            // apply synchronously — including the OnFullStateApplied hook that rebuilds peer-local
            // derivatives (the navmesh) from the restored state. The sender's fingerprint was taken
            // from ITS post-rebake state, so comparing before the apply pits this peer's pre-apply
            // derivative against that and mismatches by construction on any state carrying runtime
            // rebakes. That false positive is not free: the mismatch report is one-shot
            // (_staticMismatchLogged re-arms only on a later MATCHING check), so it consumed the
            // report a real divergence in the same window would have needed.
            //
            // Ordered this way the check also becomes the detector for a derivative rebuild that
            // FAILED — the hook throwing leaves the navmesh behind the state it is now simulating,
            // and this is what says so.
            //
            // Residual: an apply the engine skips (retreat guard) or drops leaves the state
            // unchanged, so the comparison is against pre-apply derivatives just as it was before.
            // Unchanged behaviour for that case, and it is the narrow one.
            (_engine as KlothoEngine)?.CheckStaticGeometryFingerprint(msg.StaticFingerprint);
        }

        // ── Connection events ─────────────────────────────────────

        private void HandleConnected()
        {
            if (_reconnectState == ReconnectState.WaitingForTransport)
            {
                // Reconnect mode — send ReconnectRequest
                _logger?.KInformation($"[SDClientService] Reconnect mode: sending ReconnectRequest");
                _reconnectState = ReconnectState.SendingRequest;
                SendReconnectRequest();
                return;
            }

            Phase = SessionPhase.Syncing;
            var joinMsg = new PlayerJoinMessage { DeviceId = GetDeviceId() };
            _logger?.KInformation($"[SDClientService] Connected to server, sending PlayerJoinMessage (deviceId='{joinMsg.DeviceId}')");

            using (var serialized = _messageSerializer.SerializePooled(joinMsg))
            {
                SendFirstMessage(serialized.Data, serialized.Length);
            }
        }

        private void HandleDisconnected(DisconnectReason reason)
        {
            bool reconnectEligible = reason == DisconnectReason.NetworkFailure
                                  || reason == DisconnectReason.ReconnectRequested;

            if (Phase == SessionPhase.Playing && reconnectEligible)
            {
                // Disconnected during game by network failure → attempt reconnect
                _reconnectState = ReconnectState.WaitingForTransport;
                _reconnectStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _reconnectRetryCount = 0;
                OnReconnecting?.Invoke();
            }
            else
            {
                Phase = SessionPhase.Disconnected;
            }
        }

        // ── Reconnect ───────────────────────────────────────────

        /// <summary>
        /// Fault-injection only — forces in-session reconnect arming, mirroring the
        /// reconnect-eligible branch of <see cref="HandleDisconnected"/> but without its
        /// Phase==Playing / reason==NetworkFailure gate. The harness severs the client with
        /// DisconnectPeer (→ DisconnectPeerCalled, not reconnect-eligible), so the natural arm
        /// never fires; calling this drives the same self-reconnect state machine
        /// (<see cref="UpdateReconnect"/> → transport reconnect) the production NetworkFailure
        /// path uses. Sets the timer fields too, else UpdateReconnect's elapsed math would
        /// immediately time out. Unlike P2P, no PauseForReconnect — SD's natural reconnect path
        /// does not pause the engine.
        /// </summary>
        internal void ArmInSessionReconnectForFaultInjection()
        {
            _reconnectState = ReconnectState.WaitingForTransport;
            _reconnectStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _reconnectRetryCount = 0;
            OnReconnecting?.Invoke();
        }

        private void HandleReconnectAccept(ReconnectAcceptMessage msg)
        {
            if (_reconnectState != ReconnectState.SendingRequest)
                return;

            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);

            // Rebuild player list
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.Roster.Count; i++)
            {
                var e = msg.Roster[i];
                // Authoritative identity from the reconnect roster. IsReady stays default false
                // (entry.ReadyState meaningful only on normal join).
                _players.Add(new PlayerInfo
                {
                    PlayerId = e.PlayerId,
                    ConnectionState = (PlayerConnectionState)e.ConnectionState,
                    DisplayName = e.DisplayName.ToString(),
                    Account = e.Account.ToString(),
                });
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            ApplyRosterEntitlements(msg.RosterEntitlementData, msg.RosterEntitlementLengths, "ReconnectAccept");

            _reconnectState = ReconnectState.WaitingForFullState;
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Reconnect);
            _logger?.KInformation($"[SDClientService] Reconnect accepted: playerId={msg.PlayerId}");
        }

        private void HandleReconnectReject(ReconnectRejectMessage msg)
        {
            if (_reconnectState == ReconnectState.None)
                return;

            _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;

            // Any reject reason invalidates persisted credentials — discard.
            _reconnectCredentialsStore?.Clear();

            OnReconnectFailed?.Invoke((ReconnectRejectReason)msg.Reason);
        }

        private void UpdateReconnect()
        {
            if (_reconnectState == ReconnectState.None)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long elapsed = now - _reconnectStartTimeMs;

            if (_sessionConfig != null && elapsed > _sessionConfig.ReconnectTimeoutMs)
            {
                _reconnectState = ReconnectState.Failed;
                Phase = SessionPhase.Disconnected;
                OnReconnectFailed?.Invoke(ReconnectRejectReason.TimedOut);
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
                    else if (now - _lastTransportReconnectTime > TRANSPORT_RECONNECT_INTERVAL_MS)
                    {
                        _lastTransportReconnectTime = now;
                        if (!_transport.Connect(_transport.RemoteAddress, _transport.RemotePort))
                        {
                            _logger?.KError($"[SDClientService] Reconnect transport start failed — aborting reconnect");
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.TransportStartFailed);
                            return;
                        }
                    }
                    break;

                case ReconnectState.SendingRequest:
                    if (now - _reconnectRequestSentTime > RECONNECT_REQUEST_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_sessionConfig != null && _reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.MaxRetries);
                            return;
                        }
                        SendReconnectRequest();
                    }
                    break;

                case ReconnectState.WaitingForFullState:
                    // FullState is handled in HandleFullStateResponse → OnServerFullStateReceived
                    break;
            }
        }

        private void SendReconnectRequest()
        {
            var msg = new ReconnectRequestMessage
            {
                SessionMagic = _sessionMagic,
                PlayerId = _localPlayerId,
                DeviceId = GetDeviceId(),
            };
            _logger?.KInformation($"[SDClientService] Sending ReconnectRequestMessage (playerId={msg.PlayerId}, deviceId='{msg.DeviceId}')");

            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                SendFirstMessage(serialized.Data, serialized.Length);
            }
            _reconnectRequestSentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void SendFirstMessage(byte[] data, int length)
        {
            if (_roomId >= 0)
            {
                SendRoomHandshake();
            }
            _transport.Send(0, data, length, DeliveryMethod.ReliableOrdered);
        }

        private void SendRoomHandshake()
        {
            _roomHandshakeCache.RoomId = _roomId;
            using (var serialized = _messageSerializer.SerializePooled(_roomHandshakeCache))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Catchup complete ─────────────────────────────────────

        private void HandleCatchupComplete()
        {
            // Reset _lastServerVerifiedTick on Late Join catchup complete
            // Called from engine's OnCatchupComplete
            // No prefill needed since InputDelayTicks = 0
        }

        // ── Input resend (single-packet bundling) ──────────────

        private void ResendUnackedInputs()
        {
            if (_unackedInputs.Count == 0 || _simConfig == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastResendTime < _simConfig.InputResendIntervalMs)
                return;

            _lastResendTime = now;

            // Warn if MaxUnackedInputs exceeded
            if (_unackedInputs.Count > _simConfig.MaxUnackedInputs)
            {
                _logger?.KWarning(
                    $"[SDClientService] Unacked inputs {_unackedInputs.Count} (limit: {_simConfig.MaxUnackedInputs})");
            }

            // Bundle all unacked inputs into a single packet and resend
            var bundle = _bundleCache;
            bundle.PlayerId = _localPlayerId;
            bundle.Count = _unackedInputs.Count;
            bundle.EnsureCapacity(bundle.Count);

            int idx = 0;
            foreach (var input in _unackedInputs)
            {
                bundle.Entries[idx].Tick = input.Tick;
                bundle.Entries[idx].CommandData = input.CommandData;
                bundle.Entries[idx].CommandDataLength = input.Length;
                idx++;
            }

            using (var serialized = _messageSerializer.SerializePooled(bundle))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        // ── Helpers ───────────────────────────────────────────

        // Drain the unacked-input queue, returning each pooled command buffer to StreamPool.
        // Equivalent to _unackedInputs.Clear() plus buffer reclamation.
        private void DrainUnackedInputs()
        {
            while (_unackedInputs.Count > 0)
                StreamPool.ReturnBuffer(_unackedInputs.Dequeue().CommandData);
        }

        private void SendToServer(INetworkMessage message, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                _transport.Send(0, serialized.Data, serialized.Length, deliveryMethod);
            }
        }

        // ── Internal types ───────────────────────────────────────

        private struct UnackedInput
        {
            public int Tick;
            public byte[] CommandData;   // StreamPool-rented buffer (bucket size, Length != valid length)
            public int Length;           // valid data length
        }

        // ── Suppress unused event warnings ─────────────────────────

        internal void SuppressWarnings()
        {
            OnCountdownStarted?.Invoke(0);
            OnPlayerJoined?.Invoke(null);
            OnPlayerLeft?.Invoke(null);
            OnCommandReceived?.Invoke(null);
            OnDesyncDetected?.Invoke(0, 0, 0, 0);
            OnSyncHashCompared?.Invoke(0, 0, false);
            OnResyncFailureReported?.Invoke(0, 0);
            OnMatchAbortReceived?.Invoke(0);
            OnFrameAdvantageReceived?.Invoke(0, 0, 0);
            OnFullStateRequested?.Invoke(0, 0);
            OnFullStateReceived?.Invoke(0, null, 0, FullStateKind.Unicast);
            OnPlayerDisconnected?.Invoke(null);
            OnPlayerReconnected?.Invoke(null);
            OnReconnectFailed?.Invoke(ReconnectRejectReason.Unknown);
            OnReconnected?.Invoke();
            OnLateJoinPlayerAdded?.Invoke(0, 0);
        }
    }
}
