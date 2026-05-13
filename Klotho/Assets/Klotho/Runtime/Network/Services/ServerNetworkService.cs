using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Peer connection state (ServerDriven only).
    /// </summary>
    internal enum ServerPeerState
    {
        Handshaking,
        CatchingUp,
        Playing
    }

    /// <summary>
    /// Per-peer state info (ServerDriven only).
    /// </summary>
    internal class ServerPeerInfo
    {
        public int PeerId;
        public int PlayerId;
        public ServerPeerState State;
        public int LastAckedTick;
    }

    /// <summary>
    /// Server-side IServerDrivenNetworkService implementation for ServerDriven mode.
    /// Handles session management, handshake, GameStart, VerifiedState broadcast, and peer state tracking.
    /// SubscribeEngine(): no engine event subscriptions — the server does not need to inject catchup/spectator/disconnect inputs.
    /// </summary>
    public partial class ServerNetworkService : IServerDrivenNetworkService
    {
        private const int NUM_SYNC_PACKETS = 5;
        private const int SYNC_TIMEOUT_MS = 5000;
        private const int PING_INTERVAL_MS = 1000;
        private const int BOOTSTRAP_TIMEOUT_MS = 1000;
        private const int REJECT_TOKENS_PER_SEC = 10;
        private const int REJECT_BUCKET_CAPACITY = 10;

        private ILogger _logger;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

        private IKlothoEngine _engine;
        private ISimulationConfig _simConfig;
        private ISessionConfig _sessionConfig;
        private bool _minPlayersClampWarned;

        // Player management
        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();
        private readonly Dictionary<int, int> _peerToPlayer = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _peerDeviceIds = new Dictionary<int, string>();
        private int _nextPlayerId = 1; // No local player on server (playerId=0)

        // Raw bytes cache for forwarding existing player PlayerConfigs to late-join guests
        private readonly Dictionary<int, byte[]> _playerConfigBytes = new Dictionary<int, byte[]>();

        // Peer state (Handshaking → CatchingUp → Playing)
        private readonly Dictionary<int, ServerPeerInfo> _peerStates = new Dictionary<int, ServerPeerInfo>();

        // Handshake
        private readonly Dictionary<int, PeerSyncState> _peerSyncStates = new Dictionary<int, PeerSyncState>();
        private readonly HashSet<int> _pendingPeers = new HashSet<int>();

        // Spectators
        private readonly List<SpectatorInfo> _spectators = new List<SpectatorInfo>();

        // Input collection
        private ServerInputCollector _inputCollector;

        // Session
        private long _sessionMagic;
        private SharedTimeClock _sharedClock;
        private SessionPhase _phase;
        private int _localTick;
        private int _randomSeed;
        private long _lastPingTime;
        private int _pingSequence;
        private long _gameStartTime;

        // RTT metrics (match identification)
        public static bool RttMetricsEnabled = false;   // global runtime toggle: off → 0 emit / 0 GC for sample path
        private int _roomId = -1;         // -1 sentinel distinguishes missed SetRoomId from valid roomId=0
        private long _matchId;
        private long _playingStartMs;
        private readonly Dictionary<int, MatchRttAccumulator> _matchRttAcc = new Dictionary<int, MatchRttAccumulator>();

        // Per-player short-window RTT smoother. Populated regardless of RttMetricsEnabled —
        // intended consumer is the push-decision path
        // 5-sample sliding median (≈5s window at PING_INTERVAL_MS=1000) rejects single-spike outliers.
        private readonly Dictionary<int, PlayerRttSmoother> _rttSmoothers = new Dictionary<int, PlayerRttSmoother>();

        // Dynamic InputDelay push state. Keyed by peerId — reset on disconnect.
        // Seed entries written at CompletePeerSync to avoid redundant first push.
        private readonly Dictionary<int, int> _lastPushedExtraDelay = new Dictionary<int, int>();
        private readonly Dictionary<int, long> _lastPushTimeMs = new Dictionary<int, long>();
        private const int EXTRA_DELAY_PUSH_THRESHOLD_UP = 2;       // ticks — fast UP response (storm prevention)
        private const int EXTRA_DELAY_PUSH_THRESHOLD_DOWN = 4;     // ticks — conservative DOWN (oscillation buffer)
        private const long MIN_PUSH_INTERVAL_MS = 500;             // per-peer push frequency cap

        // PastTick burst tracker — emits [Metrics][BurstDuration] when reject silence > threshold,
        // or on disconnect / Phase transition for final flush.
        private readonly Dictionary<int, PastTickBurstState> _pastTickBursts = new Dictionary<int, PastTickBurstState>();
        private const long BURST_SILENCE_THRESHOLD_MS = 1000;

        // Reconnect support
        private DisconnectedPlayerInfo[] _disconnectedPlayerPool;
        private int _disconnectedPlayerCount;
        private int _maxPlayersPerRoom;
        private int _maxSpectatorsPerRoom;

        // Phase-branched player count accounting
        private bool _gameStarted;
        private int _assignedPlayerIdCount;

        // Bootstrap window (SD-server, post Phase=Playing). Tracks per-player ack reception so
        // the server can defer first tick until all clients have applied Initial FullState.
        private readonly HashSet<int> _bootstrapAckedPlayers = new HashSet<int>();
        private long _bootstrapWindowOpenedTimeMs;
        private bool _bootstrapTimedOut;

        // Per-peer token bucket throttling outgoing CommandRejected hints.
        private struct RejectTokenState { public int Tokens; public long LastRefillMs; }
        private readonly Dictionary<int, RejectTokenState> _rejectTokens = new Dictionary<int, RejectTokenState>();

        // Message cache (GC avoidance)
        private readonly PingMessage _pingMessageCache = new PingMessage();
        private readonly PongMessage _pongMessageCache = new PongMessage();
        private readonly VerifiedStateMessage _verifiedStateCache = new VerifiedStateMessage();
        private readonly InputAckMessage _inputAckCache = new InputAckMessage();

        // Cached serialized bytes of the last VerifiedState (for resend when promoting CatchingUp → Playing)
        private byte[] _lastVerifiedBytes;
        private int _lastVerifiedBytesLength;
        private int _lastVerifiedTick;

        // ── Smoothed RTT (push-decision path consumer) ────────────────────

        // Returns false until MIN_SAMPLES (=3) pongs observed for this player.
        // Returns median of up to BUFFER_SIZE (=5) most recent samples — single-spike resistant.
        internal bool TryGetSmoothedRtt(int playerId, out int rttMs)
        {
            if (_rttSmoothers.TryGetValue(playerId, out var smoother))
                return smoother.TryGetSmoothedRtt(out rttMs);
            rttMs = 0;
            return false;
        }

        // ── Draining check (referenced by Room) ────────────────────

        public int PeerToPlayerCount => _peerToPlayer.Count;
        public int PendingPeerCount => _pendingPeers.Count;
        public int PeerSyncStateCount => _peerSyncStates.Count;
        public int DisconnectedPlayerCount => _disconnectedPlayerCount;
        public int MaxPlayersPerRoom => _maxPlayersPerRoom;
        public int MaxPlayerCapacity => _maxPlayersPerRoom;
        public int MaxSpectatorsPerRoom
        {
            get => _maxSpectatorsPerRoom;
            set => _maxSpectatorsPerRoom = value;
        }

        // ── IKlothoNetworkService properties ────────────────────

        public SessionPhase Phase
        {
            get => _phase;
            private set
            {
                var prevPhase = _phase;

                // Snapshot input collector counters per phase (monitoring).
                if (_inputCollector != null && prevPhase != value)
                {
                    _inputCollector.GetAndResetStats(out int accepted, out int pastTick, out int peerMismatch, out int tolerance);
                    if (accepted > 0 || pastTick > 0 || peerMismatch > 0 || tolerance > 0)
                    {
                        _logger?.ZLogInformation($"[InputCollector] Phase {prevPhase} stats: accepted={accepted}, rejectedPastTick={pastTick}, rejectedPeerMismatch={peerMismatch}, rejectedToleranceExceeded={tolerance}");
                    }
                }

                _phase = value;
                // Lobby branch is defensively retained but unreachable in current code paths
                // (HandlePeerDisconnected guards Phase != Playing; SNS instances are fresh per Room).
                if (prevPhase == SessionPhase.Playing &&
                    (value == SessionPhase.Disconnected || value == SessionPhase.Lobby))
                {
                    foreach (var kvp in _matchRttAcc)
                        EmitRttMatchAggregate(kvp.Value);
                    _matchRttAcc.Clear();
                    _rttSmoothers.Clear();
                    _lastPushedExtraDelay.Clear();
                    _lastPushTimeMs.Clear();
                    // Final flush of any in-progress PastTick bursts before clearing.
                    foreach (var kvp in _pastTickBursts)
                        EmitBurstDuration(kvp.Key, kvp.Value);
                    _pastTickBursts.Clear();
                }
                if (value == SessionPhase.Disconnected || value == SessionPhase.Lobby)
                {
                    // Disconnected = teardown signal, Lobby = fresh session start.
                    // Resets here cover the Countdown-abort fallback path where
                    // Phase = Lobby would otherwise leave _gameStarted=true with a stale snapshot.
                    _gameStarted = false;
                    _assignedPlayerIdCount = 0;
                    _nextPlayerId = 1;

                    // Bootstrap window state — primary clear point against single-room reuse leak.
                    _bootstrapAckedPlayers.Clear();
                    _bootstrapTimedOut = false;
                    _inputCollector?.SetBootstrapPending(false);
                }
                else if (value == SessionPhase.Playing && prevPhase != SessionPhase.Playing)
                {
                    // Open the bootstrap ack window before the engine broadcasts Initial FullState
                    // so early-arriving acks aren't dropped. Defensive Clear() — primary site has already cleared.
                    _bootstrapAckedPlayers.Clear();
                    _bootstrapTimedOut = false;
                    _bootstrapWindowOpenedTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _inputCollector?.SetBootstrapPending(true);
                    _playingStartMs = _bootstrapWindowOpenedTimeMs;
                    _matchId = _playingStartMs;
                }
                _logger?.ZLogInformation($"[ServerNetworkService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");
            }
        }

        public SharedTimeClock SharedClock => _sharedClock;
        public int PlayerCount => _players.Count;
        public int SpectatorCount => _spectators.Count;
        public int PendingLateJoinCatchupCount => 0; // SD server has no LateJoin catchup queue (uses different path)
        public bool AllPlayersReady => _players.TrueForAll(p => p.IsReady);
        public int LocalPlayerId => -1; // No local player on the server
        public bool IsHost => true;
        public int RandomSeed => _randomSeed;
        public IReadOnlyList<IPlayerInfo> Players => _players;

        // ── IServerDrivenNetworkService properties ────────────────

        public bool IsServer => true;

        // ── Events ──────────────────────────────────────────

        public event Action OnGameStart;
        public event Action<long> OnCountdownStarted;
        public event Action<IPlayerInfo> OnPlayerJoined;
        public event Action<IPlayerInfo> OnPlayerLeft;
        public event Action<ICommand> OnCommandReceived;
        public event Action<int, int, long, long> OnDesyncDetected;
        public event Action<int, int> OnFrameAdvantageReceived;
        public event Action<int> OnLocalPlayerIdAssigned;
        public event Action<int, int> OnFullStateRequested;
        public event Action<int, byte[], long> OnFullStateReceived;
        public event Action<IPlayerInfo> OnPlayerDisconnected;
        public event Action<IPlayerInfo> OnPlayerReconnected;
        public event Action OnReconnecting;
        public event Action<string> OnReconnectFailed;
        public event Action OnReconnected;
        public event Action<int, int> OnLateJoinPlayerAdded;

        // SD-only events (not fired on the server — client-only)
        public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
        public event Action<int> OnInputAckReceived;
        public event Action<int, byte[], long> OnServerFullStateReceived;
        public event Action<int, long> OnBootstrapBegin;
        public event Action<int, int, RejectionReason> OnCommandRejected;

        // ── Input collector access (used by engine) ────────────────────

        public ServerInputCollector InputCollector => _inputCollector;

        /// <summary>
        /// peerId → playerId mapping (read-only reference).
        /// </summary>
        public IReadOnlyDictionary<int, int> PeerToPlayerMap => _peerToPlayer;

        // ── Initialization ─────────────────────────────────────────

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, ILogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            _inputCollector = new ServerInputCollector();
            _inputCollector.Configure(0, _peerToPlayer);
            _inputCollector.SetLogger(logger);

            // Layer 1: transport-level rejects originate here — peerId is already in scope, no lookup needed.
            _inputCollector.OnCommandRejected += HandleInputCollectorRejected;

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
        }

        private void HandleInputCollectorRejected(int peerId, int tick, int cmdTypeId, RejectionReason reason)
        {
            TryUnicastReject(peerId, tick, cmdTypeId, reason);
            if (reason == RejectionReason.PastTick)
                TrackPastTickBurst(peerId);
        }

        private void TrackPastTickBurst(int peerId)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_pastTickBursts.TryGetValue(peerId, out var burst))
            {
                if (nowMs - burst.LastRejectMs > BURST_SILENCE_THRESHOLD_MS)
                {
                    EmitBurstDuration(peerId, burst);
                    burst.Reset(nowMs);
                }
                else
                {
                    burst.LastRejectMs = nowMs;
                    burst.RejectCount++;
                }
            }
            else
            {
                _pastTickBursts[peerId] = new PastTickBurstState
                {
                    FirstRejectMs = nowMs,
                    LastRejectMs = nowMs,
                    RejectCount = 1,
                };
            }
        }

        private void EmitBurstDuration(int peerId, PastTickBurstState burst)
        {
            long durationMs = burst.LastRejectMs - burst.FirstRejectMs;
            _logger?.ZLogInformation(
                $"[Metrics][BurstDuration] {{\"peerId\":{peerId},\"firstRejectMs\":{burst.FirstRejectMs},\"lastRejectMs\":{burst.LastRejectMs},\"durationMs\":{durationMs},\"rejectCount\":{burst.RejectCount}}}");
        }

        public void SubscribeEngine(IKlothoEngine engine)
        {
            _engine = engine;
            _simConfig = engine.SimulationConfig;
            _sessionConfig = engine.SessionConfig;

            // Layer 2: game-layer rejects flow through engine's generic OnSyncedEvent. Server keeps the
            // typecheck local so the engine itself stays agnostic of game-layer SimulationEvent types.
            _engine.OnSyncedEvent += HandleEngineSyncedEvent;

            // Set initial Hard Tolerance. RTT is unknown, so use 60ms (assuming RTT/2) + 20ms (jitter margin).
            // tickBase = (leadTicks + delayTicks + 1) × T — symmetric with the CompletePeerSync formula.
            // If HardToleranceMs == 0 (auto), it is recalculated on the first handshake completion (CompletePeerSync)
            // as (leadTicks + delayTicks + 1) × TickIntervalMs + avgRtt/2 + 20ms.
            int tolerance = _simConfig.HardToleranceMs;
            if (tolerance == 0)
            {
                int leadTicks  = _simConfig.GetEffectiveSDInputLeadTicks();
                int delayTicks = _simConfig.InputDelayTicks;
                int tickBase   = _simConfig.TickIntervalMs * (leadTicks + delayTicks + 1);
                tolerance = tickBase + 60 + 20;   // RTT unknown → conservative estimate (60ms = assumed RTT/2, 20ms = jitter margin)
            }
            _inputCollector.Configure(tolerance, _peerToPlayer);
        }

        // ── Session management ───────────────────────────────────────

        public void CreateRoom(string roomName, int maxPlayers)
        {
            _maxPlayersPerRoom = maxPlayers;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sessionMagic = SessionMagicFactory.Generate();
            _sharedClock = new SharedTimeClock(now, 0);

            // Explicit reset (the Phase setter also handles Lobby — kept as defensive redundancy).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Lobby;

            InitDisconnectedPlayerPool(maxPlayers);
        }

        public void SetRoomId(int roomId) => _roomId = roomId;

        /// <summary>
        /// Starts server listening. Call after CreateRoom.
        /// Returns true on socket bind success, false on immediate failure.
        /// </summary>
        public bool Listen(string address, int port, int maxPlayers)
        {
            return _transport.Listen(address, port, maxPlayers);
        }

        public void JoinRoom(string roomName)
        {
            throw new NotSupportedException("The server cannot call JoinRoom.");
        }

        public void LeaveRoom()
        {
            _transport.OnDataReceived -= HandleDataReceived;
            _transport.OnPeerConnected -= HandlePeerConnected;
            _transport.OnPeerDisconnected -= HandlePeerDisconnected;

            _players.Clear();
            _peerToPlayer.Clear();
            _peerDeviceIds.Clear();
            _peerStates.Clear();
            _peerSyncStates.Clear();
            _pendingPeers.Clear();
            _spectators.Clear();
            _playerConfigBytes.Clear();
            _rejectTokens.Clear();
            _inputCollector.Reset();
            _sessionMagic = 0;
            _gameStartTime = 0;
            _disconnectedPlayerCount = 0;

            // Explicit reset (the Phase setter also handles Disconnected — kept as defensive redundancy).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Disconnected;
            _sharedClock = default;
        }

        public void SetReady(bool ready)
        {
            // No local player on server — no-op
        }

        public void SendCommand(ICommand command)
        {
            // No local input on server — no-op
        }

        public void RequestCommandsForTick(int tick)
        {
            // Not needed in SD mode
        }

        public void SendSyncHash(int tick, long hash)
        {
            // The server is the source of truth for hashes — no-op
        }

        public void SetLocalTick(int tick) { _localTick = tick; }

        public void ClearOldData(int tick)
        {
            // Server-side cleanup: InputCollector cleanup
            _inputCollector.CleanupBefore(tick);
        }

        public void SendPlayerConfig(int playerId, Core.PlayerConfigBase playerConfig)
        {
            // Server side: store directly in engine (no network send needed — server is the host)
            (_engine as KlothoEngine)?.HandlePlayerConfigReceived(playerId, playerConfig);

            // Cache as raw bytes for forwarding to late-join guests (includes the host's own config)
            int size = playerConfig.GetSerializedSize();
            byte[] configData = new byte[size];
            var writer = new SpanWriter(configData);
            playerConfig.Serialize(ref writer);
            _playerConfigBytes[playerId] = configData;
        }

        // ── SD-only methods ──────────────────────────────────

        public void SendClientInput(int tick, ICommand command)
        {
            throw new NotSupportedException("The server cannot call SendClientInput.");
        }

        public void SendBootstrapReady(int playerId)
        {
            throw new NotSupportedException("The server cannot call SendBootstrapReady.");
        }

        public void SendFullStateRequest(int currentTick)
        {
            throw new NotSupportedException("The server cannot call SendFullStateRequest.");
        }

        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
        {
            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateData = stateData,
                StateHash = stateHash
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// Broadcasts FullState to all remote Playing clients (and spectators).
        /// Used for the initial FullState send at session start (reuses the _peerStates iteration pattern from BroadcastVerifiedState).
        /// The server itself is not in _peerStates and is naturally excluded.
        /// </summary>
        public void BroadcastFullState(int tick, byte[] stateData, long stateHash)
        {
            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateData = stateData,
                StateHash = stateHash
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerStates)
                {
                    if (kvp.Value.State == ServerPeerState.Playing)
                    {
                        _transport.Send(kvp.Value.PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }

                for (int i = 0; i < _spectators.Count; i++)
                {
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        public void ClearUnackedInputs()
        {
            // No resend queue on the server — no-op
        }

        public int GetMinClientAckedTick()
        {
            int minTick = int.MaxValue;
            foreach (var kvp in _peerStates)
            {
                if (kvp.Value.State == ServerPeerState.Playing && kvp.Value.LastAckedTick < minTick)
                    minTick = kvp.Value.LastAckedTick;
            }
            return minTick == int.MaxValue ? 0 : minTick;
        }

        // ── Update ──────────────────────────────────────────

        public void Update()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Handshake timeout check
            foreach (var kvp in _peerSyncStates)
            {
                var state = kvp.Value;
                if (!state.Completed && now - state.LastSyncSentTime > SYNC_TIMEOUT_MS)
                {
                    state.Attempt++;
                    SendSyncRequest(kvp.Key, state);
                }
            }

            // Reconnect timeout check
            CheckDisconnectedPlayerTimeout();

            // Bootstrap ack-window timeout (SD server only).
            CheckBootstrapTimeout(now);

            // Countdown expiry check
            if (Phase == SessionPhase.Countdown && _sharedClock.IsValid && _sharedClock.SharedNow >= _gameStartTime)
            {
                Phase = SessionPhase.Playing;
                OnGameStart?.Invoke();
            }

            // Periodic ping (after game start)
            if (Phase == SessionPhase.Playing && now - _lastPingTime >= PING_INTERVAL_MS)
            {
                _lastPingTime = now;
                _pingSequence++;
                var ping = _pingMessageCache;
                ping.Timestamp = now;
                ping.Sequence = _pingSequence;
                using (var serialized = _messageSerializer.SerializePooled(ping))
                {
                    foreach (var kvp in _peerToPlayer)
                    {
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
                    }
                }
            }
        }

        public void FlushSendQueue()
        {
            _transport?.FlushSendQueue();
        }

        // ── VerifiedState broadcast (called by engine) ────────────

        /// <summary>
        /// Broadcasts the verified tick state to all clients in the Playing state.
        /// Handshaking/CatchingUp peers are excluded (14.1 A-enforcement).
        /// </summary>
        public void BroadcastVerifiedState(int tick, List<ICommand> commands, long stateHash)
        {
            int dataSize = _commandFactory.GetSerializedCommandsSize(commands);
            byte[] buf = StreamPool.GetBuffer(dataSize);
            int written = _commandFactory.SerializeCommandsTo(buf.AsSpan(0, buf.Length));

            var msg = _verifiedStateCache;
            msg.Tick = tick;
            msg.StateHash = stateHash;
            msg.ConfirmedInputsData = buf;
            msg.ConfirmedInputsDataLength = written;
            msg._sourceBuffer = null;

            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                // Cache serialized bytes (grow-only, no GC)
                _lastVerifiedTick = tick;
                if (_lastVerifiedBytes == null || _lastVerifiedBytes.Length < serialized.Length)
                    _lastVerifiedBytes = new byte[serialized.Length];
                Buffer.BlockCopy(serialized.Data, 0, _lastVerifiedBytes, 0, serialized.Length);
                _lastVerifiedBytesLength = serialized.Length;

                foreach (var kvp in _peerStates)
                {
                    if (kvp.Value.State == ServerPeerState.Playing)
                    {
                        _transport.Send(kvp.Value.PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }

                // Also send to spectators
                for (int i = 0; i < _spectators.Count; i++)
                {
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    _spectators[i].LastSentTick = tick;
                }
            }

            StreamPool.ReturnBuffer(buf);
        }

        /// <summary>
        /// Sends an input acknowledgement to a specific client.
        /// </summary>
        public void SendInputAck(int peerId, int ackedTick)
        {
            var msg = _inputAckCache;
            msg.AckedTick = ackedTick;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        // ── Game start ───────────────────────────────────────

        /// <summary>
        /// Starts the game on the server. Sends GameStartMessage to all clients.
        /// If CountdownDurationMs > 0, transitions through the countdown phase before Playing.
        /// </summary>
        public void StartGame()
        {
            // Duplicate-call guard. Re-entry would re-snapshot and disrupt
            // any LateJoin already absorbed past the first call.
            if (_gameStarted)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] StartGame called twice — ignoring (snapshot already done)");
                return;
            }

            // GameStart snapshot — must run before Phase change so the EffectivePlayerCount
            // post-branch sees consistent state from the moment _gameStarted flips.
            _assignedPlayerIdCount = _players.Count;
            int maxId = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId > maxId) maxId = _players[i].PlayerId;
            }
            _nextPlayerId = maxId + 1;
            _gameStarted = true;

            _randomSeed = Environment.TickCount;

            bool useCountdown = _sessionConfig.CountdownDurationMs > 0;

            if (useCountdown)
                _gameStartTime = _sharedClock.SharedNow + _sessionConfig.CountdownDurationMs;

            var msg = new GameStartMessage
            {
                StartTime = useCountdown ? _gameStartTime : 0,
                RandomSeed = _randomSeed,
                MaxPlayers = _players.Count,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ResyncMaxRetries = _sessionConfig.ResyncMaxRetries,
                DesyncThresholdForResync = _sessionConfig.DesyncThresholdForResync,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                CatchupMaxTicksPerFrame = _sessionConfig.CatchupMaxTicksPerFrame,
                MinPlayers = _sessionConfig.MinPlayers,
            };

            foreach (var player in _players)
            {
                msg.PlayerIds.Add(player.PlayerId);
                _inputCollector.AddPlayer(player.PlayerId);
            }

            // Send to all peers
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }

                // Also send to waiting spectators
                for (int i = 0; i < _spectators.Count; i++)
                {
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }

            // Transition all peers to Playing state
            foreach (var kvp in _peerStates)
                kvp.Value.State = ServerPeerState.Playing;

            if (useCountdown)
            {
                Phase = SessionPhase.Countdown;
                OnCountdownStarted?.Invoke(_gameStartTime);
            }
            else
            {
                Phase = SessionPhase.Playing;
                OnGameStart?.Invoke();
            }
        }

        // ── Message handling ─────────────────────────────────────

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            if (_pendingPeers.Contains(peerId))
            {
                var firstMsg = _messageSerializer.Deserialize(data, length);
                _pendingPeers.Remove(peerId);
                if (firstMsg is PlayerJoinMessage playerJoin)
                {
                    _peerDeviceIds[peerId] = playerJoin.DeviceId ?? string.Empty;
                    // Outer capacity gate covering both the Pre-GameStart Lobby race and Post-GameStart capacity.
                    // HandleLateJoin's own gate is the second-line defense for non-dispatch callers.
                    if (EffectivePlayerCount >= MaxPlayerCapacity)
                    {
                        _logger?.ZLogWarning($"[ServerNetworkService][HandleDataReceived] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                        SendJoinReject(peerId, 2); // RoomFull
                        _transport.DisconnectPeer(peerId);
                        return;
                    }

                    // Dispatch on _gameStarted (not Phase == Playing).
                    //   Countdown peers go to LateJoin too — a standard handshake completing
                    //   after StartGame would land in a wire/PlayerId mismatch (no GameStartMessage
                    //   sent to the new peer; the race guard in CompletePeerSync catches the residual case).
                    if (_gameStarted)
                        HandleLateJoin(peerId);
                    else
                        StartHandshake(peerId);
                }
                else if (firstMsg is ReconnectRequestMessage reconnectReq)
                {
                    HandleReconnectRequest(peerId, reconnectReq);
                }
                else if (firstMsg is SpectatorJoinMessage)
                {
                    HandleSpectatorJoin(peerId);
                }
                else
                {
                    _logger?.ZLogWarning($"[ServerNetworkService] Malformed/unknown first message — peerId={peerId} disconnected");
                    _transport.DisconnectPeer(peerId);
                }
                return;
            }

            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] Malformed payload from peerId={peerId} — disconnect");
                _transport.DisconnectPeer(peerId);
                return;
            }

            switch (message)
            {
                case ClientInputMessage inputMsg:
                    HandleClientInputMessage(peerId, inputMsg);
                    break;

                case ClientInputBundleMessage bundleMsg:
                    HandleClientInputBundleMessage(peerId, bundleMsg);
                    break;

                case SyncReplyMessage syncReply:
                    HandleSyncReply(peerId, syncReply);
                    break;

                case PlayerReadyMessage readyMsg:
                    HandlePlayerReadyMessage(readyMsg, peerId);
                    break;

                case PongMessage pongMsg:
                    HandlePongMessage(peerId, pongMsg);
                    break;

                case FullStateRequestMessage fullReqMsg:
                    OnFullStateRequested?.Invoke(peerId, fullReqMsg.RequestTick);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(playerConfigMsg);
                    break;

                case PlayerBootstrapReadyMessage bootReady:
                    HandlePlayerBootstrapReady(peerId, bootReady);
                    break;
            }
        }

        private void HandlePlayerBootstrapReady(int peerId, PlayerBootstrapReadyMessage msg)
        {
            // Validate peerId-PlayerId pairing (mirrors InputCollector's first-line check).
            if (!_peerToPlayer.TryGetValue(peerId, out int expectedPlayerId)
                || expectedPlayerId != msg.PlayerId)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] BootstrapReady peer/player mismatch: peerId={peerId}, msg.PlayerId={msg.PlayerId}, expected={expectedPlayerId}");
                return;
            }

            // Drop late acks (post-CompleteBootstrap) to protect Reconnect / retry paths.
            if (_engine == null || _engine.State != KlothoState.BootstrapPending)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] BootstrapReady dropped (engineState={_engine?.State}): peerId={peerId}, playerId={msg.PlayerId}");
                return;
            }

            if (!_bootstrapAckedPlayers.Add(msg.PlayerId))
                return;

            _logger?.ZLogInformation($"[ServerNetworkService] BootstrapReady ack: playerId={msg.PlayerId}, acked={_bootstrapAckedPlayers.Count}/{_players.Count}");

            if (_bootstrapAckedPlayers.Count >= _players.Count)
                CompleteBootstrap();
        }

        // Closes the bootstrap window — called from ack-complete and timeout paths.
        // Order: clear pending flag → broadcast first-tick alignment → flip engine state.
        private void CompleteBootstrap()
        {
            _inputCollector?.SetBootstrapPending(false);
            BroadcastBootstrapBegin();
            (_engine as KlothoEngine)?.MarkBootstrapComplete();
        }

        private void BroadcastBootstrapBegin()
        {
            // firstTick mirrors engine CurrentTick (= 0 while BootstrapPending blocks UpdateServerTick).
            // tickStartTimeMs anchors client _accumulator to the server's actual tick start.
            int firstTick = _engine?.CurrentTick ?? 0;
            long tickStartTimeMs = _sharedClock.IsValid ? _sharedClock.SharedNow : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var msg = new BootstrapBeginMessage
            {
                FirstTick = firstTick,
                TickStartTimeMs = tickStartTimeMs,
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                    _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                for (int i = 0; i < _spectators.Count; i++)
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
            _logger?.ZLogInformation($"[ServerNetworkService] BootstrapBegin broadcast: firstTick={firstTick}, tickStartTimeMs={tickStartTimeMs}");
        }

        // Bootstrap timeout — falls back to FullState resync for unacked peers, then completes.
        private void CheckBootstrapTimeout(long now)
        {
            if (_bootstrapTimedOut) return;
            if (_engine == null || _engine.State != KlothoState.BootstrapPending) return;
            if (now - _bootstrapWindowOpenedTimeMs < BOOTSTRAP_TIMEOUT_MS) return;

            _bootstrapTimedOut = true;
            int unackedCount = 0;

            // Unicast FullState resync to each unacked peer; they recover via the determinism-failure path.
            foreach (var kvp in _peerToPlayer)
            {
                if (_bootstrapAckedPlayers.Contains(kvp.Value)) continue;
                unackedCount++;
                (_engine as KlothoEngine)?.SendBootstrapTimeoutResync(kvp.Key);
            }

            _logger?.ZLogWarning($"[ServerNetworkService] Bootstrap timeout: acked={_bootstrapAckedPlayers.Count}/{_players.Count}, unacked={unackedCount} — completing with FullState resync");
            CompleteBootstrap();
        }

        // ── Command rejection feedback (transport + game-layer unicast) ─────

        private void HandleEngineSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is CommandRejectedSimEvent rejectEvt)
            {
                if (!TryGetPeerId(rejectEvt.PlayerId, out int peerId))
                {
                    _logger?.ZLogWarning($"[ServerNetworkService] Reject feedback skip: playerId={rejectEvt.PlayerId} not in peer map");
                    return;
                }
                TryUnicastReject(peerId, rejectEvt.Tick, rejectEvt.CommandTypeId, rejectEvt.ReasonEnum);
            }
            // Future game-layer reject types: add additional case branches here. Engine stays agnostic.
        }

        private bool TryGetPeerId(int playerId, out int peerId)
        {
            foreach (var kvp in _peerToPlayer)
            {
                if (kvp.Value == playerId)
                {
                    peerId = kvp.Key;
                    return true;
                }
            }
            peerId = -1;
            return false;
        }

        private void TryUnicastReject(int peerId, int tick, int cmdTypeId, RejectionReason reason)
        {
            if (!ConsumeRejectToken(peerId)) return;

            var msg = new CommandRejectedMessage
            {
                Tick = tick,
                CommandTypeId = cmdTypeId,
            };
            msg.ReasonEnum = reason;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        // Token bucket — drops surplus rejects (bug / abusive client) while preserving normal feedback rate.
        private bool ConsumeRejectToken(int peerId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!_rejectTokens.TryGetValue(peerId, out var s))
                s = new RejectTokenState { Tokens = REJECT_BUCKET_CAPACITY, LastRefillMs = now };

            long elapsed = now - s.LastRefillMs;
            if (elapsed > 0)
            {
                int refill = (int)(elapsed * REJECT_TOKENS_PER_SEC / 1000);
                if (refill > 0)
                {
                    s.Tokens = Math.Min(REJECT_BUCKET_CAPACITY, s.Tokens + refill);
                    s.LastRefillMs = now;
                }
            }

            if (s.Tokens <= 0)
            {
                _rejectTokens[peerId] = s;
                return false;
            }

            s.Tokens--;
            _rejectTokens[peerId] = s;
            return true;
        }

        private void HandlePlayerConfigMessage(PlayerConfigMessage msg)
        {
            // Deserialize ConfigData and store in the server engine
            var configMsg = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            if (configMsg != null)
            {
                (_engine as KlothoEngine)?.HandlePlayerConfigReceived(msg.PlayerId, configMsg);
            }

            // Cache as raw bytes for late-join guests (copy since the source buffer may be pooled)
            byte[] cached = new byte[msg.ConfigData.Length];
            Buffer.BlockCopy(msg.ConfigData, 0, cached, 0, msg.ConfigData.Length);
            _playerConfigBytes[msg.PlayerId] = cached;

            // Broadcast to all clients (including sender — sender also stores locally via MessageSerializer)
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void HandleClientInputMessage(int peerId, ClientInputMessage msg)
        {
            _logger?.ZLogTrace($"[HandleClientInput] peerId={peerId}, tick={msg.Tick}, dataLen={msg.CommandDataLength}");

            // Deserialize command
            var cmdSpan = msg.CommandDataSpan;
            if (cmdSpan.Length < 4)
                return;

            var reader = new SpanReader(cmdSpan);
            var command = _commandFactory.DeserializeCommandRaw(ref reader);
            if (command == null)
                return;

            // Delegate to InputCollector (includes peerId-PlayerId validation and deadline check)
            if (_inputCollector.TryAcceptInput(peerId, msg.Tick, msg.PlayerId, command))
            {
                // Update peer state LastAckedTick
                if (_peerStates.TryGetValue(peerId, out var peerInfo))
                {
                    if (msg.Tick > peerInfo.LastAckedTick)
                        peerInfo.LastAckedTick = msg.Tick;
                }
            }
            else
            {
                CommandPool.Return(command);
            }

            // Send ACK — also ACK rejected past-tick inputs to allow client unacked queue cleanup
            SendInputAck(peerId, msg.Tick);
        }

        private void HandleClientInputBundleMessage(int peerId, ClientInputBundleMessage bundle)
        {
            int maxAckedTick = -1;

            for (int i = 0; i < bundle.Count; i++)
            {
                var entry = bundle.Entries[i];
                if (entry.CommandDataLength < 4)
                    continue;

                var reader = new SpanReader(entry.CommandData.AsSpan(0, entry.CommandDataLength));
                var command = _commandFactory.DeserializeCommandRaw(ref reader);
                if (command == null)
                    continue;

                if (_inputCollector.TryAcceptInput(peerId, entry.Tick, bundle.PlayerId, command))
                {
                    if (entry.Tick > maxAckedTick)
                        maxAckedTick = entry.Tick;
                }
                else
                {
                    CommandPool.Return(command);
                }
            }

            // Track max tick in bundle (including rejected)
            int maxBundleTick = -1;
            for (int i = 0; i < bundle.Count; i++)
            {
                if (bundle.Entries[i].Tick > maxBundleTick)
                    maxBundleTick = bundle.Entries[i].Tick;
            }

            if (maxAckedTick >= 0)
            {
                if (_peerStates.TryGetValue(peerId, out var peerInfo))
                {
                    if (maxAckedTick > peerInfo.LastAckedTick)
                        peerInfo.LastAckedTick = maxAckedTick;
                }
            }

            // Send ACK — includes rejected past-tick inputs to allow client unacked queue cleanup
            int ackTick = Math.Max(maxAckedTick, maxBundleTick);
            if (ackTick >= 0)
                SendInputAck(peerId, ackTick);
        }

        private void HandlePlayerReadyMessage(PlayerReadyMessage msg, int fromPeerId)
        {
            _logger?.ZLogInformation($"[ServerNetworkService] Player ready: playerId={msg.PlayerId}, isReady={msg.IsReady}, fromPeerId={fromPeerId}");

            var player = _players.Find(p => p.PlayerId == msg.PlayerId);
            if (player != null)
            {
                player.IsReady = msg.IsReady;
            }

            // Relay to other peers
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key != fromPeerId)
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
                }
            }

            // Start game when all players are ready
            int minStartPlayers = Math.Min(_sessionConfig.MinPlayers, MaxPlayersPerRoom);
            if (!_minPlayersClampWarned && minStartPlayers != _sessionConfig.MinPlayers)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] MinPlayers clamped to MaxPlayersPerRoom: {_sessionConfig.MinPlayers} -> {minStartPlayers} (MaxPlayersPerRoom={MaxPlayersPerRoom})");
                _minPlayersClampWarned = true;
            }
            if (AllPlayersReady && _players.Count >= minStartPlayers)
            {
                StartGame();
            }
        }

        private void HandlePongMessage(int peerId, PongMessage msg)
        {
            long rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.Timestamp;
            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = _players.Find(p => p.PlayerId == playerId);
                if (player != null)
                    player.Ping = (int)rtt;

                // Feed short-window smoother (always-on; consumer = push-decision path).
                // Independent of RttMetricsEnabled (which gates measurement-only emit).
                if (Phase == SessionPhase.Playing)
                {
                    if (!_rttSmoothers.TryGetValue(playerId, out var smoother))
                    {
                        smoother = new PlayerRttSmoother();
                        _rttSmoothers[playerId] = smoother;
                    }
                    smoother.OnSample((int)rtt);
                    MaybePushExtraDelayUpdate(playerId, peerId);
                }

                if (Phase == SessionPhase.Playing && RttMetricsEnabled)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long matchTimeSec = (nowMs - _playingStartMs) / 1000;
                    _logger?.ZLogInformation(
                        $"[Metrics][RttSample] {{\"v\":1,\"roomId\":{_roomId},\"playerId\":{playerId},\"peerId\":{peerId},\"sampleMs\":{rtt},\"matchId\":{_matchId},\"matchTimeSec\":{matchTimeSec}}}");

                    if (!_matchRttAcc.TryGetValue(playerId, out var acc))
                    {
                        acc = new MatchRttAccumulator
                        {
                            RoomId = _roomId,
                            MatchId = _matchId,
                            PlayerId = playerId,
                            PeerId = peerId,
                            StartTimeMs = nowMs,
                        };
                        _matchRttAcc[playerId] = acc;
                    }
                    if (acc.PrevSampleMs > 0 && rtt >= acc.PrevSampleMs * 2) acc.SpikeCount++;
                    if (rtt > 250) acc.ThresholdExceedCount++;
                    acc.Samples.Add((int)rtt);
                    acc.PrevSampleMs = (int)rtt;
                }
            }
        }

        private void EmitRttMatchAggregate(MatchRttAccumulator acc)
        {
            if (acc.Samples.Count == 0) return;
            long durationSec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - acc.StartTimeMs) / 1000;
            // one-shot at match end — single sort allocation tolerated
            var sorted = acc.Samples.OrderBy(x => x).ToArray();
            int min = sorted[0], max = sorted[^1];
            int mean = (int)acc.Samples.Average();
            int p50 = sorted[sorted.Length / 2];
            int p95 = sorted[(int)(sorted.Length * 0.95)];
            int p99 = sorted[(int)(sorted.Length * 0.99)];
            double thresholdExceedFrac = (double)acc.ThresholdExceedCount / acc.Samples.Count;
            string fracStr = thresholdExceedFrac.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            _logger?.ZLogInformation(
                $"[Metrics][RttMatch] {{\"v\":1,\"roomId\":{acc.RoomId},\"playerId\":{acc.PlayerId},\"matchId\":{acc.MatchId},\"durationSec\":{durationSec},\"sampleCount\":{acc.Samples.Count},\"min\":{min},\"max\":{max},\"mean\":{mean},\"p50\":{p50},\"p95\":{p95},\"p99\":{p99},\"spikeCount\":{acc.SpikeCount},\"thresholdExceedFrac\":{fracStr}}}");
            acc.Samples.Clear();
        }

        // ── Dynamic InputDelay push ─────────────────────────────────────

        private void MaybePushExtraDelayUpdate(int playerId, int peerId)
        {
            if (!_rttSmoothers.TryGetValue(playerId, out var smoother))
                return;
            if (!smoother.TryGetSmoothedRtt(out int smoothedRtt))
                return;

            // Pure compute — no per-sample log emit. Instance wrapper (which emits
            // [ServerNetworkService][{tag}] + [Metrics][{tag}]) is reserved for 1-shot entry events
            // (Sync / LateJoin / Reconnect). Mid-match emits are limited to actual push events
            // via [Metrics][DynamicDelay] with tag="DynamicDelayPush".
            var (newExtraDelay, _, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                smoothedRtt,
                _simConfig.TickIntervalMs,
                _simConfig.LateJoinDelaySafety,
                _simConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            // First entry path is seeded at CompletePeerSync / CompleteLateJoinSync /
            // HandleReconnectRequest, so this lookup normally hits. If absent (race or path
            // gap), treat lastPushed as 0 and require asymmetric UP threshold for first push.
            int lastPushed = _lastPushedExtraDelay.TryGetValue(peerId, out int v) ? v : 0;
            int diff = newExtraDelay - lastPushed;
            int absDiff = diff >= 0 ? diff : -diff;
            int threshold = (diff > 0) ? EXTRA_DELAY_PUSH_THRESHOLD_UP : EXTRA_DELAY_PUSH_THRESHOLD_DOWN;
            if (absDiff < threshold)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastPushTimeMs.TryGetValue(peerId, out long lastTime)
                && now - lastTime < MIN_PUSH_INTERVAL_MS)
                return;

            string reason = diff > 0 ? "threshold_up" : "threshold_down";
            PushExtraDelayUpdate(peerId, playerId, newExtraDelay, smoothedRtt, lastPushed, reason);
            _lastPushedExtraDelay[peerId] = newExtraDelay;
            _lastPushTimeMs[peerId] = now;
        }

        private void PushExtraDelayUpdate(int peerId, int playerId, int extraDelay, int avgRttMs, int prevDelay, string reason)
        {
            var msg = new RecommendedExtraDelayUpdateMessage
            {
                RecommendedExtraDelay = extraDelay,
                AvgRttMs = avgRttMs,
            };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
            _logger?.ZLogDebug(
                $"[ServerNetworkService][DynamicDelay] Push: peerId={peerId}, prev={prevDelay}, new={extraDelay}, avgRtt={avgRttMs}ms");
            _logger?.ZLogInformation(
                $"[Metrics][DynamicDelay] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"DynamicDelayPush\",\"avgRtt\":{avgRttMs},\"prevDelay\":{prevDelay},\"newDelay\":{extraDelay},\"reason\":\"{reason}\"}}");
        }

        // ── Handshake ─────────────────────────────────────

        private void HandlePeerConnected(int peerId)
        {
            _pendingPeers.Add(peerId);
        }

        private void HandlePeerDisconnected(int peerId)
        {
            _logger?.ZLogInformation($"[ServerNetworkService] Peer disconnected: peerId={peerId}");

            _pendingPeers.Remove(peerId);

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = _players.Find(p => p.PlayerId == playerId);
                if (player != null)
                {
                    if (Phase == SessionPhase.Playing)
                    {
                        // Disconnected while Playing → wait for reconnect
                        player.ConnectionState = PlayerConnectionState.Disconnected;
                        var info = RentDisconnectedInfo();
                        if (info != null)
                        {
                            info.PlayerId = playerId;
                            info.PeerId = peerId;
                            info.DisconnectTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            info.LastConfirmedTick = _engine?.CurrentTick ?? 0;
                            info.PredictedTickCount = 0;
                            _peerDeviceIds.TryGetValue(peerId, out var disconnectedDeviceId);
                            info.DeviceId = disconnectedDeviceId ?? string.Empty;
                            // Capture last RTT sample before _peerSyncStates removal — Reconnect's new
                            // peerId would otherwise miss this entry.
                            if (_peerSyncStates.TryGetValue(peerId, out var disconnSyncState))
                                disconnSyncState.GetBestSample(out info.LastAvgRtt, out _);
                            _disconnectedPlayerCount++;
                            _engine?.NotifyPlayerDisconnected(playerId);
                            OnPlayerDisconnected?.Invoke(player);
                            if (_matchRttAcc.TryGetValue(playerId, out var rttAcc))
                            {
                                EmitRttMatchAggregate(rttAcc);
                                _matchRttAcc.Remove(playerId);
                            }
                            // Reconnect window: drop smoother. Fresh entry will be created on first
                            // pong from the new peer — RTT distribution may differ on reconnect.
                            _rttSmoothers.Remove(playerId);
                            // peerId changes on reconnect → push state for the old peerId becomes stale.
                            _lastPushedExtraDelay.Remove(peerId);
                            _lastPushTimeMs.Remove(peerId);
                            // Flush any in-progress PastTick burst for the disconnecting peer.
                            if (_pastTickBursts.TryGetValue(peerId, out var pendingBurst))
                            {
                                EmitBurstDuration(peerId, pendingBurst);
                                _pastTickBursts.Remove(peerId);
                            }
                        }
                        else
                        {
                            _players.Remove(player);
                            _inputCollector.RemovePlayer(playerId);
                            _engine?.NotifyPlayerLeft(playerId);
                            OnPlayerLeft?.Invoke(player);
                        }
                    }
                    else
                    {
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                    }
                }
                _peerToPlayer.Remove(peerId);
            }

            _peerStates.Remove(peerId);
            _peerSyncStates.Remove(peerId);
            _peerDeviceIds.Remove(peerId);
            _rejectTokens.Remove(peerId);

            for (int i = _spectators.Count - 1; i >= 0; i--)
            {
                if (_spectators[i].PeerId == peerId)
                {
                    _spectators.RemoveAt(i);
                    break;
                }
            }

            // All players left → return to Lobby
            if (_peerToPlayer.Count == 0 && _pendingPeers.Count == 0
                && _peerSyncStates.Count == 0 && _disconnectedPlayerCount == 0
                && Phase != SessionPhase.Playing)
            {
                Phase = SessionPhase.Lobby;
            }
        }

        private void StartHandshake(int peerId)
        {
            _logger?.ZLogInformation($"[ServerNetworkService] Handshake started: peerId={peerId}");

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

            if (Phase < SessionPhase.Countdown)
                Phase = SessionPhase.Syncing;
        }

        private void SendSyncRequest(int peerId, PeerSyncState state)
        {
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
            if (msg.Magic != _sessionMagic)
                return;
            if (!_peerSyncStates.TryGetValue(peerId, out var state))
                return;
            if (state.Completed || msg.Sequence != state.SyncPacketsSent || msg.Attempt != state.Attempt)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rtt = now - state.LastSyncSentTime;
            long offset = msg.ClientTime - state.LastSyncSentTime - rtt / 2;

            state.RttSamples[state.SyncPacketsSent] = rtt;
            state.ClockOffsetSamples[state.SyncPacketsSent] = offset;
            state.SyncPacketsSent++;

            if (state.SyncPacketsSent >= NUM_SYNC_PACKETS)
                CompletePeerSync(peerId, state);
            else
                SendSyncRequest(peerId, state);
        }

        private void CompletePeerSync(int peerId, PeerSyncState state)
        {
            if (state.IsLateJoin)
            {
                CompleteLateJoinSync(peerId, state);
                return;
            }

            // Race guard for a standard handshake completing after StartGame(). _gameStarted flips
            // before Phase changes, so this catches the window where SyncReply arrives post-snapshot.
            // Reject with RoomFull(=2); the client retries via the LateJoin path.
            if (_gameStarted && !state.IsLateJoin)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] Standard handshake completed after GameStart (race): peer={peerId}, dropping for LateJoin retry");
                SendJoinReject(peerId, /*RoomFull*/2);
                _transport.DisconnectPeer(peerId);
                _peerSyncStates.Remove(peerId);
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

            _peerStates[peerId] = new ServerPeerInfo
            {
                PeerId = peerId,
                PlayerId = newPlayerId,
                State = ServerPeerState.Handshaking,
                LastAckedTick = -1
            };

            int seedExtraDelay = ComputeRecommendedExtraDelay(avgRtt, newPlayerId, peerId, "Sync");
            var syncComplete = new SyncCompleteMessage
            {
                Magic = _sessionMagic,
                PlayerId = newPlayerId,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                RecommendedExtraDelay = seedExtraDelay,
            };
            // Seed push baseline with the handshake-time value sent in SyncCompleteMessage so the
            // first MaybePushExtraDelayUpdate compares against the value the client already applied.
            _lastPushedExtraDelay[peerId] = seedExtraDelay;
            using (var serialized = _messageSerializer.SerializePooled(syncComplete))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // Propagate SimulationConfig to SD client (host authority model)
            SendSimulationConfig(peerId);

            // If HardToleranceMs == 0 (auto), recalculate tolerance from the first RTT sample.
            // deadline = now_at_tick(cmd.Tick) + H. Since cmd.Tick = serverTick+L+D,
            // the window opens L+D ticks later, reducing the minimum required H.
            // tickBase = (leadTicks + delayTicks + 1) × T makes L·D explicit in the formula.
            // Skip recalculation if avgRtt is abnormal (≤ 0 or > 240ms).
            if (_simConfig != null && _simConfig.HardToleranceMs == 0
                && avgRtt > 0 && avgRtt <= 240)
            {
                int leadTicks = _simConfig.GetEffectiveSDInputLeadTicks();
                int delayTicks = _simConfig.InputDelayTicks;
                int tickBase = _simConfig.TickIntervalMs * (leadTicks + delayTicks + 1);
                int rttBased = tickBase + avgRtt / 2 + 20;
                _logger?.ZLogInformation($"[ServerNetworkService][CompletePeerSync] HardTolerance recalculated: peerId={peerId}, avgRtt={avgRtt}ms, tolerance={rttBased}ms (lead={leadTicks} delay={delayTicks} tickBase={tickBase} + avgRtt({avgRtt})/2 + 20)");

                _inputCollector.Configure(rttBased, _peerToPlayer);
            }

            // Handshake complete → Synchronized (if no other pending handshakes)
            _peerStates[peerId].State = ServerPeerState.Playing; // Initial connection goes directly to Playing

            Phase = SessionPhase.Synchronized;
            OnPlayerJoined?.Invoke(newPlayer);
        }

        private void SendSimulationConfig(int peerId)
        {
            var simConfig = _engine?.SimulationConfig;
            if (simConfig == null) return;

            var msg = new SimulationConfigMessage();
            msg.CopyFrom(simConfig);
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Spectators ──────────────────────────────────────────

        private void HandleSpectatorJoin(int peerId)
        {
            if (_spectators.Count >= _maxSpectatorsPerRoom)
            {
                _logger?.ZLogWarning($"[ServerNetworkService] Spectator rejected: count={_spectators.Count}, max={_maxSpectatorsPerRoom}");
                SendJoinReject(peerId, /*RoomFull*/2);
                _transport.DisconnectPeer(peerId);
                return;
            }

            var info = new SpectatorInfo
            {
                SpectatorId = _spectators.Count,
                PeerId = peerId,
                LastSentTick = -1
            };
            _spectators.Add(info);

            // Send SpectatorAcceptMessage — set all fields same as P2P HandleSpectatorJoin
            var acceptMsg = new SpectatorAcceptMessage
            {
                SpectatorId = info.SpectatorId,
                RandomSeed = _randomSeed,
                CurrentTick = _engine?.CurrentTick ?? 0,
                LastVerifiedTick = _engine?.LastVerifiedTick ?? -1,
            };
            if (_engine?.SimulationConfig != null)
                acceptMsg.CopySimulationConfigFrom(_engine.SimulationConfig);
            if (_engine?.SessionConfig != null)
                acceptMsg.CopySessionConfigFrom(_engine.SessionConfig);
            for (int i = 0; i < _players.Count; i++)
                acceptMsg.PlayerIds.Add(_players[i].PlayerId);

            using (var serialized = _messageSerializer.SerializePooled(acceptMsg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // Removed direct FullState send — SpectatorService reads LastVerifiedTick from SpectatorAcceptMessage
            // and sends FullStateRequestMessage explicitly, unifying to a single code path.
            // Subsequent BroadcastVerifiedState calls will also deliver VerifiedStateMessage to the spectator peer.
        }

        // ── Reconnect support (pooling) ──────────────────────────────

        private void InitDisconnectedPlayerPool(int maxPlayers)
        {
            _disconnectedPlayerPool = new DisconnectedPlayerInfo[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
                _disconnectedPlayerPool[i] = new DisconnectedPlayerInfo();
            _disconnectedPlayerCount = 0;
        }

        private DisconnectedPlayerInfo RentDisconnectedInfo()
        {
            for (int i = 0; i < _disconnectedPlayerPool.Length; i++)
            {
                if (!_disconnectedPlayerPool[i].IsActive)
                    return _disconnectedPlayerPool[i];
            }
            return null;
        }

        private void CheckDisconnectedPlayerTimeout()
        {
            if (_disconnectedPlayerCount == 0 || _sessionConfig == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < _disconnectedPlayerPool.Length; i++)
            {
                var info = _disconnectedPlayerPool[i];
                if (!info.IsActive)
                    continue;

                if (now - info.DisconnectTimeMs > _sessionConfig.ReconnectTimeoutMs)
                {
                    int playerId = info.PlayerId;
                    info.Reset();
                    _disconnectedPlayerCount--;

                    var player = _players.Find(p => p.PlayerId == playerId);
                    if (player != null)
                    {
                        _players.Remove(player);
                        _inputCollector.RemovePlayer(playerId);
                        _engine?.NotifyPlayerLeft(playerId);
                        OnPlayerLeft?.Invoke(player);
                    }
                }
            }
        }

        // ── Suppress unused event warnings ─────────────────────────

        internal void SuppressWarnings()
        {
            OnCommandReceived?.Invoke(null);
            OnDesyncDetected?.Invoke(0, 0, 0, 0);
            OnFrameAdvantageReceived?.Invoke(0, 0);
            OnLocalPlayerIdAssigned?.Invoke(0);
            OnFullStateReceived?.Invoke(0, null, 0);
            OnReconnecting?.Invoke();
            OnReconnectFailed?.Invoke(null);
            OnReconnected?.Invoke();
            OnCountdownStarted?.Invoke(0);
            OnVerifiedStateReceived?.Invoke(0, null, 0);
            OnInputAckReceived?.Invoke(0);
            OnServerFullStateReceived?.Invoke(0, null, 0);
            OnBootstrapBegin?.Invoke(0, 0);
            OnCommandRejected?.Invoke(0, 0, default);
        }

        private readonly JoinRejectMessage _joinRejectCache = new JoinRejectMessage();

        private void SendJoinReject(int peerId, byte reason)
        {
            _joinRejectCache.Reason = reason;
            using var msg = _messageSerializer.SerializePooled(_joinRejectCache);
            _transport.Send(peerId, msg.Data, msg.Length, DeliveryMethod.Reliable);
        }

        // ── Player count accounting helpers ─────────────────────────

        // Phase-branched effective slot count.
        //   Pre-GameStart: _players.Count (slot reuse on leave) + pending handshakes.
        //   Post-GameStart: Math.Max(_assignedPlayerIdCount, _nextPlayerId-1) enforces both
        //     the capacity invariant and the bot-ID invariant — covers sparse distributions
        //     (e.g., {1,4} → max=4 blocks LateJoin from invading bot space).
        private int EffectivePlayerCount
        {
            get
            {
                int pending = CountPendingHandshakes();
                if (!_gameStarted)
                    return _players.Count + pending;

                int occupiedSlots = Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1);
                return occupiedSlots + pending;
            }
        }

        // Pre-GameStart slot reuse — smallest unused PlayerId in [1, upper].
        //   SD (LocalPlayerId == -1): server has no slot, players use [1, MaxPlayerCapacity].
        //   Returns -1 only if all slots are full (callers' gate must prevent this; -1 = regression).
        private int FindSmallestUnusedPlayerId()
        {
            int upper = (LocalPlayerId == 0) ? MaxPlayerCapacity - 1 : MaxPlayerCapacity;
            for (int id = 1; id <= upper; id++)
            {
                bool used = false;
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].PlayerId == id) { used = true; break; }
                }
                if (!used) return id;
            }
            return -1;
        }

        // Phase-branched slot reservation + reject action capsule.
        //   Pre-GameStart: smallest unused ID (slot reuse).
        //   Post-GameStart: monotonic _nextPlayerId++ (permanent occupation).
        //   On reject: SendJoinReject(RoomFull=2) + DisconnectPeer + immediate _peerSyncStates.Remove —
        //   the transport disconnect is async; without explicit removal, the stale entry keeps counting.
        private bool TryReservePlayerSlot(int peerId, out int newPlayerId)
        {
            if (!_gameStarted)
            {
                newPlayerId = FindSmallestUnusedPlayerId();
                if (newPlayerId < 0)
                {
                    _logger?.ZLogError($"[ServerNetworkService] FindSmallestUnusedPlayerId returned -1: peer={peerId}, players={_players.Count}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                    SendJoinReject(peerId, /*RoomFull*/2);
                    _transport.DisconnectPeer(peerId);
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
            }
            else
            {
                if (Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1) >= MaxPlayerCapacity)
                {
                    _logger?.ZLogError($"[ServerNetworkService] Post-GameStart slot overflow: assigned={_assignedPlayerIdCount}, nextId={_nextPlayerId}, max={MaxPlayerCapacity}, peer={peerId}");
                    SendJoinReject(peerId, /*RoomFull*/2);
                    _transport.DisconnectPeer(peerId);
                    _peerSyncStates.Remove(peerId);
                    newPlayerId = -1;
                    return false;
                }
                newPlayerId = _nextPlayerId++;
                _assignedPlayerIdCount++;
            }
            return true;
        }

        private class MatchRttAccumulator
        {
            public int RoomId;
            public long MatchId;
            public int PlayerId;
            public int PeerId;
            public long StartTimeMs;
            public List<int> Samples = new List<int>(256);
            public int SpikeCount;
            public int ThresholdExceedCount;
            public int PrevSampleMs;
        }

        private class PastTickBurstState
        {
            public long FirstRejectMs;
            public long LastRejectMs;
            public int RejectCount;

            public void Reset(long nowMs)
            {
                FirstRejectMs = nowMs;
                LastRejectMs = nowMs;
                RejectCount = 1;
            }
        }
    }
}
