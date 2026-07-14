using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using System.Linq;

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
        // Routed/connected-but-not-joined peer eviction: a peer that connected (and, in multi-room, was routed
        // via RoomHandshake) but never sent its first app message (PlayerJoin/ReconnectRequest/SpectatorJoin)
        // holds a transport slot indefinitely. Evict after this timeout (2x SYNC_TIMEOUT_MS).
        private const int PENDING_PEER_TIMEOUT_MS = 10000;
        private const int PING_INTERVAL_MS = 1000;
        private const int BOOTSTRAP_TIMEOUT_MS = 1000;
        private const int REJECT_TOKENS_PER_SEC = 10;
        private const int REJECT_BUCKET_CAPACITY = 10;

        private IKLogger _logger;
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
        // Per-peer claimed display name captured at PlayerJoinMessage receipt and read later by
        // CompletePeerSync. Same lifecycle as _peerDeviceIds (cleared on reset, removed on disconnect).
        private readonly Dictionary<int, string> _peerClaimedDisplayNames = new Dictionary<int, string>();
        // Per-peer lobby ticket captured at PlayerJoinMessage receipt and read later by the validation
        // hook. Same lifecycle as _peerClaimedDisplayNames (cleared on reset, removed on disconnect).
        private readonly Dictionary<int, string> _peerTickets = new Dictionary<int, string>();
        // Authority-side ticket validator (SD server). null = no validation (behaviour unchanged). Injected
        // by RoomManager from the server config. A pending (async) redeem parks in _pendingValidation until
        // the poll-drain completes it. SD only — P2P validation is synchronous.
        private IPlayerIdentityValidator _identityValidator;
        // Authority-side player-config entitlement guard. null = no cross-check (selection passes through
        // unchanged). Injected like _identityValidator (SetPlayerConfigEntitlementGuard).
        private IPlayerConfigEntitlementGuard _entitlementGuard;
        // Authority-side gate for client-submitted in-match reliable commands. null = no cross-check (every
        // reliable command is accepted). Injected like _entitlementGuard (SetReliableCommandEntitlementGate).
        private IReliableCommandEntitlementGate _reliableCommandGate;
        // Peers whose async ticket validation is in flight (slot not yet reserved). Drained each tick in
        // Update → DrainPendingValidations, and evicted on disconnect. _pendingValidationDrainKeys is a
        // reused scratch buffer to iterate without mutating the dict.
        private readonly Dictionary<int, PendingValidation> _pendingValidation = new Dictionary<int, PendingValidation>();
        private readonly List<int> _pendingValidationDrainKeys = new List<int>();
        private int _nextPlayerId = 1; // No local player on server (playerId=0)

        // Raw bytes cache for forwarding existing player PlayerConfigs to late-join guests
        private readonly Dictionary<int, byte[]> _playerConfigBytes = new Dictionary<int, byte[]>();

        // Peer state (Handshaking → CatchingUp → Playing)
        private readonly Dictionary<int, ServerPeerInfo> _peerStates = new Dictionary<int, ServerPeerInfo>();

        // Handshake
        private readonly Dictionary<int, PeerSyncState> _peerSyncStates = new Dictionary<int, PeerSyncState>();
        // peerId → connectedAtMs (first-message timeout anchor; see PENDING_PEER_TIMEOUT_MS).
        private readonly Dictionary<int, long> _pendingPeers = new Dictionary<int, long>();
        // Reused scratch for the pending-peer sweep (collect-then-remove without mutating during iteration).
        private readonly List<int> _expiredPendingPeers = new List<int>();

        // Injectable clock for the pending-peer sweep (deterministic unit tests). Defaults to wall clock;
        // Initialize may override. Only the sweep path (Update's `now` + HandlePeerConnected anchor) uses it.
        private Func<long> _nowProvider = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        // Per-peer reported effective extra-delay (client reactive absorption). Folded into
        // the RTT-based baseline (max). Reset on disconnect / handshake — same lifecycle as above.
        private readonly Dictionary<int, int> _reportedEffective = new Dictionary<int, int>();
        private const int EXTRA_DELAY_PUSH_THRESHOLD_UP = 2;       // ticks — fast UP response (storm prevention)
        private const int EXTRA_DELAY_PUSH_THRESHOLD_DOWN = 4;     // ticks — conservative DOWN (oscillation buffer)
        private const long MIN_PUSH_INTERVAL_MS = 500;             // per-peer push frequency cap

        // PastTick burst tracker — emits [Metrics][BurstDuration] when reject silence > threshold,
        // or on disconnect / Phase transition for final flush.
        private readonly Dictionary<int, PastTickBurstState> _pastTickBursts = new Dictionary<int, PastTickBurstState>();

        // FullStateRequest rate limit. A request is ~5 bytes but the response is the whole state,
        // so an unthrottled client can amplify its uplink into the server's downlink and force a
        // re-serialize (+ heap alloc) on the room worker thread every tick. Mirrors the P2P host
        // guard, sharing ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS.
        // Keyed by peerId (players AND spectators — no exception, or the spectator slot stays an
        // amplification hole). Room-worker-thread only (inbound drain, disconnect and Engine.Update
        // all run there), so no lock. Dropped requests only bump DropsSinceLastServe: logging per
        // drop would hand the attacker an O(N) string-alloc + sink-write path, which is the very
        // cost the cooldown exists to deny.
        private struct PeerResyncState
        {
            public long LastServeMs;
            public int DropsSinceLastServe;
        }
        private readonly Dictionary<int, PeerResyncState> _resyncState = new Dictionary<int, PeerResyncState>();
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
        private readonly CommandRejectedMessage _commandRejectedCache = new CommandRejectedMessage();
        private readonly RecommendedExtraDelayUpdateMessage _recommendedExtraDelayCache = new RecommendedExtraDelayUpdateMessage();

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
                    _inputCollector.GetAndResetStats(out int accepted, out int pastTick, out int peerMismatch);
                    if (accepted > 0 || pastTick > 0 || peerMismatch > 0)
                    {
                        _logger?.KInformation($"[InputCollector] Phase {prevPhase} stats: accepted={accepted}, rejectedPastTick={pastTick}, rejectedPeerMismatch={peerMismatch}");
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
                    _reportedEffective.Clear();
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
                _logger?.KInformation($"[ServerNetworkService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");

                if (prevPhase != value)
                    OnPhaseChanged?.Invoke(value);
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

        // Capture-free equivalent of _players.Find(p => p.PlayerId == id) (no closure allocation).
        private PlayerInfo FindPlayerById(int playerId)
        {
            for (int i = 0; i < _players.Count; i++)
                if (_players[i].PlayerId == playerId)
                    return _players[i];
            return null;
        }

        // Server-only accessor for the per-player opaque entitlement blob. Read by the engine's tick-0 seed
        // path — kept off IPlayerInfo so it never leaks onto the roster wire. Null when the player is unknown
        // or has no entitlement.
        public byte[] GetPlayerEntitlement(int playerId) => FindPlayerById(playerId)?.Entitlement;

        // ── IServerDrivenNetworkService properties ────────────────

        public bool IsServer => true;

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

        // SD-only events (not fired on the server — client-only)
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

        // ── Input collector access (used by engine) ────────────────────

        public ServerInputCollector InputCollector => _inputCollector;

        /// <summary>
        /// peerId → playerId mapping (read-only reference).
        /// </summary>
        public IReadOnlyDictionary<int, int> PeerToPlayerMap => _peerToPlayer;

        // ── Initialization ─────────────────────────────────────────

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            _inputCollector = new ServerInputCollector();
            _inputCollector.Configure(_peerToPlayer);
            _inputCollector.SetLogger(logger);

            // Layer 1: transport-level rejects originate here — peerId is already in scope, no lookup needed.
            _inputCollector.OnCommandRejected += HandleInputCollectorRejected;

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
        }

        /// <summary>Test seam: override the clock used by the pending-peer eviction sweep (Update's `now` and
        /// the HandlePeerConnected anchor) so the timeout can be unit-tested deterministically. No-op for a
        /// null argument; production never calls this (default = wall clock).</summary>
        public void SetNowProviderForTest(Func<long> nowProvider)
        {
            if (nowProvider != null) _nowProvider = nowProvider;
        }

        /// <summary>
        /// Inject the authority-side ticket validator (SD server, server-global). null = no validation
        /// (behaviour unchanged). Called by RoomManager after Initialize, before the room goes Active.
        /// </summary>
        public void SetIdentityValidator(IPlayerIdentityValidator validator)
        {
            _identityValidator = validator;
        }

        /// <summary>
        /// Injects the authority-side player-config entitlement guard. null (default) = no cross-check;
        /// client selections pass through unchanged. Called by RoomManager alongside SetIdentityValidator.
        /// </summary>
        public void SetPlayerConfigEntitlementGuard(IPlayerConfigEntitlementGuard guard)
        {
            _entitlementGuard = guard;
        }

        /// <summary>
        /// Sets the authority-side gate for client-submitted in-match reliable commands. null (default) skips
        /// the cross-check, so every client-submitted reliable command is accepted (no regression). Called
        /// by RoomManager alongside SetIdentityValidator / SetPlayerConfigEntitlementGuard.
        /// </summary>
        public void SetReliableCommandEntitlementGate(IReliableCommandEntitlementGate gate)
        {
            _reliableCommandGate = gate;
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
            _logger?.KInformation(
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

            // Input acceptance deadline is the tick's execution moment (past-tick rejection in
            // ServerInputCollector); chronic lateness self-corrects via lead escalation.
            // HardToleranceMs is deprecated and has no effect.
            _inputCollector.Configure(_peerToPlayer);
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

        public void LeaveRoom(bool keepReconnectCredentials = false)
        {
            _transport.OnDataReceived -= HandleDataReceived;
            _transport.OnPeerConnected -= HandlePeerConnected;
            _transport.OnPeerDisconnected -= HandlePeerDisconnected;

            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Clear();
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
            _peerToPlayer.Clear();
            _peerDeviceIds.Clear();
            _peerClaimedDisplayNames.Clear();
            _peerTickets.Clear();
            foreach (var pv in _pendingValidation.Values) pv.Handle.Dispose();
            _pendingValidation.Clear();
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

        // The server is the authority and never submits a reliable command — no-op (mirrors SendCommand).
        public void SendReliableCommand(ICommand command)
        {
            // No local reliable submit on server.
        }

        public void RequestCommandsForTick(int tick)
        {
            // Not needed in SD mode
        }

        public void SendSyncHash(int tick, long hash, long cmdHash)
        {
            // The server is the source of truth for hashes — no-op
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

        // Server-driven mode does not piggyback frame-advantage on CommandMessage — no-op.
        public void SetLocalAdvantage(int advantage) { }

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
#if KLOTHO_FAULT_INJECTION
            // Scenario C: drop the desync-resync unicast for the targeted player so its
            // FullState request stays unanswered, exercising the SD timeout/retry/terminate ladder.
            if (_peerToPlayer.TryGetValue(peerId, out int fiPlayerId)
                && xpTURN.Klotho.Diagnostics.FaultInjection.DropFullStateResponsePlayerIds.Contains(fiPlayerId))
            {
                _logger?.KWarning($"[FaultInjection][SD] Dropping FullStateResponse: peerId={peerId}, playerId={fiPlayerId}, tick={tick}");
                return;
            }
#endif
            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateData = stateData,
                StateHash = stateHash,
                StaticFingerprint = (_engine as KlothoEngine)?.GetLocalStaticFingerprint() ?? 0,
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
        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
        {
            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateData = stateData,
                StateHash = stateHash,
                KindEnum = kind,
                StaticFingerprint = (_engine as KlothoEngine)?.GetLocalStaticFingerprint() ?? 0,
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
            long now = _nowProvider();

            // Handshake timeout check. Skip AwaitingValidation peers: sync is done, the
            // peer is parked on async identity validation — re-sending SyncRequest would be spurious.
            foreach (var kvp in _peerSyncStates)
            {
                var state = kvp.Value;
                if (!state.Completed && !state.AwaitingValidation && now - state.LastSyncSentTime > SYNC_TIMEOUT_MS)
                {
                    state.Attempt++;
                    SendSyncRequest(kvp.Key, state);
                }
            }

            // Pending-peer timeout: a peer that connected/routed but never sent its first app message
            // (PlayerJoin / ReconnectRequest / SpectatorJoin) → evict so the held transport slot is freed.
            // Runs in every phase (incl. Playing late-join attempts). Collect-then-remove to avoid mutating
            // during iteration; the disconnect's OnPeerDisconnected does the rest of the cleanup.
            if (_pendingPeers.Count > 0)
            {
                _expiredPendingPeers.Clear();
                foreach (var kvp in _pendingPeers)
                {
                    if (now - kvp.Value > PENDING_PEER_TIMEOUT_MS)
                        _expiredPendingPeers.Add(kvp.Key);
                }
                for (int i = 0; i < _expiredPendingPeers.Count; i++)
                {
                    int pendingPeerId = _expiredPendingPeers[i];
                    _logger?.KWarning($"[ServerNetworkService] Pending peer {pendingPeerId} timed out (no join), disconnecting");
                    _pendingPeers.Remove(pendingPeerId);
                    _transport.DisconnectPeer(pendingPeerId);
                }
            }

            // Drain pending async identity validations. After the sweep / before reconnect checks;
            // runs every tick via Engine.Update → _networkService.Update.
            DrainPendingValidations(now);

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
                _logger?.KWarning($"[ServerNetworkService] StartGame called twice — ignoring (snapshot already done)");
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
                MinPlayers = _sessionConfig.MinPlayers,
                MaxSpectators = _sessionConfig.MaxSpectators,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelaySafety = _sessionConfig.LateJoinDelaySafety,
                RttSanityMaxMs = _sessionConfig.RttSanityMaxMs,
                MinStallAbortTicks = _sessionConfig.MinStallAbortTicks,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                AbortGraceMs = _sessionConfig.AbortGraceMs,
                EndGracePolicy = (int)_sessionConfig.EndGracePolicy,
                EndGraceMs = _sessionConfig.EndGraceMs,
                ClientShutdownGraceMs = _sessionConfig.ClientShutdownGraceMs,
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
            if (_pendingPeers.ContainsKey(peerId))
            {
                // Pre-auth memory-amplification guard: reject an oversized first message before
                // Deserialize allocates per-field strings or the Ticket is retained pre-validation.
                if (length > PlayerJoinMessage.MaxPreAuthMessageBytes)
                {
                    _logger?.KWarning($"[ServerNetworkService][HandleDataReceived] Oversized pre-auth message from peer {peerId}: {length}B > {PlayerJoinMessage.MaxPreAuthMessageBytes}B");
                    _pendingPeers.Remove(peerId);
                    DisconnectWithReason(peerId, JoinFailReason.IdentityInvalid.ToWireCode());
                    return;
                }
                var firstMsg = _messageSerializer.Deserialize(data, length);
                _pendingPeers.Remove(peerId);
                if (firstMsg is PlayerJoinMessage playerJoin)
                {
                    _peerDeviceIds[peerId] = playerJoin.DeviceId ?? string.Empty;
                    // Capture now (the message is a reused singleton; CompletePeerSync reads it later).
                    _peerClaimedDisplayNames[peerId] = RosterEntry.ClampClaimedName(playerJoin.ClaimedDisplayName);
                    _peerTickets[peerId] = playerJoin.Ticket ?? string.Empty; // captured now; read by the validation hook later
                    // Outer capacity gate covering both the Pre-GameStart Lobby race and Post-GameStart capacity.
                    // HandleLateJoin's own gate is the second-line defense for non-dispatch callers.
                    if (EffectivePlayerCount >= MaxPlayerCapacity)
                    {
                        _logger?.KWarning($"[ServerNetworkService][HandleDataReceived] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                        DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
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
                    _logger?.KWarning($"[ServerNetworkService] Malformed/unknown first message — peerId={peerId} disconnected");
                    _transport.DisconnectPeer(peerId);
                }
                return;
            }

            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[ServerNetworkService] Malformed payload from peerId={peerId} — disconnect");
                _transport.DisconnectPeer(peerId);
                return;
            }

            switch (message)
            {
                case ClientInputMessage inputMsg:
                    HandleClientInputMessage(peerId, inputMsg);
                    break;

                case ReliableCommandSubmitMessage submitMsg:
                    HandleReliableCommandSubmit(peerId, submitMsg);
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
                    HandleFullStateRequest(peerId, fullReqMsg);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(peerId, playerConfigMsg);
                    break;

                case PlayerBootstrapReadyMessage bootReady:
                    HandlePlayerBootstrapReady(peerId, bootReady);
                    break;

                case ReactiveExtraDelayReportMessage reactiveReport:
                    HandleReactiveExtraDelayReport(peerId, reactiveReport);
                    break;

                case DesyncProbeRequestMessage probeReq:
                    HandleDesyncProbeRequest(peerId, probeReq);
                    break;

                case DesyncVerdictReportMessage verdictMsg:
                    HandleDesyncVerdictReport(peerId, verdictMsg);
                    break;
            }
        }

        // Per-peer cooldown before another FullState is served to the same peer (see _resyncState).
        // Dropping is safe without a reject message: every requester already re-sends on its own
        // resync timeout, which is longer than the cooldown, so a legitimate retry always lands
        // outside the window. The first request from a peer is never throttled (no entry yet).
        private void HandleFullStateRequest(int peerId, FullStateRequestMessage msg)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool known = _resyncState.TryGetValue(peerId, out var state);

            if (known && now - state.LastServeMs < ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS)
            {
                state.DropsSinceLastServe++;
                _resyncState[peerId] = state;
                return;
            }

            int throttled = known ? state.DropsSinceLastServe : 0;
            _resyncState[peerId] = new PeerResyncState { LastServeMs = now, DropsSinceLastServe = 0 };

            // The only log on this path, so it is bounded by the cooldown and cannot be spammed.
            // Information for a routine serve (a late-joining spectator resyncs this way) to match
            // the P2P host; Warning once drops appeared, which is what a flooding peer looks like —
            // throttledSinceLastServe then carries the count, so no separate threshold alarm.
            _logger?.KLog(throttled > 0 ? KLogLevel.Warning : KLogLevel.Information,
                $"[ServerNetworkService] FullState request: peerId={peerId}, requestTick={msg.RequestTick}, throttledSinceLastServe={throttled}");
            OnFullStateRequested?.Invoke(peerId, msg.RequestTick);
        }

        // Client reported its effective extra-delay. Store and re-evaluate the push so the
        // server baseline absorbs the client's reactive correction (max with RTT-based) on the next tick.
        private void HandleReactiveExtraDelayReport(int peerId, ReactiveExtraDelayReportMessage msg)
        {
            if (Phase != SessionPhase.Playing) return;
            _reportedEffective[peerId] = msg.EffectiveExtraDelay < 0 ? 0 : msg.EffectiveExtraDelay;
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"sd-server\",\"dir\":\"absorb\",\"peerId\":{peerId},\"reportedEffective\":{_reportedEffective[peerId]}}}");
            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
                MaybePushExtraDelayUpdate(playerId, peerId);
        }

        private void HandlePlayerBootstrapReady(int peerId, PlayerBootstrapReadyMessage msg)
        {
            // Validate peerId-PlayerId pairing (mirrors InputCollector's first-line check).
            if (!_peerToPlayer.TryGetValue(peerId, out int expectedPlayerId)
                || expectedPlayerId != msg.PlayerId)
            {
                _logger?.KWarning($"[ServerNetworkService] BootstrapReady peer/player mismatch: peerId={peerId}, msg.PlayerId={msg.PlayerId}, expected={expectedPlayerId}");
                return;
            }

            // Drop late acks (post-CompleteBootstrap) to protect Reconnect / retry paths.
            if (_engine == null || _engine.State != KlothoState.BootstrapPending)
            {
                _logger?.KWarning($"[ServerNetworkService] BootstrapReady dropped (engineState={_engine?.State}): peerId={peerId}, playerId={msg.PlayerId}");
                return;
            }

            if (!_bootstrapAckedPlayers.Add(msg.PlayerId))
                return;

            _logger?.KInformation($"[ServerNetworkService] BootstrapReady ack: playerId={msg.PlayerId}, acked={_bootstrapAckedPlayers.Count}/{_players.Count}");

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
            _logger?.KInformation($"[ServerNetworkService] BootstrapBegin broadcast: firstTick={firstTick}, tickStartTimeMs={tickStartTimeMs}");
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

            _logger?.KWarning($"[ServerNetworkService] Bootstrap timeout: acked={_bootstrapAckedPlayers.Count}/{_players.Count}, unacked={unackedCount} — completing with FullState resync");
            CompleteBootstrap();
        }

        // ── Command rejection feedback (transport + game-layer unicast) ─────

        private void HandleEngineSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is CommandRejectedSimEvent rejectEvt)
            {
                if (!TryGetPeerId(rejectEvt.PlayerId, out int peerId))
                {
                    _logger?.KWarning($"[ServerNetworkService] Reject feedback skip: playerId={rejectEvt.PlayerId} not in peer map");
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

            var msg = _commandRejectedCache;
            msg.Tick = tick;
            msg.CommandTypeId = cmdTypeId;
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

        private void HandlePlayerConfigMessage(int peerId, PlayerConfigMessage msg)
        {
            // Security: bind to the authenticated peer's playerId — never trust the
            // wire-claimed msg.PlayerId (the dispatch does not verify it, so a client could otherwise write
            // another player's config / be checked against another player's entitlement). Drop a config from
            // an unmapped peer (not a joined player). Stamp the authoritative id so engine/cache/broadcast agree.
            if (!_peerToPlayer.TryGetValue(peerId, out int playerId))
                return;
            msg.PlayerId = playerId;

            // Deserialize the client's selection. A config the authoritative server cannot parse is dropped
            // (no apply/cache/broadcast) — it cannot be applied, entitlement-clamped, or late-join seeded, so
            // propagating its raw bytes would diverge the server from clients and bypass the guard. Mirrors the
            // P2P null-config drop. Logged: on a dedicated server a deserialize failure is an operational signal
            // (garbage/attack or a client/server config-type version mismatch), not a silent no-op.
            var selection = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            if (selection == null)
            {
                _logger?.KWarning($"[ServerNetworkService] PlayerConfig ConfigData did not deserialize (peer={peerId}, playerId={playerId}) — dropped (no apply/cache/broadcast).");
                return;
            }

            // Entitlement cross-check (server-authoritative, post-join). Unset guard → passthrough.
            if (_entitlementGuard != null)
            {
                var verdict = _entitlementGuard.Check(playerId, GetPlayerEntitlement(playerId), selection);
                if (verdict.Kind == PlayerConfigVerdictKind.Reject)
                {
                    // Post-join reject is strict-policy-only: drop the selection (no apply/cache/broadcast),
                    // do not disconnect a joined player. Default-recommended verdict is Clamp, not Reject.
                    _logger?.KWarning($"[ServerNetworkService] PlayerConfig rejected by entitlement guard: playerId={playerId}, code={JoinFailReasonExtensions.ClampIdentityWireCode(verdict.RejectWireCode)}");
                    return;
                }
                if (verdict.Kind == PlayerConfigVerdictKind.Clamp && verdict.Replacement == null)
                {
                    // Fail-closed: a Clamp verdict with no replacement selection cannot be applied. Drop the
                    // selection (no apply/cache/broadcast) instead of passing the un-clamped client original,
                    // so a guard that intended to restrict an over-privileged selection never fails open.
                    _logger?.KWarning($"[ServerNetworkService] PlayerConfig clamp with null replacement: playerId={playerId} — dropped (no apply/cache/broadcast).");
                    return;
                }
                if (verdict.Kind == PlayerConfigVerdictKind.Clamp && verdict.Replacement != null)
                {
                    // Replace with the server-chosen selection and re-serialize so engine apply, late-join
                    // cache, and broadcast all carry the same authoritative bytes (server single decision).
                    selection = verdict.Replacement;
                    int size = selection.GetSerializedSize();
                    byte[] clamped = new byte[size];
                    var writer = new SpanWriter(clamped);
                    selection.Serialize(ref writer);
                    msg.ConfigData = clamped;
                    _logger?.KInformation($"[ServerNetworkService] PlayerConfig clamped by entitlement guard: playerId={playerId}");
                }
            }

            // Apply (validated/clamped) selection to the server engine.
            (_engine as KlothoEngine)?.HandlePlayerConfigReceived(playerId, selection);

            // Cache as raw bytes for late-join guests (copy since the source buffer may be pooled)
            byte[] cached = new byte[msg.ConfigData.Length];
            Buffer.BlockCopy(msg.ConfigData, 0, cached, 0, msg.ConfigData.Length);
            _playerConfigBytes[playerId] = cached;

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
            _logger?.KTrace($"[HandleClientInput] peerId={peerId}, tick={msg.Tick}, dataLen={msg.CommandDataLength}");

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

        // Reliable command submit receive → the collector's reliable inbox. Peer↔player validation and
        // per-player high-water sequence dedup live in the collector; the past-tick check does not apply
        // (there is no client-assigned tick — the server assigns the execution tick at the next
        // CollectTickInputs). Accepted → owned by the inbox; rejected/duplicate → returned to the pool here.
        private void HandleReliableCommandSubmit(int peerId, ReliableCommandSubmitMessage msg)
        {
            _logger?.KTrace($"[HandleReliableCommandSubmit] peerId={peerId}, playerId={msg.PlayerId}, dataLen={msg.CommandDataLength}");

            var cmdSpan = msg.CommandDataSpan;
            if (cmdSpan.Length < 4)
                return;
            var reader = new SpanReader(cmdSpan);
            var command = _commandFactory.DeserializeCommandRaw(ref reader);
            if (command == null)
                return;

            // In-match entitlement gate (opt-in; null = accept all, no regression). The entire block is
            // gated on the gate being set so an unset gate is byte-identical to the prior path. Resolve the
            // authoritative playerId from the authenticated peer (the wire-claimed msg.PlayerId is untrusted)
            // and validate ONLY for a registered peer — an unregistered peer falls through to
            // TryAcceptReliable, which emits the existing PeerMismatch reject (preserving telemetry and
            // avoiding a KeyNotFound on the indexer). The gate runs BEFORE TryAcceptReliable so a dropped
            // sequence number is NOT consumed by the high-water dedup: a later resubmit of the same sequence
            // (e.g. after a resync) is re-evaluated rather than silently ignored as a duplicate.
            if (_reliableCommandGate != null
                && _peerToPlayer.TryGetValue(peerId, out int gatePlayerId))
            {
                var verdict = _reliableCommandGate.Check(gatePlayerId, GetPlayerEntitlement(gatePlayerId), command);
                if (verdict.Kind == ReliableCommandVerdictKind.Drop)
                {
                    // Trust-boundary event (unowned in-match action, or stale client entitlement) — logged at
                    // Information like the input-collector peer/tick rejects so it is visible without debug logs.
                    _logger?.KInformation($"[HandleReliableCommandSubmit] gate Drop: peerId={peerId}, playerId={gatePlayerId}, cmdTypeId={command.CommandTypeId}");
                    CommandPool.Return(command);
                    return;
                }
            }

            int seq = command is IReliableCommand rel ? rel.SequenceNumber : 0;
            if (!_inputCollector.TryAcceptReliable(peerId, msg.PlayerId, seq, command))
                CommandPool.Return(command);
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
            _logger?.KInformation($"[ServerNetworkService] Player ready: playerId={msg.PlayerId}, isReady={msg.IsReady}, fromPeerId={fromPeerId}");

            var player = FindPlayerById(msg.PlayerId);
            if (player != null)
            {
                bool prevReady = AllPlayersReady;
                player.IsReady = msg.IsReady;
                RaiseAllPlayersReadyIfChanged(prevReady);
            }

            // Relay to other peers (and spectators, tracked separately from _peerToPlayer) so spectator
            // ready display stays consistent with players.
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key != fromPeerId)
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
                }
                for (int i = 0; i < _spectators.Count; i++)
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }

            // Start game when all players are ready
            int minStartPlayers = Math.Min(_sessionConfig.MinPlayers, MaxPlayersPerRoom);
            if (!_minPlayersClampWarned && minStartPlayers != _sessionConfig.MinPlayers)
            {
                _logger?.KWarning($"[ServerNetworkService] MinPlayers clamped to MaxPlayersPerRoom: {_sessionConfig.MinPlayers} -> {minStartPlayers} (MaxPlayersPerRoom={MaxPlayersPerRoom})");
                _minPlayersClampWarned = true;
            }
            if (AllPlayersReady && _players.Count >= minStartPlayers)
            {
                StartGame();
            }
        }

        // A pong older than two ping intervals is a stale measurement, not a latency reading:
        // newer samples are already in flight, so the value reflects a pump stall / backlog
        // flush (e.g. a client that stopped polling), not current network conditions —
        // feeding such a burst shifts the smoother's sliding median wholesale and misfires
        // DynamicDelayPush. Distinct from RttSanityMaxMs (240ms default), which is
        // the delay calculator's value clamp and overlaps legitimate long-haul RTT — discarding
        // at that bound would starve high-RTT players of pushes.
        internal static bool IsStaleRttSample(long rttMs) => rttMs > PING_INTERVAL_MS * 2;

        private void HandlePongMessage(int peerId, PongMessage msg)
        {
            long rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.Timestamp;
            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = FindPlayerById(playerId);
                if (player != null)
                    player.Ping = (int)rtt;

                // Feed short-window smoother (always-on; consumer = push-decision path).
                // Independent of RttMetricsEnabled (which gates measurement-only emit).
                // Stale samples skip ONLY the smoother/push path — player.Ping above and the
                // metrics block below keep the raw value (SpikeCount/p99 exist to observe stalls).
                if (Phase == SessionPhase.Playing && !IsStaleRttSample(rtt))
                {
                    if (!_rttSmoothers.TryGetValue(playerId, out var smoother))
                    {
                        smoother = new PlayerRttSmoother();
                        _rttSmoothers[playerId] = smoother;
                    }
                    smoother.OnSample((int)rtt);
                    MaybePushExtraDelayUpdate(playerId, peerId);
                }
                else if (Phase == SessionPhase.Playing)
                {
                    _logger?.KDebug(
                        $"[ServerNetworkService][Rtt] Stale pong discarded from push path: playerId={playerId}, rtt={rtt}ms (> 2x ping interval)");
                }

                if (Phase == SessionPhase.Playing && RttMetricsEnabled)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long matchTimeSec = (nowMs - _playingStartMs) / 1000;
                    _logger?.KInformation(
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
            _logger?.KInformation(
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
            var (rttBased, _, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                smoothedRtt,
                _simConfig.TickIntervalMs,
                _sessionConfig.LateJoinDelaySafety,
                _sessionConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            // Fold the client's reported effective extra-delay into the
            // authoritative baseline so a client's locally-observed reactive correction migrates to the
            // server. Clamped to the rollback budget (MaxRollbackTicks/2) — same bound as the calculator.
            int reported = _reportedEffective.TryGetValue(peerId, out int r) ? r : 0;
            int newExtraDelay = System.Math.Min(System.Math.Max(rttBased, reported), _simConfig.MaxRollbackTicks / 2);

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
            var msg = _recommendedExtraDelayCache;
            msg.RecommendedExtraDelay = extraDelay;
            msg.AvgRttMs = avgRttMs;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
            _logger?.KDebug(
                $"[ServerNetworkService][DynamicDelay] Push: peerId={peerId}, prev={prevDelay}, new={extraDelay}, avgRtt={avgRttMs}ms");
            _logger?.KInformation(
                $"[Metrics][DynamicDelay] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"DynamicDelayPush\",\"avgRtt\":{avgRttMs},\"prevDelay\":{prevDelay},\"newDelay\":{extraDelay},\"reason\":\"{reason}\"}}");
        }

        // ── Handshake ─────────────────────────────────────

        private void HandlePeerConnected(int peerId)
        {
            _pendingPeers[peerId] = _nowProvider();
        }

        // Broadcast a host-confirmed player connection-state transition (disconnect / reconnect / leave)
        // to clients and spectators so their roster connection-state display stays consistent. State is
        // network metadata (not simulation), so recipients apply it display-only. Mirrors the P2P
        // BroadcastPlayerState path; the server has no Broadcast helper, so iterate both sets directly.
        private void BroadcastPlayerState(int playerId, PlayerStateChange state)
        {
            var msg = new PlayerStateNotificationMessage { PlayerId = playerId, State = (byte)state };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                foreach (var kvp in _peerToPlayer)
                    _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                for (int i = 0; i < _spectators.Count; i++)
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandlePeerDisconnected(int peerId)
        {
            _logger?.KInformation($"[ServerNetworkService] Peer disconnected: peerId={peerId}");

            _pendingPeers.Remove(peerId);

            // Unconditional: spectators and pending peers are not in _peerToPlayer, yet they are
            // rate-limited too. Leaving the entry behind would let a recycled peerId inherit the
            // previous peer's cooldown and get its FIRST request dropped (a spectator/late-joiner
            // would then stall until its resync timeout).
            _resyncState.Remove(peerId);
            _probeServeState.Remove(peerId);
            _verdictReportState.Remove(peerId);

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = FindPlayerById(playerId);
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
                            BroadcastPlayerState(playerId, PlayerStateChange.Disconnected);
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
                            _reportedEffective.Remove(peerId); // drop stale reactive report

                            // Flush any in-progress PastTick burst for the disconnecting peer.
                            if (_pastTickBursts.TryGetValue(peerId, out var pendingBurst))
                            {
                                EmitBurstDuration(peerId, pendingBurst);
                                _pastTickBursts.Remove(peerId);
                            }
                        }
                        else
                        {
                            // No reconnect slot available (pool exhausted) → treat as permanent leave.
                            int prevCount = _players.Count;
                            bool prevReady = AllPlayersReady;
                            _players.Remove(player);
                            _inputCollector.RemovePlayer(playerId);
                            _engine?.NotifyPlayerLeft(playerId);
                            OnPlayerLeft?.Invoke(player);
                            RaisePlayerCountIfChanged(prevCount);
                            RaiseAllPlayersReadyIfChanged(prevReady);
                            BroadcastPlayerState(playerId, PlayerStateChange.Left);
                        }
                    }
                    else
                    {
                        int prevCount = _players.Count;
                        bool prevReady = AllPlayersReady;
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                        RaisePlayerCountIfChanged(prevCount);
                        RaiseAllPlayersReadyIfChanged(prevReady);

                        // Notify remaining clients (and spectators) of the lobby leave. This server has no
                        // RelayMessage helper, so iterate _peerToPlayer directly, excluding the leaving peer
                        // (still present here; removed below). The server is always the authority.
                        var leaveNotification = new PlayerLeaveNotificationMessage { PlayerId = playerId };
                        using (var serialized = _messageSerializer.SerializePooled(leaveNotification))
                        {
                            foreach (var kvp in _peerToPlayer)
                            {
                                if (kvp.Key != peerId)
                                    _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                            }
                            for (int i = 0; i < _spectators.Count; i++)
                                _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                        }

                        // A not-ready player leaving can make the remaining roster all-ready.
                        // Re-evaluate the start condition here so the room is not stuck waiting
                        // for another PlayerReadyMessage (the only other trigger).
                        int minStartPlayers = Math.Min(_sessionConfig.MinPlayers, MaxPlayersPerRoom);
                        if (!_gameStarted && AllPlayersReady && _players.Count >= minStartPlayers)
                            StartGame();
                    }
                }
                _peerToPlayer.Remove(peerId);
            }

            // Proactive evict of any in-flight identity validation. Runs unconditionally (pending peers
            // are not in _peerToPlayer). Dispose signals the validator to cancel the in-flight redeem
            // before it burns the lobby nonce.
            if (_pendingValidation.TryGetValue(peerId, out var pendingV))
            {
                pendingV.Handle.Dispose();
                _pendingValidation.Remove(peerId);
            }

            _peerStates.Remove(peerId);
            _peerSyncStates.Remove(peerId);
            _peerDeviceIds.Remove(peerId);
            _peerClaimedDisplayNames.Remove(peerId);
            _peerTickets.Remove(peerId);
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
                && Phase != SessionPhase.Playing && Phase != SessionPhase.Countdown)
            {
                Phase = SessionPhase.Lobby;
            }
        }

        private void StartHandshake(int peerId)
        {
            _logger?.KInformation($"[ServerNetworkService] Handshake started: peerId={peerId}");

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
            // AwaitingValidation: sync phase is done, async validation pending — ignore stray replies.
            if (state.Completed || state.AwaitingValidation || msg.Sequence != state.SyncPacketsSent || msg.Attempt != state.Attempt)
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
                _logger?.KWarning($"[ServerNetworkService] Standard handshake completed after GameStart (race): peer={peerId}, dropping for LateJoin retry");
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                _peerSyncStates.Remove(peerId);
                return;
            }

            // Identity validation gate — before slot reservation so a reject consumes no slot.
            // Handles all outcomes (no-validator fallback / sync accept / sync reject / async park);
            // FinalizeNormalJoin runs the rest of the join (inline now, or from the poll-drain later).
            RunJoinValidation(peerId, state, isLateJoin: false);
        }

        // ── Identity validation hook + pending machinery ─────────────────────────────────────────

        // Shared validation entry for all SD join-completion sites. Caller returns immediately after.
        //   validator == null → finalize with the fallback identity (unchanged behaviour).
        //   sync-complete handle → accept (finalize) or reject (disconnect) inline.
        //   incomplete handle    → park in _pendingValidation; the poll-drain completes it later (SD only).
        private void RunJoinValidation(int peerId, PeerSyncState state, bool isLateJoin)
        {
            if (_identityValidator == null)
            {
                FinalizeJoinDispatch(peerId, state, isLateJoin, validatorRan: false, account: null, displayName: null, entitlement: null);
                return;
            }

            // Re-entry guard: a duplicate sync completion must not start a second validation.
            if (_pendingValidation.ContainsKey(peerId))
                return;

            var handle = _identityValidator.BeginValidate(BuildIdentityRequest(peerId, isLateJoin));
            if (handle.IsComplete)
            {
                var outcome = handle.Outcome;
                handle.Dispose();
                ApplyValidationOutcome(peerId, state, isLateJoin, outcome);
                return;
            }

            // Async redeem in flight → park. Slot not reserved; Completed stays false so the peer still
            // counts toward capacity while parked.
            state.AwaitingValidation = true;
            _pendingValidation[peerId] = new PendingValidation(handle, state, isLateJoin,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private IdentityValidationRequest BuildIdentityRequest(int peerId, bool isLateJoin)
        {
            string ticket = _peerTickets.TryGetValue(peerId, out var t) ? t : string.Empty;
            string claimed = _peerClaimedDisplayNames.TryGetValue(peerId, out var c) ? c : string.Empty;
            string deviceId = _peerDeviceIds.TryGetValue(peerId, out var d) ? d : string.Empty;
            return new IdentityValidationRequest(ticket, claimed, _sessionMagic, peerId, deviceId,
                isLateJoin, isHostSelf: false, roomId: _roomId);
        }

        private void ApplyValidationOutcome(int peerId, PeerSyncState state, bool isLateJoin, IdentityValidationOutcome outcome)
        {
            // Reject on a validator reject, OR an accepted-but-over-bound account (>62B would truncate in the
            // roster field → identity collision). Empty account is allowed (Accept contract permits empty).
            if (!outcome.Accepted || RosterEntry.IsAccountOverBound(outcome.Account))
            {
                state.AwaitingValidation = false;
                byte code = outcome.Accepted
                    ? JoinFailReason.IdentityInvalid.ToWireCode()
                    : JoinFailReasonExtensions.ClampIdentityWireCode(outcome.RejectWireCode);
                if (outcome.Accepted)
                    // Accepted-but-over-bound: the validator OK'd this identity but its account would truncate
                    // in the 62-byte roster field (→ identity collision), so the join is refused. Log it —
                    // otherwise a consumer whose validator emits long accounts without its own length guard
                    // sees only an opaque code-6 disconnect for users that previously joined (truncated).
                    _logger?.KWarning($"[ServerNetworkService] Join rejected: validator-accepted account exceeds the 62 UTF-8 byte roster bound (peer={peerId}) — refused to avoid identity-collision truncation.");
                DisconnectWithReason(peerId, code);
                _peerSyncStates.Remove(peerId);
                return;
            }
            FinalizeJoinDispatch(peerId, state, isLateJoin, validatorRan: true, outcome.Account, outcome.DisplayName, outcome.Entitlement);
        }

        private void FinalizeJoinDispatch(int peerId, PeerSyncState state, bool isLateJoin, bool validatorRan, string account, string displayName, byte[] entitlement)
        {
            if (isLateJoin) FinalizeLateJoin(peerId, state, validatorRan, account, displayName, entitlement);
            else            FinalizeNormalJoin(peerId, state, validatorRan, account, displayName, entitlement);
        }

        // Resolves the display name by priority: validated value > claimed name (only when no validator
        // ran) > fabricated default. The fabricated default needs the reserved playerId, so it is resolved
        // here (after slot reservation), not in the validator.
        private string ResolveJoinDisplayName(int peerId, int newPlayerId, bool validatorRan, string validatedDisplayName)
        {
            if (validatorRan)
                return string.IsNullOrEmpty(validatedDisplayName) ? $"Player{newPlayerId}" : validatedDisplayName; // claimed name ignored once verified
            var claimed = _peerClaimedDisplayNames.TryGetValue(peerId, out var c) ? c : string.Empty;
            return !string.IsNullOrEmpty(claimed) ? claimed : $"Player{newPlayerId}";
        }

        // Drains pending async validations once per tick (called from Update, after DrainInboundQueue so a
        // same-tick disconnect has already evicted). Completed → accept/reject; timed out → reject.
        private void DrainPendingValidations(long now)
        {
            if (_pendingValidation.Count == 0) return;
            // Snapshot keys — completion mutates _players/_peerToPlayer and the dict.
            _pendingValidationDrainKeys.Clear();
            foreach (var kvp in _pendingValidation)
                _pendingValidationDrainKeys.Add(kvp.Key);

            for (int i = 0; i < _pendingValidationDrainKeys.Count; i++)
            {
                int peerId = _pendingValidationDrainKeys[i];
                if (!_pendingValidation.TryGetValue(peerId, out var pending))
                    continue; // already finalized/evicted earlier this drain

                if (pending.Handle.IsComplete)
                {
                    var outcome = pending.Handle.Outcome;
                    pending.Handle.Dispose();
                    _pendingValidation.Remove(peerId);
                    pending.State.AwaitingValidation = false;
                    ApplyValidationOutcome(peerId, pending.State, pending.IsLateJoin, outcome);
                }
                else if (now - pending.BeginMs > _sessionConfig.ValidationTimeoutMs)
                {
                    pending.Handle.Dispose();
                    _pendingValidation.Remove(peerId);
                    pending.State.AwaitingValidation = false;
                    DisconnectWithReason(peerId, JoinFailReason.IdentityValidationFailed.ToWireCode());
                    _peerSyncStates.Remove(peerId);
                }
            }
        }

        private void FinalizeNormalJoin(int peerId, PeerSyncState state, bool validatorRan, string account, string displayName, byte[] entitlement)
        {
            // A pending validation may complete after StartGame() flipped _gameStarted. Re-evaluate the
            // race guard at finalize time (the inline caller already passed it, so this is a no-op there).
            if (_gameStarted)
            {
                _logger?.KWarning($"[ServerNetworkService] Validation completed after GameStart (race): peer={peerId}, dropping for LateJoin retry");
                state.AwaitingValidation = false;
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                _peerSyncStates.Remove(peerId);
                return;
            }

            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;
            state.AwaitingValidation = false;

            state.GetBestSample(out int avgRtt, out long avgOffset);

            _peerToPlayer[peerId] = newPlayerId;

            // Identity: validated value when a validator ran (claimed name ignored), else
            // the claimed name, else a fabricated fallback. Account stays empty unless validated.
            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                DisplayName = ResolveJoinDisplayName(peerId, newPlayerId, validatorRan, displayName),
                Account = validatorRan ? (account ?? string.Empty) : string.Empty,
                Entitlement = validatorRan ? entitlement : null, // server-only; null when no validator ran
                IsReady = false,
                Ping = avgRtt
            };
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
            if (newPlayer.Entitlement != null && newPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[ServerNetworkService][Entitlement] loaded via NormalJoin: playerId={newPlayerId}, bytes={newPlayer.Entitlement.Length}");

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
            // Attach the pre-game roster snapshot (including the just-added newPlayer) so the joining
            // client builds its full player list immediately. KlothoConnection forwards these into
            // ConnectionResult, then ServerDrivenClientService.InitializeFromConnection rebuilds the list.
            for (int i = 0; i < _players.Count; i++)
            {
                syncComplete.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger, (byte)_players[i].ConnectionState,
                    _players[i].IsReady ? (byte)1 : (byte)0));
            }
            // Per-player server-verified entitlement bytes, index-parallel to the Roster built above (same
            // _players order). Lets tick-0 SD clients read GetPlayerEntitlement for initial-entry players.
            syncComplete.RosterEntitlementData = BuildRosterEntitlements(syncComplete.RosterEntitlementLengths);
            // Seed push baseline with the handshake-time value sent in SyncCompleteMessage so the
            // first MaybePushExtraDelayUpdate compares against the value the client already applied.
            _lastPushedExtraDelay[peerId] = seedExtraDelay;
            _reportedEffective.Remove(peerId); // seed resets client reactive → drop any stale report
            using (var serialized = _messageSerializer.SerializePooled(syncComplete))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // Propagate SimulationConfig to SD client (host authority model)
            SendSimulationConfig(peerId);

            // Notify existing clients (and spectators) of the new player. This server has no RelayMessage
            // helper, so iterate _peerToPlayer directly. The new player's peer is already registered above,
            // so exclude it; it received the full roster via the SyncCompleteMessage.
            var joinNotification = new PlayerJoinNotificationMessage
            {
                PlayerId = newPlayerId,
                ConnectionState = (byte)newPlayer.ConnectionState,
                IsReady = newPlayer.IsReady,
                Account = newPlayer.Account ?? string.Empty,
                DisplayName = newPlayer.DisplayName ?? string.Empty,
                // Server-verified entitlement so connected SD clients set this player's entitlement for
                // GetPlayerEntitlement reads (null when no validator ran).
                Entitlement = newPlayer.Entitlement,
            };
            using (var serialized = _messageSerializer.SerializePooled(joinNotification))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key != peerId)
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
                for (int i = 0; i < _spectators.Count; i++)
                    _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
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
                _logger?.KWarning($"[ServerNetworkService] Spectator rejected: count={_spectators.Count}, max={_maxSpectatorsPerRoom}");
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
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
            {
                acceptMsg.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger, (byte)_players[i].ConnectionState,
                    _players[i].IsReady ? (byte)1 : (byte)0));
            }

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

                    var player = FindPlayerById(playerId);
                    if (player != null)
                    {
                        int prevCount = _players.Count;
                        bool prevReady = AllPlayersReady;
                        _players.Remove(player);
                        _inputCollector.RemovePlayer(playerId);
                        _engine?.NotifyPlayerLeft(playerId);
                        OnPlayerLeft?.Invoke(player);
                        RaisePlayerCountIfChanged(prevCount);
                        RaiseAllPlayersReadyIfChanged(prevReady);
                        BroadcastPlayerState(playerId, PlayerStateChange.Left);
                    }
                }
            }
        }

        // ── Suppress unused event warnings ─────────────────────────

        internal void SuppressWarnings()
        {
            OnCommandReceived?.Invoke(null);
            OnDesyncDetected?.Invoke(0, 0, 0, 0);
            OnSyncHashCompared?.Invoke(0, 0, false);
            OnResyncFailureReported?.Invoke(0, 0);
            OnMatchAbortReceived?.Invoke(0);
            OnFrameAdvantageReceived?.Invoke(0, 0, 0);
            OnLocalPlayerIdAssigned?.Invoke(0);
            OnFullStateReceived?.Invoke(0, null, 0, FullStateKind.Unicast);
            OnReconnecting?.Invoke();
            OnReconnectFailed?.Invoke(ReconnectRejectReason.Unknown);
            OnReconnected?.Invoke();
            OnCountdownStarted?.Invoke(0);
            OnVerifiedStateReceived?.Invoke(0, null, 0);
            OnInputAckReceived?.Invoke(0);
            OnServerFullStateReceived?.Invoke(0, null, 0);
            OnBootstrapBegin?.Invoke(0, 0);
            OnCommandRejected?.Invoke(0, 0, default);
        }

        private readonly byte[] _disconnectReasonBuf = new byte[1];

        // Reject a peer by disconnecting with the reason carried on the disconnect packet. A separate
        // reliable reject message would be dropped because the immediately following disconnect halts
        // the peer's send flush.
        private void DisconnectWithReason(int peerId, byte reason)
        {
            _disconnectReasonBuf[0] = reason;
            _transport.DisconnectPeer(peerId, _disconnectReasonBuf);
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
        //   On reject: DisconnectWithReason(RoomFull=2) + immediate _peerSyncStates.Remove —
        //   the transport disconnect is async; without explicit removal, the stale entry keeps counting.
        private bool TryReservePlayerSlot(int peerId, out int newPlayerId)
        {
            if (!_gameStarted)
            {
                newPlayerId = FindSmallestUnusedPlayerId();
                if (newPlayerId < 0)
                {
                    _logger?.KError($"[ServerNetworkService] FindSmallestUnusedPlayerId returned -1: peer={peerId}, players={_players.Count}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                    DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
            }
            else
            {
                if (Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1) >= MaxPlayerCapacity)
                {
                    _logger?.KError($"[ServerNetworkService] Post-GameStart slot overflow: assigned={_assignedPlayerIdCount}, nextId={_nextPlayerId}, max={MaxPlayerCapacity}, peer={peerId}");
                    DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                    _peerSyncStates.Remove(peerId);
                    newPlayerId = -1;
                    return false;
                }
                newPlayerId = _nextPlayerId++;
                _assignedPlayerIdCount++;
            }
            return true;
        }

        // Parked async identity validation. BeginMs is settable so tests can backdate it to trigger the
        // timeout deterministically without a real wait.
        private sealed class PendingValidation
        {
            public readonly IIdentityValidation Handle;
            public readonly PeerSyncState State;
            public readonly bool IsLateJoin;
            public long BeginMs;

            public PendingValidation(IIdentityValidation handle, PeerSyncState state, bool isLateJoin, long beginMs)
            {
                Handle = handle;
                State = state;
                IsLateJoin = isLateJoin;
                BeginMs = beginMs;
            }
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
