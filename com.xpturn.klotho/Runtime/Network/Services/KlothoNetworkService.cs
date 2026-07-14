using System;
using xpTURN.Klotho.Logging;
using System.Buffers;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Player info implementation.
    /// </summary>
    public class PlayerInfo : IPlayerInfo
    {
        public int PlayerId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Account { get; set; } = "";
        public bool IsReady { get; set; }
        public int Ping { get; set; }
        public PlayerConnectionState ConnectionState { get; set; }

        // Server-only opaque entitlement blob: not on IPlayerInfo, so it never leaks onto the roster wire.
        // Set at join (authoritative redeem outcome), rides the PlayerInfo lifetime (so it is preserved
        // across disconnect and cleared with the player on evict — no separate bookkeeping). Null when no
        // entitlement is used. Read by the player-config entitlement guard and the tick-0 seed path.
        public byte[] Entitlement { get; set; }

        // P2P host-only: the player's ORIGINAL lobby-signed ticket (the opaque string the peer presented),
        // captured at join. Propagated to all peers so each guest can
        // independently re-verify the signature instead of trusting the host-relayed Account/DisplayName.
        // Like Entitlement it is not on IPlayerInfo (never on the roster wire) and rides the PlayerInfo
        // lifetime (preserved across disconnect for reconnect re-propagation). Empty when the P2P
        // original-ticket propagation gate is off. Host-side only — guests rebuild their roster from the
        // wire and re-verify each receipt, so they do not retain this.
        public string OriginalTicket { get; set; } = "";
    }

    /// <summary>
    /// Per-spectator connection state.
    /// </summary>
    internal sealed class SpectatorInfo
    {
        public int SpectatorId;
        public int PeerId;
        public int LastSentTick = -1;
    }

    /// <summary>
    /// Disconnected-player info (supports reconnect, pooled).
    /// </summary>
    internal sealed class DisconnectedPlayerInfo
    {
        public int PlayerId;
        public int PeerId;
        public long DisconnectTimeMs;
        public int LastConfirmedTick;
        public int PredictedTickCount;
        public string DeviceId;
        // RTT sample captured at disconnect time. Used by SD Reconnect path
        // (ServerNetworkService.HandleReconnectRequest) to seed RecommendedExtraDelay computation
        // when no fresh handshake samples are available — the new connection's peerId differs from
        // the disconnected one, so PeerSyncStates lookup misses. P2P does not consume this field;
        // KlothoNetworkService's Reconnect path does not currently set RecommendedExtraDelay.
        public int LastAvgRtt;
        // True when this entry was added by the quorum-miss watchdog before transport-level
        // detection. Cleared once transport disconnect confirms or real input arrival rolls back.
        public bool IsPresumedDrop;

        public bool IsActive => PlayerId != 0;

        public void Reset()
        {
            PlayerId = 0;
            PeerId = 0;
            DisconnectTimeMs = 0;
            LastConfirmedTick = 0;
            PredictedTickCount = 0;
            DeviceId = null;
            LastAvgRtt = 0;
            IsPresumedDrop = false;
        }
    }

    internal class LateJoinCatchupInfo
    {
        public int PeerId;
        public int PlayerId;
        public int LastSentTick;
        public int JoinTick;

        /// <summary>
        /// Cold-start Reconnect uses the same catchup mechanism as LateJoin, but with two differences —
        /// (a) PlayerJoinCommand is NOT inserted (existing PlayerId), (b) JoinTick = _engine.CurrentTick (immediate).
        /// </summary>
        public bool IsReconnect;
    }

    /// <summary>
    /// Per-peer handshake state.
    /// </summary>
    internal class PeerSyncState
    {
        public int PeerId;
        public int SyncPacketsSent;
        public long[] RttSamples;
        public long[] ClockOffsetSamples;
        public long LastSyncSentTime;
        public int Attempt;
        public bool Completed;
        public bool IsLateJoin;
        // SD identity validation: set while a pending async validation is in flight (sync done, slot not
        // yet reserved, Completed still false). The handshake-timeout sweep and SyncReply handler skip an
        // AwaitingValidation state, while CountPendingHandshakes still counts it toward capacity. P2P
        // leaves it false (validation there is synchronous, so it never parks).
        public bool AwaitingValidation;

        public void GetBestSample(out int rtt, out long offset)
        {
            int minIdx = 0;
            for (int i = 1; i < SyncPacketsSent; i++)
            {
                if (RttSamples[i] < RttSamples[minIdx])
                    minIdx = i;
            }
            rtt = (int)RttSamples[minIdx];
            offset = ClockOffsetSamples[minIdx];
        }
    }

    /// <summary>
    /// Lockstep network service implementation.
    /// </summary>
    public partial class KlothoNetworkService : IKlothoNetworkService
    {
        private const int NUM_SYNC_PACKETS = 5;
        private const int SYNC_TIMEOUT_MS = 5000;
        private const int PING_INTERVAL_MS = 1000;

        private IKLogger _logger;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();
        private readonly Dictionary<int, int> _peerToPlayer = new Dictionary<int, int>();
        // Value = (state hash, command digest). The command digest lets CompareAndReportSyncHash
        // classify a mismatch as input vs state divergence without any recovery-path involvement.
        private readonly Dictionary<(int tick, int playerId), (long state, long cmd)> _syncHashes = new Dictionary<(int tick, int playerId), (long, long)>();
        private readonly Dictionary<int, PeerSyncState> _peerSyncStates = new Dictionary<int, PeerSyncState>();
        private readonly HashSet<int> _pendingPeers = new HashSet<int>();
        private readonly Dictionary<int, long> _peerConnectedAtMs = new Dictionary<int, long>();
        private readonly List<int> _zombieScanSnapshot = new List<int>();
        private readonly List<SpectatorInfo> _spectators = new List<SpectatorInfo>();
        private int _nextSpectatorId = -1;
        private int _nextPlayerId;

        // Phase-branched player count accounting
        private bool _gameStarted;
        private int _assignedPlayerIdCount;

        private IKlothoEngine _engine;
        private ISessionConfig _sessionConfig;
        private ISimulationConfig _simConfig;
        private IReconnectCredentialsStore _reconnectCredentialsStore;
        private string _appVersion;
        private IDeviceIdProvider _deviceIdProvider;
        // Authority-side ticket validator (P2P host). null = no validation (behaviour unchanged).
        // P2P validation is offline and synchronous — the returned handle must be immediately complete;
        // the P2P host keeps no pending-validation queue or drain.
        private IPlayerIdentityValidator _identityValidator;
        // Optional guest-side signature-only re-verifier of propagated original tickets.
        // Companion to _identityValidator, NOT a method on it (SD never re-verifies). The same P2P
        // validator object typically implements both. null = no re-verification capability.
        private IPropagatedTicketVerifier _propagatedTicketVerifier;
        // The SINGLE gate for original-ticket propagation + per-peer re-verification. When
        // false (default), host does NOT populate OriginalTicket/RosterTickets and guests skip
        // re-verification, i.e. semi-trust. This is enabled when the P2P entitlement
        // hook is configured (the entitlement hook is itself the gate), with a fail-closed guard that a
        // _propagatedTicketVerifier is present (else host-relay entitlement would be blindly trusted —
        // an entitlement-forgery cheat). Read identically by host populate, guest re-verify, and GameStart-cache paths.
        private bool _propagateOriginalTickets;
        // Optional cross-check that clamps a client's PlayerConfig selection against the player's verified
        // entitlement. In P2P every peer runs it independently, clamping the relayed config with its own
        // verified entitlement; because every peer holds the same signed bytes the result is deterministic.
        // Unset leaves selections untouched. The dedicated server uses the same seam, but P2P never rewrites
        // the relayed message — it relays the raw config (see HandlePlayerConfigMessage).
        private IPlayerConfigEntitlementGuard _entitlementGuard;
        // The host's own lobby ticket, self-validated when the host adds itself in CreateRoom. Empty when no lobby.
        private string _localIdentityTicket = string.Empty;
        private readonly Dictionary<int, string> _peerDeviceIds = new Dictionary<int, string>();
        // Per-peer claimed display name captured at PlayerJoinMessage receipt and read later by
        // CompletePeerSync. Same lifecycle as _peerDeviceIds (removed on disconnect).
        private readonly Dictionary<int, string> _peerClaimedDisplayNames = new Dictionary<int, string>();
        // Per-peer lobby ticket captured at PlayerJoinMessage receipt and read later by the validation
        // hook. Same lifecycle as _peerClaimedDisplayNames (removed on disconnect).
        private readonly Dictionary<int, string> _peerTickets = new Dictionary<int, string>();

        // Pending extra-delay seed buffered between InitializeFromConnection and SubscribeEngine.
        // Guest path: _engine is wired only at SubscribeEngine, so the seed value forwarded by
        // KlothoConnection (Sync scalar / LateJoin+Reconnect Payload.AcceptMessage) must be held here
        // until the engine is available, then flushed exactly once via ApplyExtraDelay.
        private int? _pendingExtraDelayValue;
        private ExtraDelaySource _pendingExtraDelaySource;

        // Cached list (GC avoidance)
        private readonly List<(int tick, int playerId)> _hashKeysToRemoveCache = new List<(int tick, int playerId)>();

        // Cached scratch for preserving DisplayName across the GameStart roster rebuild — one-shot per match, reused.
        private readonly List<string> _gameStartNameCache = new List<string>();
        // Parallel cache preserving Account across the GameStart roster rebuild.
        private readonly List<string> _gameStartAccountCache = new List<string>();
        // Parallel cache preserving host-only PlayerInfo.OriginalTicket across the
        // GameStart roster rebuild (_players.Clear()+rebuild drops it otherwise → post-GameStart
        // late-join/reconnect would propagate empty tickets). Snapshots ALL players incl. host-self.
        private readonly List<string> _gameStartTicketCache = new List<string>();
        // Parallel cache preserving PlayerInfo.Entitlement across the GameStart roster rebuild, mirroring the
        // ticket cache. Without it the rebuild would drop each peer's entitlement, so a post-GameStart
        // late-join or reconnect would carry an empty entitlement, the joining guest's seed would be missing,
        // and the match would desync. It snapshots every player including the host, holding the byte[] by
        // reference (no copy).
        private readonly List<byte[]> _gameStartEntitlementCache = new List<byte[]>();

        // Cached message objects (GC avoidance)
        private readonly CommandMessage _commandMessageCache = new CommandMessage();
        private readonly SyncHashMessage _syncHashMessageCache = new SyncHashMessage();
        private readonly PingMessage _pingMessageCache = new PingMessage();
        private readonly PongMessage _pongMessageCache = new PongMessage();
        private readonly PlayerJoinMessage _playerJoinMessageCache = new PlayerJoinMessage();
        private readonly SpectatorInputMessage _spectatorInputMessageCache = new SpectatorInputMessage();
        private readonly ReconnectRequestMessage _reconnectRequestCache = new ReconnectRequestMessage();
        private readonly ReconnectAcceptMessage _reconnectAcceptCache = new ReconnectAcceptMessage();
        private readonly ReconnectRejectMessage _reconnectRejectCache = new ReconnectRejectMessage();
        private readonly RecommendedExtraDelayUpdateMessage _recommendedExtraDelayCache = new RecommendedExtraDelayUpdateMessage();
        private readonly ReactiveExtraDelayReportMessage _reactiveExtraDelayCache = new ReactiveExtraDelayReportMessage();

        private long _sessionMagic;
        private SharedTimeClock _sharedClock;
        public SharedTimeClock SharedClock => _sharedClock;
        private SessionPhase _phase;
        private long _gameStartTime; // Absolute game start time on the SharedNow timeline
        private long _lastPingTime;
        private int _pingSequence;

        public int MaxPlayers { get; private set; }
        public int MaxPlayerCapacity => MaxPlayers;
        public string RoomName { get; private set; }
        public int PlayerCount => _players.Count;
        public int SpectatorCount => _spectators.Count;
        public int PendingLateJoinCatchupCount => _lateJoinCatchups.Count;
        public bool AllPlayersReady => _players.TrueForAll(p => p.IsReady);
        public int LocalPlayerId { get; private set; }
        public bool IsHost { get; private set; }
        public int RandomSeed { get; private set; }
        public IReadOnlyList<IPlayerInfo> Players => _players;

        /// <summary>
        /// The independently-verified entitlement bytes this peer extracted and re-verified from the player's
        /// propagated ticket, or null. It is deliberately not on <see cref="IPlayerInfo"/> so it never leaks
        /// onto the roster wire; the engine reaches it through a concrete cast to read the P2P seed. The bytes
        /// are used directly with no re-derivation, and they are identical on every peer, so a seed computed
        /// from them locally is deterministic.
        /// </summary>
        public byte[] GetPlayerEntitlement(int playerId) => FindPlayerById(playerId)?.Entitlement;

        // Capture-free equivalent of _players.Find(p => p.PlayerId == id) (no closure allocation).
        private PlayerInfo FindPlayerById(int playerId)
        {
            for (int i = 0; i < _players.Count; i++)
                if (_players[i].PlayerId == playerId)
                    return _players[i];
            return null;
        }

        public SessionPhase Phase
        {
            get
            {
                return _phase;
            }

            set
            {
                var prev = _phase;
                _phase = value;
                if (value == SessionPhase.Disconnected || value == SessionPhase.Lobby)
                {
                    // Disconnected = teardown signal, Lobby = fresh session entry.
                    // Guests do not use these counters (host-only) but the reset is harmless and keeps
                    // setter semantics symmetric with the SD ServerNetworkService.
                    _gameStarted = false;
                    _assignedPlayerIdCount = 0;
                    _nextPlayerId = 1;

                    // Emit PresumedDrop summary on match end transition (Playing → end).
                    if (prev == SessionPhase.Playing)
                    {
                        EmitPresumedDropMetrics(IsHost ? "host" : "guest", LocalPlayerId);
                    }
                }
                _logger?.KInformation($"[KlothoNetworkService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");

                if (prev != value)
                    OnPhaseChanged?.Invoke(value);
            }
        }

        private int _localTick;

        // Sender's measured frame-advantage (round(CalculateLocalAdvantage)), pushed
        // each tick from the engine and stamped into CommandMessage.SenderAdvantage.
        private int _localAdvantage;

        // Set immediately before a proxy-fill SendCommand (host filling for a
        // disconnected / catching-up player) so the broadcast carries IsProxyTiming and receivers
        // skip the timing vote. Consumed (cleared) inside SendCommand.
        private bool _proxyTimingPending;

        // CommandMessage.TimingFlags bit0.
        private const byte TIMING_FLAG_PROXY = 1;

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

        public void SetLocalTick(int tick) { _localTick = tick; }

        public void SetLocalAdvantage(int advantage) { _localAdvantage = advantage; }

        public void SubscribeEngine(IKlothoEngine engine)
        {
            _engine = engine;
            _sessionConfig = engine.SessionConfig;
            _simConfig = engine.SimulationConfig;
            engine.OnVerifiedInputBatchReady += HandleVerifiedInputBatchReady;
            engine.OnDisconnectedInputNeeded += HandleDisconnectedInputNeeded;
            engine.OnCatchupComplete += HandleCatchupComplete;
            engine.OnExtraDelayChanged += HandleEngineExtraDelayChanged;

            // Flush any extra-delay seed buffered during InitializeFromConnection (guest path).
            // Single-slot — re-entry (e.g. warm reconnect re-running InitializeFromConnection) refills with the new value.
            if (_pendingExtraDelayValue.HasValue)
            {
                _engine.ApplyExtraDelay(_pendingExtraDelayValue.Value, _pendingExtraDelaySource);
                _pendingExtraDelayValue = null;
            }

            // Flush the joinTick reconstructed in SeedLateJoinPlayers (called before the engine exists)
            // as the engine's cmd.Tick floor: the late-joiner's own commands must not target a tick
            // before the PlayerJoinCommand that seeds its join-time state. Concrete-cast like
            // GetLocalStaticFingerprint — keeps the floor off the canonical IKlothoEngine surface.
            if (_lateJoinJoinTick > 0)
                (_engine as KlothoEngine)?.SetLateJoinCommandFloor(_lateJoinJoinTick);
        }

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _emptyCommandCache = _commandFactory.CreateEmptyCommand();
            _sessionConfig = new SessionConfig();  // Default. Replaced in SubscribeEngine().
            _simConfig = new SimulationConfig();   // Default. Replaced in SubscribeEngine().

            // Wire up network events
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        /// <summary>
        /// Inject the cold-start Reconnect credentials store. Optional — when null, cold-start
        /// credentials are not persisted. Game boot wires this with PlayerPrefsReconnectCredentialsStore.
        /// </summary>
        public void SetReconnectCredentialsStore(IReconnectCredentialsStore store, string appVersion, IDeviceIdProvider deviceIdProvider = null)
        {
            _reconnectCredentialsStore = store;
            _appVersion = appVersion;
            _deviceIdProvider = deviceIdProvider;
        }

        private string GetDeviceId() => _deviceIdProvider?.GetDeviceId() ?? string.Empty;

        /// <summary>
        /// Inject the authority-side ticket validator (P2P host). null = no validation (behaviour unchanged).
        /// </summary>
        public void SetIdentityValidator(IPlayerIdentityValidator validator)
        {
            _identityValidator = validator;
        }

        /// <summary>
        /// Injects the optional guest-side propagated-ticket re-verifier. Typically the
        /// same object as the identity validator. null = no re-verification capability.
        /// </summary>
        public void SetPropagatedTicketVerifier(IPropagatedTicketVerifier verifier)
        {
            _propagatedTicketVerifier = verifier;
        }

        /// <summary>
        /// Injects the optional player-config entitlement guard that cross-checks and clamps a client's
        /// selection. A null guard leaves behaviour unchanged. In P2P every peer holds the guard and clamps
        /// the relayed config locally, which is deterministic because every peer shares the same signed bytes.
        /// </summary>
        public void SetPlayerConfigEntitlementGuard(IPlayerConfigEntitlementGuard guard)
        {
            _entitlementGuard = guard;
        }

        /// <summary>
        /// Enables original-ticket propagation + per-peer re-verification (the single gate).
        /// Off by default, i.e. semi-trust. Enabled when the P2P entitlement hook
        /// is configured. Fail-closed: enabling without a re-verifier would let the
        /// host-relay entitlement be trusted blindly (an entitlement-forgery cheat), so it is refused (gate stays off + log).
        /// </summary>
        public void SetOriginalTicketPropagation(bool enabled)
        {
            if (enabled && _propagatedTicketVerifier == null)
            {
                _logger?.KError($"[KlothoNetworkService] Original-ticket propagation requested without an IPropagatedTicketVerifier — refused (host-relay entitlement would be trusted blindly). Gate stays off.");
                _propagateOriginalTickets = false;
                return;
            }
            _propagateOriginalTickets = enabled;
        }

        /// <summary>
        /// Guest-side independent re-verification of a propagated original ticket.
        /// When the gate is on and a non-empty ticket is present, re-verify signature-only and ADOPT
        /// the ticket-derived Account/DisplayName (replace — the host-relayed roster value is untrusted);
        /// on a host-relay mismatch, log (host-forge detection). On signature failure the identity
        /// is host-forged: surface it (log) and KEEP the host-relayed values — do NOT silently drop the
        /// player (a roster drop itself diverges). When entitlement is in play the rejected trusted data leaves
        /// the seed missing → desync abort; without entitlement there is no seed path.
        /// No-op (host-relayed values unchanged) when the gate is off / no verifier / empty ticket.
        /// Caller skips its OWN entry (the guest already knows its own ticket).
        /// </summary>
        private void ReverifyAndAdoptIdentity(int playerId, string originalTicket, ref string account, ref string displayName, ref byte[] entitlement)
        {
            if (!_propagateOriginalTickets || _propagatedTicketVerifier == null || string.IsNullOrEmpty(originalTicket))
                return;
            var outcome = _propagatedTicketVerifier.ReverifyPropagatedTicket(originalTicket);
            if (!outcome.Accepted)
            {
                _logger?.KError($"[KlothoNetworkService] Propagated ticket re-verification FAILED for playerId={playerId} — host-forged identity (match integrity violated). Keeping host-relayed value; trusted entitlement data rejected.");
                return;
            }
            if (account != outcome.Account || displayName != outcome.DisplayName)
                _logger?.KWarning($"[KlothoNetworkService] Host-relayed identity for playerId={playerId} differs from re-verified ticket — adopting ticket value (host may have tampered the roster).");
            account = outcome.Account ?? string.Empty;
            displayName = outcome.DisplayName ?? string.Empty;
            // Adopt the entitlement extracted from the same signature-verified ticket. Every peer derives
            // identical bytes from the same propagated ticket via signature-only re-verification, so peers
            // agree on a player's entitlement without trusting the host's relay. This stays null when the
            // signature check above fails: there is no trusted entitlement to fall back to, and the missing
            // seed surfaces as a desync rather than a silent default.
            entitlement = outcome.Entitlement;
        }

        /// <summary>
        /// The host's own lobby ticket, validated when the host adds itself to the roster. Empty when no lobby.
        /// </summary>
        public void SetLocalIdentityTicket(string ticket)
        {
            _localIdentityTicket = ticket ?? string.Empty;
        }

        // Validates the host's own ticket as it adds itself to the roster. Synchronous (P2P is offline).
        // The host is NEVER rejected: on reject, a contract-violating pending handle, or an empty result,
        // keep the "Host" fallback and log. Validated values overlay the defaults.
        private void ResolveHostSelfIdentity(ref string displayName, ref string account, ref byte[] entitlement)
        {
            // The host's own entitlement is extracted with a signature-only re-verification — the same path
            // guests use on the propagated host ticket — rather than from the enforcing Evaluate below. That
            // matters because Evaluate early-returns on an expiry, nonce, or sessionId reject (keeping the
            // "Host" fallback), which would leave the host's entitlement empty locally while guests still
            // extract it from the same propagated ticket. The host's tick-0 seed would then disagree with the
            // guests' view and desync. Extracting it independently here keeps them consistent.
            entitlement = null;
            if (_propagateOriginalTickets && _propagatedTicketVerifier != null && !string.IsNullOrEmpty(_localIdentityTicket))
            {
                var entOutcome = _propagatedTicketVerifier.ReverifyPropagatedTicket(_localIdentityTicket);
                if (entOutcome.Accepted)
                    entitlement = entOutcome.Entitlement;
            }

            var request = new IdentityValidationRequest(_localIdentityTicket, string.Empty, _sessionMagic,
                LocalPlayerId, GetDeviceId(), isLateJoin: false, isHostSelf: true, roomId: -1);
            using (var handle = _identityValidator.BeginValidate(request))
            {
                if (!handle.IsComplete)
                {
                    _logger?.KWarning($"[KlothoNetworkService] Host self-validation returned a pending handle (must be synchronous) — keeping \"Host\" fallback");
                    return;
                }
                var outcome = handle.Outcome;
                if (!outcome.Accepted)
                {
                    // The host cannot be kicked from its own session — log and keep the fallback.
                    _logger?.KWarning($"[KlothoNetworkService] Host self-validation rejected (code={outcome.RejectWireCode}) — keeping \"Host\" fallback");
                    return;
                }
                if (!string.IsNullOrEmpty(outcome.DisplayName)) displayName = outcome.DisplayName;
                // An over-bound account (>62B) would truncate in the roster field → identity collision. The
                // host cannot be rejected, so drop it to the fallback (empty) instead of propagating it.
                if (RosterEntry.IsAccountOverBound(outcome.Account))
                {
                    _logger?.KWarning($"[KlothoNetworkService] Host self-validation account exceeds 62B — dropping to empty");
                    account = string.Empty;
                }
                else
                {
                    account = outcome.Account ?? string.Empty;
                }
            }
        }

        // Runs the validation hook for an incoming P2P guest. P2P is offline and synchronous, so there is
        // no pending queue or drain. Returns true to proceed (validatorRan + resolved validated identity
        // out-params); false if the peer was rejected (disconnect sent, sync state removed) and the caller
        // must return.
        private bool TryValidateIdentityP2P(int peerId, bool isLateJoin, out bool validatorRan, out string account, out string displayName, out byte[] entitlement)
        {
            validatorRan = false;
            account = string.Empty;
            displayName = string.Empty;
            entitlement = null; // remains null on every reject and on the no-validator path
            if (_identityValidator == null)
                return true; // no validator: skip validation and proceed with the fallback identity

            validatorRan = true;
            string ticket = _peerTickets.TryGetValue(peerId, out var t) ? t : string.Empty;
            string claimed = _peerClaimedDisplayNames.TryGetValue(peerId, out var c) ? c : string.Empty;
            string deviceId = _peerDeviceIds.TryGetValue(peerId, out var d) ? d : string.Empty;
            var request = new IdentityValidationRequest(ticket, claimed, _sessionMagic, peerId, deviceId, isLateJoin, isHostSelf: false, roomId: -1);
            using (var handle = _identityValidator.BeginValidate(request))
            {
                if (!handle.IsComplete)
                {
                    // P2P validators must complete synchronously (offline). A pending handle is misuse;
                    // the P2P host has no drain to wait on, so reject and log.
                    _logger?.KError($"[KlothoNetworkService] P2P validator returned a pending handle (must be synchronous): peer={peerId}, rejecting");
                    DisconnectWithReason(peerId, JoinFailReason.IdentityValidationFailed.ToWireCode());
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
                var outcome = handle.Outcome;
                // Reject on a validator reject, OR an accepted-but-over-bound account (>62B would truncate in
                // the roster field → identity collision). Empty account is allowed (Accept contract permits empty).
                if (!outcome.Accepted || RosterEntry.IsAccountOverBound(outcome.Account))
                {
                    byte code = outcome.Accepted
                        ? JoinFailReason.IdentityInvalid.ToWireCode()
                        : JoinFailReasonExtensions.ClampIdentityWireCode(outcome.RejectWireCode);
                    if (outcome.Accepted)
                        // Accepted-but-over-bound: validator OK'd the identity but its account would truncate in
                        // the 62-byte roster field (→ identity collision), so the join is refused. Log it —
                        // otherwise a consumer whose validator emits long accounts without its own length guard
                        // sees only an opaque code-6 disconnect for peers that previously joined (truncated).
                        _logger?.KWarning($"[KlothoNetworkService] Join rejected: validator-accepted account exceeds the 62 UTF-8 byte roster bound (peer={peerId}) — refused to avoid identity-collision truncation.");
                    DisconnectWithReason(peerId, code);
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
                account = outcome.Account;
                displayName = outcome.DisplayName;
                // The stored entitlement must be byte-identical to what every guest independently derives for
                // this player — both feed the deterministic config clamp and the tick-0 seed, so any divergence
                // desyncs. When original-ticket propagation is on, guests derive it via signature-only
                // ReverifyPropagatedTicket (ReverifyAndAdoptIdentity), so the host derives from the SAME path
                // here rather than from the enforcing BeginValidate outcome — mirrors ResolveHostSelfIdentity.
                // A validator that normalizes/augments entitlement on Accept, or re-verifies non-signature-only,
                // would otherwise make the host-stored bytes differ from the guest-derived bytes. Falls back to
                // the validator outcome when propagation is off (host is the sole authority; no guest re-derivation)
                // or if the re-verify unexpectedly rejects a ticket BeginValidate just accepted. Null when
                // entitlements are off or the ticket is identity-only.
                if (_propagateOriginalTickets && _propagatedTicketVerifier != null && !string.IsNullOrEmpty(ticket))
                {
                    var entOutcome = _propagatedTicketVerifier.ReverifyPropagatedTicket(ticket);
                    entitlement = entOutcome.Accepted ? entOutcome.Entitlement : outcome.Entitlement;
                }
                else
                {
                    entitlement = outcome.Entitlement;
                }
                return true;
            }
        }

        // Resolves the display name by priority: validated value > claimed name (only when no validator
        // ran) > fabricated default. The fabricated default needs the reserved playerId, so this runs
        // after slot reservation.
        private string ResolveJoinDisplayName(int peerId, int newPlayerId, bool validatorRan, string validatedDisplayName)
        {
            if (validatorRan)
                return string.IsNullOrEmpty(validatedDisplayName) ? $"Player{newPlayerId}" : validatedDisplayName; // claimed name ignored once verified
            var claimed = _peerClaimedDisplayNames.TryGetValue(peerId, out var c) ? c : string.Empty;
            return !string.IsNullOrEmpty(claimed) ? claimed : $"Player{newPlayerId}";
        }

        /// <summary>
        /// Persist cold-start Reconnect credentials at Phase = Playing entry.
        /// No-op for host (host is not a cold-start target) or when no store is injected.
        /// </summary>
        private void SaveReconnectCredentialsIfApplicable()
        {
            if (IsHost || _reconnectCredentialsStore == null || _transport == null)
                return;

            var creds = new PersistedReconnectCredentials
            {
                RemoteAddress = _transport.RemoteAddress,
                RemotePort = _transport.RemotePort,
                SessionMagic = _sessionMagic,
                LocalPlayerId = LocalPlayerId,
                SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                RoomName = RoomName,
                AppVersion = _appVersion,
                DeviceId = GetDeviceId(),
            };
            _reconnectCredentialsStore.Save(creds);
        }

        /// <summary>
        /// Guest-only initialization — takes over a state where the handshake has already
        /// completed via KlothoConnection, skipping JoinRoom + handshake and starting in Synchronized.
        /// </summary>
        public void InitializeFromConnection(ConnectionResult result, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = result.Transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _emptyCommandCache = _commandFactory.CreateEmptyCommand();
            _sessionConfig = new SessionConfig();  // Default. Replaced in SubscribeEngine().
            _simConfig = new SimulationConfig();   // Default. Replaced in SubscribeEngine().

            // Apply the handshake result directly (replaces the Initialize + JoinRoom + handshake path)
            IsHost = false;
            LocalPlayerId = result.LocalPlayerId;
            _sessionMagic = result.SessionMagic;
            _sharedClock = new SharedTimeClock(result.SharedEpoch, result.ClockOffset);

            // Build the lobby roster from the SyncComplete snapshot forwarded via ConnectionResult on a
            // normal join. Without this the joining guest's player list stays empty until GameStartMessage.
            // LateJoin and Reconnect carry their roster separately, so this is guarded on the normal join.
            if (result.Kind == JoinKind.Normal && result.Roster != null && result.Roster.Count > 0)
            {
                RebuildPlayerList(result.Roster, useReady: true, useNames: true, rosterTickets: result.RosterTickets);
            }

            Phase = SessionPhase.Synchronized;

            // Buffer the server-recommended extra delay until SubscribeEngine wires the engine.
            // Source differs per JoinKind:
            //   - LateJoin / Reconnect: payload's AcceptMessage (KlothoConnection preserves the entire msg).
            //   - Normal (Sync): scalar forwarded via ConnectionResult.RecommendedExtraDelay.
            // Defensive ?. + ?? 0 guards a Kind/Payload invariant breach (theoretical) with graceful fallback.
            int seedValue = result.Kind switch
            {
                JoinKind.LateJoin  => result.LateJoinPayload?.AcceptMessage?.RecommendedExtraDelay ?? 0,
                JoinKind.Reconnect => result.ReconnectPayload?.AcceptMessage?.RecommendedExtraDelay ?? 0,
                _                  => result.RecommendedExtraDelay,
            };
            if (seedValue > 0)
            {
                _pendingExtraDelayValue = seedValue;
                _pendingExtraDelaySource = ResolveExtraDelaySource(result.Kind);
            }

            // Wire up network events (same as Initialize)
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        private static ExtraDelaySource ResolveExtraDelaySource(JoinKind kind) => kind switch
        {
            JoinKind.LateJoin  => ExtraDelaySource.LateJoin,
            JoinKind.Reconnect => ExtraDelaySource.Reconnect,
            _                  => ExtraDelaySource.Sync,
        };

        public void CreateRoom(string roomName, int maxPlayers)
        {
            IsHost = true;
            RoomName = roomName;
            MaxPlayers = maxPlayers;
            LocalPlayerId = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sessionMagic = SessionMagicFactory.Generate();
            _sharedClock = new SharedTimeClock(now, 0);

            // Explicit reset before Phase = Synchronized (the Phase setter does NOT reset on Synchronized,
            // so this is the only init point for a new host session).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Synchronized; // Host bypasses handshake — no handshaking needed

            InitDisconnectedPlayerPool(maxPlayers);

            // Add the host as a player. With a lobby validator, the host self-validates its own ticket
            // and overlays the authoritative DisplayName/Account; otherwise it keeps "Host". This is
            // synchronous (P2P is offline) — no pending-validation drain is running yet at this point.
            string hostDisplayName = "Host";
            string hostAccount = string.Empty;
            byte[] hostEntitlement = null; // the host's own entitlement, extracted signature-only below
            if (_identityValidator != null)
                ResolveHostSelfIdentity(ref hostDisplayName, ref hostAccount, ref hostEntitlement);
            var hostPlayer = new PlayerInfo
            {
                PlayerId = LocalPlayerId,
                DisplayName = hostDisplayName,
                Account = hostAccount,
                // Host-self OriginalTicket source = the durable _localIdentityTicket
                // field (NOT _peerTickets — the host has no peer entry). Propagated like any peer's so
                // guests re-verify the host (the guest is the real verifier). "" when no lobby / gate off.
                OriginalTicket = _propagateOriginalTickets ? _localIdentityTicket : string.Empty,
                // The host's own entitlement — the same bytes guests derive from the propagated host ticket
                // via signature-only re-verification, so the host's tick-0 seed agrees with every guest's view.
                Entitlement = hostEntitlement,
                IsReady = false
            };
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Add(hostPlayer);
            if (hostPlayer.Entitlement != null && hostPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via HostSelf: playerId={LocalPlayerId}, bytes={hostPlayer.Entitlement.Length}");
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
        }

        public void JoinRoom(string roomName)
        {
            IsHost = false;
            _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            Phase = SessionPhase.Lobby;
        }

        public void LeaveRoom(bool keepReconnectCredentials = false)
        {
            if (_transport != null)
            {
                _transport.OnDataReceived -= HandleDataReceived;
                _transport.OnPeerConnected -= HandlePeerConnected;
                _transport.OnPeerDisconnected -= HandlePeerDisconnected;
                _transport.OnConnected -= HandleConnected;
                _transport.OnDisconnected -= HandleDisconnected;
            }

            // Discard cold-start Reconnect credentials on graceful session end.
            // Process-shutdown paths pass keepReconnectCredentials=true so a relaunch can Reconnect.
            if (!keepReconnectCredentials)
                _reconnectCredentialsStore?.Clear();

            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Clear();
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
            _spectators.Clear();
            _peerToPlayer.Clear();
            _peerDeviceIds.Clear();
            _peerClaimedDisplayNames.Clear();
            _peerTickets.Clear();
            _peerSyncStates.Clear();
            _syncHashes.Clear();
            _sessionMagic = 0;
            _gameStartTime = 0;

            // Explicit reset (the Phase setter also handles Disconnected — kept as defensive redundancy).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Disconnected;
            _sharedClock = default;
        }

        public void SetReady(bool ready)
        {
            // Ready is only allowed once handshake has completed
            if (Phase != SessionPhase.Synchronized)
                return;

            // Broadcast ready state
            var msg = new PlayerReadyMessage
            {
                PlayerId = LocalPlayerId,
                IsReady = ready
            };
            BroadcastMessagePooled(msg, DeliveryMethod.Reliable);

            // Apply locally for the local player too — our own broadcast is not echoed back to us
            // (the host relays a player's ready to other peers only). The relay/start-game logic
            // inside is gated by IsHost + fromPeerId, so this is safe for a guest as well.
            HandlePlayerReadyMessage(msg);
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

            if (IsHost)
            {
                // Host: HandlePlayerConfigMessage handles local storage (Deserialize) + relay to all peers
                HandlePlayerConfigMessage(msg);
            }
            else
            {
                // Guest: send to host — the host echo-broadcasts to every peer including the sender
                using (var serialized = _messageSerializer.SerializePooled(msg))
                    _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // peerId is the sender's peer when the message arrived over the wire, or -1 for the host's own
        // config sent through SendPlayerConfig. It drives the per-peer entitlement clamp and the anti-spoof
        // binding below.
        private void HandlePlayerConfigMessage(PlayerConfigMessage msg, int peerId = -1)
        {
            // Deserialize ConfigData into a PlayerConfigBase
            var configMsg = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            // A config whose ConfigData does not deserialize would skip the spoof-binding and entitlement
            // guards below — drop it WITHOUT relaying. Otherwise the host forwards a message carrying an
            // attacker-chosen msg.PlayerId to every peer, bypassing the unmapped-peer / spoof-id drop. Mirrors
            // the SD server's null-selection drop. A legitimate config always deserializes non-null; and in
            // lockstep every peer must share the config type, so a config the host cannot parse (thus cannot
            // clamp) must not be relayed unchecked.
            if (configMsg == null)
            {
                _logger?.KWarning($"[KlothoNetworkService] PlayerConfig ConfigData did not deserialize (peer={peerId}) — dropped (no apply/relay).");
                return;
            }

            // When the host receives a config directly from a guest, bind msg.PlayerId to the
            // authenticated sender and drop a spoofed id before relaying, so a cheating guest cannot
            // submit a config for another player. The host's own config (peerId below zero) and a config
            // relayed to a guest (peerId is the host) trust msg.PlayerId, since the host already bound it
            // on direct receipt.
            int resolvedPlayerId = msg.PlayerId;
            if (IsHost && peerId >= 0)
            {
                // The sender MUST be a joined player. A config from an unmapped peer — a spectator, or a
                // peer still mid-handshake before CompletePeerSync registers it — is dropped (no apply/
                // relay) so it cannot inject or overwrite another player's config. Mirrors the SD server's
                // unmapped-peer drop; without it the wire-claimed msg.PlayerId would be trusted verbatim.
                if (!_peerToPlayer.TryGetValue(peerId, out int boundPlayerId))
                {
                    _logger?.KWarning($"[KlothoNetworkService] PlayerConfig from unmapped peer={peerId} (not a joined player) — dropped (no apply/relay).");
                    return;
                }
                if (msg.PlayerId != boundPlayerId)
                {
                    _logger?.KWarning($"[KlothoNetworkService] PlayerConfig PlayerId spoof: peer={peerId} claimed playerId={msg.PlayerId} != bound {boundPlayerId} — dropped (no apply/relay).");
                    return;
                }
                resolvedPlayerId = boundPlayerId;
            }

            // Cross-check and clamp the selection. This runs on every peer and is deterministic: each
            // peer applies the same guard with the same verified entitlement bytes for this player, so
            // they reach an identical verdict. An unset guard leaves the selection untouched. Unlike the
            // dedicated server, which rewrites msg.ConfigData for an authoritative broadcast, P2P leaves
            // msg untouched: the host relays the raw config and each peer clamps locally. Clamping before
            // relay would make the seed host-authored and clamp it twice on guests.
            var effective = configMsg;
            if (_entitlementGuard != null)
            {
                var verdict = _entitlementGuard.Check(resolvedPlayerId, FindPlayerById(resolvedPlayerId)?.Entitlement, configMsg);
                if (verdict.Kind == PlayerConfigVerdictKind.Reject)
                {
                    // Post-join reject (strict-policy-only): drop locally and DO NOT relay — no peer
                    // receives it, so every peer falls back to the default for this player (consistent).
                    _logger?.KWarning($"[KlothoNetworkService] PlayerConfig rejected by entitlement guard: playerId={resolvedPlayerId} — dropped (no apply/relay).");
                    return;
                }
                if (verdict.Kind == PlayerConfigVerdictKind.Clamp && verdict.Replacement == null)
                {
                    // Fail-closed: a Clamp verdict with no replacement cannot be applied. Drop locally and
                    // DO NOT relay (mirrors Reject) instead of relaying the un-clamped client original, so a
                    // guard meant to restrict an over-privileged selection never fails open. Every peer runs
                    // the same deterministic guard and reaches the same drop, so all fall back to the default.
                    _logger?.KWarning($"[KlothoNetworkService] PlayerConfig clamp with null replacement: playerId={resolvedPlayerId} — dropped (no apply/relay).");
                    return;
                }
                if (verdict.Kind == PlayerConfigVerdictKind.Clamp && verdict.Replacement != null)
                    effective = verdict.Replacement; // applied to the engine only; msg stays raw for relay
            }
            (_engine as KlothoEngine)?.HandlePlayerConfigReceived(resolvedPlayerId, effective);

            // If we are the host, relay the RAW message to every peer (including the sender — it also stores
            // via the MessageSerializer path; each peer clamps the raw config locally).
            if (IsHost)
            {
                foreach (var kv in _peerToPlayer)
                {
                    using (var serialized = _messageSerializer.SerializePooled(msg))
                        _transport.Send(kv.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void StartGame()
        {
            // Duplicate-call guard. Re-entry would re-snapshot and disrupt
            // any LateJoin already absorbed past the first call.
            if (_gameStarted)
            {
                _logger?.KWarning($"[KlothoNetworkService] StartGame called twice — ignoring (snapshot already done)");
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

            long startTime = _sharedClock.SharedNow + _sessionConfig.CountdownDurationMs;
            _gameStartTime = startTime;

            var msg = new GameStartMessage
            {
                StartTime = startTime,
                RandomSeed = Environment.TickCount,
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
            }

            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
            HandleGameStartMessage(msg); // The server itself also processes it directly

            // Game start: send GameStartMessage to waiting spectators
            if (_spectators.Count > 0)
            {
                using (var serialized = _messageSerializer.SerializePooled(msg))
                {
                    for (int i = 0; i < _spectators.Count; i++)
                    {
                        if (_spectators[i].LastSentTick == -1)
                            _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }

        public void SendCommand(ICommand command)
        {
            int cmdSize = command.GetSerializedSize();
            var cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = _commandMessageCache;
            msg.Tick = command.Tick;
            msg.PlayerId = command.PlayerId;
            msg.SenderTick = _localTick;
            // Stamp the measured advantage + proxy-timing flag. Both are set on every
            // send (the cache is reused) so a stale flag/value never leaks across calls.
            msg.SenderAdvantage = _localAdvantage;
            msg.TimingFlags = _proxyTimingPending ? TIMING_FLAG_PROXY : (byte)0;
            _proxyTimingPending = false; // one-shot — consumed by this send
            msg.CommandData = cmdBuf;
            msg.CommandDataLength = cmdWriter.Position;

            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);

            // The sender processes its own command locally right away (common to host/client)
            HandleCommandMessage(msg);

            StreamPool.ReturnBuffer(cmdBuf);
        }

        // Reliable command submit — tick-less, reliably-ordered. Guest → host (star topology: a guest's
        // broadcast reaches peerId 0). The host would assign the execution tick and re-broadcast a
        // tick-confirmed command.
        // Currently unused: peer-to-peer keeps the legacy reliable path (the engine's reliable submit
        // returns false for peer-to-peer, so the command tracker uses the slot/retry path), so nothing
        // calls this. Retained as the host-placement entry point should the channel later be enabled
        // for peer-to-peer.
        public void SendReliableCommand(ICommand command)
        {
            int cmdSize = command.GetSerializedSize();
            var cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = new ReliableCommandSubmitMessage
            {
                PlayerId = command.PlayerId,
                CommandData = cmdBuf,
                CommandDataLength = cmdWriter.Position,
                _sourceBuffer = null,
            };

            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);

            StreamPool.ReturnBuffer(cmdBuf);
        }

        // Reliable submit receive — the host (authority) would sequence it: assign a lead commit tick,
        // dedup by (playerId, sequence), and re-broadcast a tick-confirmed command.
        // Currently unused: peer-to-peer keeps the legacy reliable path, so no guest submits a
        // ReliableCommandSubmitMessage and this handler never fires in peer-to-peer. The
        // deserialize-then-drop below is a defensive no-op for any stray message.
        private void HandleReliableCommandSubmit(ReliableCommandSubmitMessage msg, int fromPeerId = -1)
        {
            if (!IsHost) return;   // only the authority sequences reliable commands

            var cmdSpan = msg.CommandDataSpan;
            if (cmdSpan.Length < 4) return;
            var reader = new SpanReader(cmdSpan);
            var command = _commandFactory.DeserializeCommandRaw(ref reader);
            if (command == null) return;

            // Dormant: a host would do commit-tick placement + (playerId, sequence) dedup +
            // tick-confirmed re-broadcast here. Until then, drop (unreachable in peer-to-peer — see
            // the method summary above).
            CommandPool.Return(command);
        }

        public void RequestCommandsForTick(int tick)
        {
            // Implement resend requests as needed
        }

        public void SendSyncHash(int tick, long hash, long cmdHash)
        {
            // Store our own hash so arriving remote hashes can be compared against it —
            // the transport does not loop back and SyncHashMessage is not relayed.
            var local = (hash, cmdHash);
            _syncHashes[(tick, LocalPlayerId)] = local;

            // Compare against remote hashes that arrived before ours was computed
            // (faster peers, or our deferred send after speculative execution).
            foreach (var kvp in _syncHashes)
            {
                if (kvp.Key.tick != tick || kvp.Key.playerId == LocalPlayerId)
                    continue;
                CompareAndReportSyncHash(tick, kvp.Key.playerId, local, kvp.Value);
            }

            var msg = _syncHashMessageCache;
            msg.Tick = tick;
            msg.Hash = hash;
            msg.PlayerId = LocalPlayerId;
            msg.CommandHash = cmdHash;

            BroadcastMessagePooled(msg, DeliveryMethod.Unreliable);
        }

        public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash)
        {
            if (IsHost) return; // host handles its own state locally — reports are guest → host
            var msg = new ResyncFailureReportMessage
            {
                PlayerId = LocalPlayerId,
                Tick = tick,
                Reason = (byte)reason,
                LocalHash = localHash,
                RemoteHash = remoteHash,
            };
            _logger?.KWarning($"[KlothoNetworkService][ResyncFailure] report → host: tick={tick}, reason={reason}");
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered); // star topology — reaches the host only
        }

        public void BroadcastMatchAbort(byte reason)
        {
            if (!IsHost) return;
            var msg = new MatchAbortMessage { Reason = reason };
            _logger?.KWarning($"[KlothoNetworkService][MatchAbort] broadcast: reason={reason}");
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
        }

        private void HandleResyncFailureReportMessage(ResyncFailureReportMessage msg)
        {
            if (!IsHost) return;
            _logger?.KWarning($"[KlothoNetworkService][ResyncFailure] received: playerId={msg.PlayerId}, tick={msg.Tick}, reason={(ResyncFailureReason)msg.Reason}, local=0x{msg.LocalHash:X16}, remote=0x{msg.RemoteHash:X16}");
            OnResyncFailureReported?.Invoke(msg.PlayerId, msg.Tick);
        }

        private void HandleMatchAbortMessage(MatchAbortMessage msg)
        {
            if (IsHost) return;
            _logger?.KWarning($"[KlothoNetworkService][MatchAbort] received: reason={msg.Reason}");
            OnMatchAbortReceived?.Invoke(msg.Reason);
        }

        public void InvalidateLocalSyncHashes(int fromTick)
        {
            _hashKeysToRemoveCache.Clear();
            foreach (var key in _syncHashes.Keys)
            {
                if (key.playerId == LocalPlayerId && key.tick >= fromTick)
                    _hashKeysToRemoveCache.Add(key);
            }
            for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
                _syncHashes.Remove(_hashKeysToRemoveCache[i]);
        }

        public void InvalidateSyncHashes(int fromTick)
        {
            // Drop ALL peers' entries (local + remote) >= fromTick so a post-apply
            // recompute is not compared against a pre-reset remote hash across the reset boundary.
            _hashKeysToRemoveCache.Clear();
            foreach (var key in _syncHashes.Keys)
            {
                if (key.tick >= fromTick)
                    _hashKeysToRemoveCache.Add(key);
            }
            for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
                _syncHashes.Remove(_hashKeysToRemoveCache[i]);
        }

        public void Update()
        {
            _transport?.PollEvents();

            // Check countdown completion (common to host/client)
            if (Phase == SessionPhase.Countdown && _sharedClock.IsValid)
            {
                if (_sharedClock.SharedNow >= _gameStartTime)
                {
                    Phase = SessionPhase.Playing;
                    SaveReconnectCredentialsIfApplicable();
                    OnGameStart?.Invoke();
                }
            }

            // Reconnect / chain watchdogs — mixed host-only and peer-local; each method gates internally
            CheckQuorumMissPresumedDrop();
            CheckDisconnectedPlayerTimeout();
            CheckChainStallTimeout();
            InjectDisconnectedPlayerInputs();
            InjectCatchupPlayerInputs();
            UpdateReconnect();

            if (!IsHost) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Check handshake timeout
            foreach (var kvp in _peerSyncStates)
            {
                var state = kvp.Value;
                int timeout = state.IsLateJoin ? LATE_JOIN_HANDSHAKE_TIMEOUT_MS : SYNC_TIMEOUT_MS;
                if (!state.Completed && now - state.LastSyncSentTime > timeout)
                {
                    state.Attempt++;
                    SendSyncRequest(kvp.Key, state);
                }
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

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            if (_pendingPeers.Contains(peerId))
            {
                // Pre-auth memory-amplification guard: reject an oversized first message before
                // Deserialize allocates per-field strings or the Ticket is retained pre-validation.
                if (length > PlayerJoinMessage.MaxPreAuthMessageBytes)
                {
                    _logger?.KWarning($"[KlothoNetworkService][HandleDataReceived] Oversized pre-auth message from peer {peerId}: {length}B > {PlayerJoinMessage.MaxPreAuthMessageBytes}B");
                    _pendingPeers.Remove(peerId);
                    DisconnectWithReason(peerId, JoinFailReason.IdentityInvalid.ToWireCode());
                    return;
                }
                var firstMsg = _messageSerializer.Deserialize(data, length);
                _pendingPeers.Remove(peerId);
                if (firstMsg is PlayerJoinMessage playerJoin)
                {
                    _peerDeviceIds[peerId] = playerJoin.DeviceId ?? string.Empty;
                    // Capture now: the deserialized message is a reused singleton (the next message
                    // overwrites it), and CompletePeerSync (which reads this) runs later.
                    _peerClaimedDisplayNames[peerId] = RosterEntry.ClampClaimedName(playerJoin.ClaimedDisplayName);
                    _peerTickets[peerId] = playerJoin.Ticket ?? string.Empty; // captured now; read by the validation hook later
                    // Dispatch on _gameStarted (not Phase == Playing).
                    //   Countdown peers go to LateJoin too — a standard handshake completing
                    //   after StartGame would land in a wire/PlayerId mismatch (no GameStartMessage
                    //   sent to the new peer; the race guard in CompletePeerSync catches the residual case).
                    if (_gameStarted)
                    {
                        HandleLateJoin(peerId);
                    }
                    else
                    {
                        // Pending-aware capacity gate — closes the Lobby join race when concurrent peers approach the cap.
                        if (EffectivePlayerCount >= MaxPlayerCapacity)
                        {
                            _logger?.KWarning($"[KlothoNetworkService][HandleDataReceived] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                            DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                            return;
                        }
                        StartHandshake(peerId);
                    }
                }
                else if (firstMsg is SpectatorJoinMessage spectatorJoin)
                    HandleSpectatorJoin(peerId, spectatorJoin);
                else if (firstMsg is ReconnectRequestMessage reconnectReq)
                    HandleReconnectRequest(peerId, reconnectReq);
                else
                {
                    _logger?.KWarning($"[KlothoNetworkService] Malformed/unknown first message — peerId={peerId} disconnected");
                    _transport.DisconnectPeer(peerId);
                }
                return;
            }

            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[KlothoNetworkService] Malformed payload from peerId={peerId} — disconnect");
                _transport.DisconnectPeer(peerId);
                return;
            }

            // Spectator peers: process FullStateRequest without the player-side throttle
            if (message is FullStateRequestMessage spectatorFullReq)
            {
                for (int i = 0; i < _spectators.Count; i++)
                {
                    if (_spectators[i].PeerId == peerId)
                    {
                        OnFullStateRequested?.Invoke(peerId, spectatorFullReq.RequestTick);
                        return;
                    }
                }
            }

            switch (message)
            {
                case CommandMessage cmdMsg:
                    HandleCommandMessage(cmdMsg, peerId);
                    break;

                case ReliableCommandSubmitMessage submitMsg:
                    HandleReliableCommandSubmit(submitMsg, peerId);
                    break;

                case SyncHashMessage hashMsg:
                    HandleSyncHashMessage(hashMsg);
                    break;

                case ResyncFailureReportMessage resyncFailMsg:
                    HandleResyncFailureReportMessage(resyncFailMsg);
                    break;

                case MatchAbortMessage matchAbortMsg:
                    HandleMatchAbortMessage(matchAbortMsg);
                    break;

                case GameStartMessage startMsg:
                    HandleGameStartMessage(startMsg);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(playerConfigMsg, peerId); // pass the sender peer for id binding
                    break;

                case PlayerReadyMessage readyMsg:
                    HandlePlayerReadyMessage(readyMsg, peerId);
                    break;

                case PingMessage pingMsg:
                    HandlePingMessage(peerId, pingMsg);
                    break;

                case PongMessage pongMsg:
                    HandlePongMessage(peerId, pongMsg);
                    break;

                case SyncRequestMessage syncReqMsg:
                    HandleSyncRequest(peerId, syncReqMsg);
                    break;

                case SyncReplyMessage syncRepMsg:
                    HandleSyncReply(peerId, syncRepMsg);
                    break;

                case SyncCompleteMessage syncCompMsg:
                    HandleSyncComplete(peerId, syncCompMsg);
                    break;

                case FullStateRequestMessage fullReqMsg:
                    HandleFullStateRequest(peerId, fullReqMsg);
                    break;

                case FullStateResponseMessage fullResMsg:
                    HandleFullStateResponse(fullResMsg);
                    break;

                case ReconnectAcceptMessage reconnectAcceptMsg:
                    HandleReconnectAccept(reconnectAcceptMsg);
                    break;

                case ReconnectRejectMessage reconnectRejectMsg:
                    HandleReconnectReject(reconnectRejectMsg);
                    break;

                case LateJoinAcceptMessage lateJoinAcceptMsg:
                    HandleLateJoinAccept(lateJoinAcceptMsg);
                    break;

                case LateJoinNotificationMessage lateJoinNotification:
                    HandleLateJoinNotification(lateJoinNotification);
                    break;

                case PlayerStateNotificationMessage playerStateMsg:
                    HandlePlayerStateNotification(playerStateMsg);
                    break;

                case PlayerJoinNotificationMessage joinNotification:
                    HandlePlayerJoinNotification(joinNotification);
                    break;

                case PlayerLeaveNotificationMessage leaveNotification:
                    HandlePlayerLeaveNotification(leaveNotification);
                    break;

                case SpectatorInputMessage catchupMsg:
                    HandleCatchupInputMessage(catchupMsg);
                    break;

                case RecommendedExtraDelayUpdateMessage extraDelayMsg:
                    HandleRecommendedExtraDelayUpdate(extraDelayMsg);
                    break;

                case ReactiveExtraDelayReportMessage reactiveReport:
                    HandleReactiveExtraDelayReport(peerId, reactiveReport);
                    break;

                case DesyncProbeRequestMessage probeReq:
                    HandleDesyncProbeRequest(peerId, probeReq);
                    break;

                case DesyncProbeResponseMessage probeRes:
                    HandleDesyncProbeResponse(peerId, probeRes);
                    break;
            }
        }

        // Guest reports its effective extra-delay to the host so the host folds
        // the locally-observed reactive correction (rollback-burst) into the broadcast baseline. Host is
        // excluded (no reactive). Star topology: BroadcastMessagePooled from a guest reaches only peerId 0
        // (the host). Changes are sparse (escalate cooldown / decay dwell) — dedupe on unchanged value.
        private int _lastReportedEffective = -1;
        private void HandleEngineExtraDelayChanged(int effective)
        {
            if (IsHost) return;
            if (effective == _lastReportedEffective) return;
            _lastReportedEffective = effective;
            _reactiveExtraDelayCache.EffectiveExtraDelay = effective;
            BroadcastMessagePooled(_reactiveExtraDelayCache, DeliveryMethod.ReliableOrdered);
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"p2p-guest\",\"dir\":\"send\",\"effective\":{effective}}}");
        }

        // Guest reports its effective extra-delay; the host folds it into the
        // per-peer target baseline and re-evaluates the max-over-peers broadcast. Host-only.
        private void HandleReactiveExtraDelayReport(int peerId, ReactiveExtraDelayReportMessage msg)
        {
            if (!IsHost) return;
            _reportedEffective[peerId] = msg.EffectiveExtraDelay < 0 ? 0 : msg.EffectiveExtraDelay;
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"p2p-host\",\"dir\":\"absorb\",\"peerId\":{peerId},\"reportedEffective\":{_reportedEffective[peerId]}}}");
            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
                MaybePushExtraDelayUpdate(playerId, peerId);
        }

        // ── Gameplay messages ────────────────────

        private void HandleCommandMessage(CommandMessage msg, int fromPeerId = -1)
        {
            var cmdSpan = msg.CommandDataSpan;
            if (cmdSpan.Length < 4)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleCommandMessage] Command data too short: length={cmdSpan.Length}, playerId={msg.PlayerId}, tick={msg.Tick}");
                return;
            }

            // Guest >> Host >> other guests
            if (IsHost && fromPeerId != -1)
            {
                // If the (tick, playerId) slot is sealed locally (host has already filled with
                // an empty placeholder and chain advanced past it), suppress relay so other peers
                // keep the same empty placeholder. Without this guard, a late real packet from
                // the source peer reaches guests un-sealed and overwrites their empty → host vs
                // guest InputBuffer divergence (silent desync, no fallback at this stage).
                bool isSealedHere = _engine != null && _engine.IsCommandSealed(msg.Tick, msg.PlayerId);
                if (isSealedHere)
                {
                    _relaySealDropCount++;
                    return;
                }
                RelayMessage(msg, fromPeerId, DeliveryMethod.ReliableOrdered);
            }

            // DO NOT remove _lateJoinCatchups on first command receipt. Guest's first command
            // (Spawn at JoinTick) arrives within ~ms, well before guest has caught up via input
            // batches. Removal is now done in HandleFrameVerifiedForCatchup once
            // info.LastSentTick >= info.JoinTick — i.e., once host has actually delivered enough
            // input for guest to self-sustain.

            // Our own command received via the network has already been processed locally — avoid duplicates
            if (fromPeerId != -1 && msg.PlayerId == LocalPlayerId)
                return;

            var reader = new SpanReader(cmdSpan);
            var command = _commandFactory.DeserializeCommandRaw(ref reader);
            if (command == null)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleCommandMessage] DeserializeCommandRaw returned null (dataLen={cmdSpan.Length})");
                return;
            }

            // Quorum-miss watchdog false-positive: real input arrived for a player that was
            // presumed-dropped → remove from pool + rollback to restore real command path.
            // Network arrivals only — the watchdog's own activation fill echoes back here
            // synchronously (SendCommand self-dispatch, fromPeerId == -1) with PlayerId=X, and
            // the echo at the chain-stop tick lands exactly on the dynamic stall tick — the
            // presumed-drop would self-release in the very call stack that armed it. Path B's
            // per-tick fill echo matches the stall tick too, so every fill source must be
            // excluded here.
            if (fromPeerId != -1)
                OnRealCommandReceivedDuringPresumedDrop(command);

            OnCommandReceived?.Invoke(command);

            // Frame advantage is remote timing info — local echoes (SendCommand self-dispatch,
            // fromPeerId == -1) must not feed this machine's tick into the remote-tick median.
            // Guarding on PlayerId is NOT equivalent: host proxy-fills (Reconnect/Catchup) echo
            // with another player's PlayerId but our own SenderTick.
            // A proxy-timing broadcast (host filling for a disconnected/catching-up
            // player) carries the SENDING machine's tick/advantage, not the slot owner's — skip
            // the vote so it does not pollute _remoteTicks[that player] (wire-side bias source).
            if (fromPeerId != -1 && (msg.TimingFlags & TIMING_FLAG_PROXY) == 0)
                OnFrameAdvantageReceived?.Invoke(msg.PlayerId, msg.SenderTick, msg.SenderAdvantage);
        }

        private void HandleSyncHashMessage(SyncHashMessage msg)
        {
            _syncHashes[(msg.Tick, msg.PlayerId)] = (msg.Hash, msg.CommandHash);

            // Compare when the local hash for this tick has already been computed;
            // otherwise SendSyncHash performs the comparison once it is (deferred send path).
            if (_syncHashes.TryGetValue((msg.Tick, LocalPlayerId), out var local))
                CompareAndReportSyncHash(msg.Tick, msg.PlayerId, local, (msg.Hash, msg.CommandHash));
        }

        /// <summary>
        /// Single comparison point for local vs remote sync hashes. Fires OnDesyncDetected on
        /// mismatch and always fires OnSyncHashCompared — the engine promotes its last-matched
        /// sync anchor on matched comparisons (event-based promotion, no grace window).
        /// On a state mismatch it also emits the classification line: differing command digests ⇒
        /// input divergence (engine-side), equal digests ⇒ state divergence (game-logic determinism).
        /// The classification is diagnostic logging only — it does not alter OnDesyncDetected.
        /// </summary>
        private void CompareAndReportSyncHash(int tick, int remotePlayerId,
            (long state, long cmd) local, (long state, long cmd) remote)
        {
            bool matched = local.state == remote.state;
            if (!matched)
            {
                string cls = local.cmd != remote.cmd ? "InputDivergence" : "StateDivergence";
                _logger?.KWarning(
                    $"[Desync][Diag] tick={tick} class={cls} remotePlayer={remotePlayerId} " +
                    $"local.state=0x{local.state:X16} remote.state=0x{remote.state:X16} " +
                    $"local.cmd=0x{local.cmd:X16} remote.cmd=0x{remote.cmd:X16}");
                OnDesyncDetected?.Invoke(remotePlayerId, tick, local.state, remote.state);
            }
            OnSyncHashCompared?.Invoke(tick, remotePlayerId, matched);
        }

        private void HandleGameStartMessage(GameStartMessage msg)
        {
            _logger?.KInformation($"[KlothoNetworkService][HandleGameStartMessage] Game start: seed={msg.RandomSeed}, startTime={msg.StartTime}, players={msg.PlayerIds.Count}");

            // Apply server-authoritative SessionConfig fields in place. Engine and NetworkService
            // share the same SessionConfig reference, so mutating the instance propagates to both
            // readers automatically. Match-start one-shot; SessionConfig stays immutable afterward.
            // Host self-dispatch: msg values originate from _sessionConfig, so this is effectively a no-op.
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

            // Update the player list, preserving each existing DisplayName by PlayerId across the
            // Clear+rebuild. FindPlayerById reads the pre-clear list, so the names are captured before
            // Clear; otherwise the host's own names would be wiped to null at GameStart.
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                var existing = FindPlayerById(msg.PlayerIds[i]);
                _gameStartNameCache.Add(existing?.DisplayName);
                _gameStartAccountCache.Add(existing?.Account);  // preserve Account too
                // Preserve host-only OriginalTicket across the rebuild for ALL players
                // incl. host-self — else post-GameStart late-join/reconnect propagates empty tickets
                // and guests can't re-verify. Harmlessly "" when the propagation gate is off.
                _gameStartTicketCache.Add(existing?.OriginalTicket);
                // Preserve each peer's entitlement across the rebuild for every player including the host;
                // otherwise a post-GameStart late-join or reconnect carries an empty entitlement and desyncs.
                _gameStartEntitlementCache.Add(existing?.Entitlement);
            }
            _players.Clear();
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                var player = new PlayerInfo
                {
                    PlayerId = msg.PlayerIds[i],
                    DisplayName = _gameStartNameCache[i] ?? string.Empty,
                    Account = _gameStartAccountCache[i] ?? string.Empty,
                    OriginalTicket = _gameStartTicketCache[i] ?? string.Empty,
                    Entitlement = _gameStartEntitlementCache[i], // null is fine and means no entitlement
                    IsReady = true
                };
                _players.Add(player);
                if (player.Entitlement != null && player.Entitlement.Length > 0)
                    _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via GameStartRebuild: playerId={player.PlayerId}, bytes={player.Entitlement.Length}");
            }
            _gameStartNameCache.Clear();
            _gameStartAccountCache.Clear();
            _gameStartTicketCache.Clear();
            _gameStartEntitlementCache.Clear();
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);

            RandomSeed = msg.RandomSeed;
            _gameStartTime = msg.StartTime;
            Phase = SessionPhase.Countdown;
            OnCountdownStarted?.Invoke(msg.StartTime);
        }

        private void HandlePlayerReadyMessage(PlayerReadyMessage msg, int fromPeerId = -1)
        {
            _logger?.KInformation($"[KlothoNetworkService][HandlePlayerReadyMessage] Player ready: playerId={msg.PlayerId}, isReady={msg.IsReady}, fromPeerId={fromPeerId}");

            var player = FindPlayerById(msg.PlayerId);
            if (player != null)
            {
                bool prevReady = AllPlayersReady;
                player.IsReady = msg.IsReady;
                RaiseAllPlayersReadyIfChanged(prevReady);
            }

            // BroadcastMessagePooled → _transport.Broadcast reaches all connected peers, including
            // spectators (so their ready display stays consistent — SpectatorService handles PlayerReadyMessage).
            if (IsHost && fromPeerId != -1)
                BroadcastMessagePooled(msg, DeliveryMethod.Reliable);

            // Host: start the game once every player is ready
            if (IsHost && AllPlayersReady && _players.Count >= _sessionConfig.MinPlayers)
            {
                StartGame();
            }
        }

        // Receiver for a new player that completed the normal-join (lobby) handshake on the host. Adds it
        // to this guest's player list so the roster stays consistent before StartGame. The host owns its
        // own roster (added in CompletePeerSync), so a host instance rejects this to keep a forged guest
        // notification from slipping past the duplicate guard. A duplicate (reliable retry) is a no-op.
        private void HandlePlayerJoinNotification(PlayerJoinNotificationMessage msg)
        {
            if (IsHost)
                return;

            if (FindPlayerById(msg.PlayerId) != null)
            {
                _logger?.KDebug($"[KlothoNetworkService][HandlePlayerJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                return;
            }

            // Use the authority-propagated identity instead of locally fabricating a name.
            // Re-verify the propagated original ticket and adopt the ticket-derived
            // identity (replace host-relayed) before adding — no-op when the gate is off.
            string pjDisplayName = msg.DisplayName ?? string.Empty;
            string pjAccount = msg.Account ?? string.Empty;
            byte[] pjEntitlement = null; // adopted from the re-verified ticket
            ReverifyAndAdoptIdentity(msg.PlayerId, msg.OriginalTicket, ref pjAccount, ref pjDisplayName, ref pjEntitlement);
            var newPlayer = new PlayerInfo
            {
                PlayerId = msg.PlayerId,
                DisplayName = pjDisplayName,
                Account = pjAccount,
                Entitlement = pjEntitlement,
                IsReady = msg.IsReady,
                ConnectionState = (PlayerConnectionState)msg.ConnectionState,
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            if (newPlayer.Entitlement != null && newPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via JoinNotification: playerId={msg.PlayerId}, bytes={newPlayer.Entitlement.Length}");

            OnPlayerJoined?.Invoke(newPlayer);
            _logger?.KInformation($"[KlothoNetworkService][HandlePlayerJoinNotification] Lobby player added: playerId={msg.PlayerId}");
        }

        // Receiver for a player that left during the lobby. Removes it from this guest's player list.
        // The host owns its own roster, so a host instance rejects this. An unknown PlayerId is a no-op.
        private void HandlePlayerLeaveNotification(PlayerLeaveNotificationMessage msg)
        {
            if (IsHost)
                return;

            var player = FindPlayerById(msg.PlayerId);
            if (player == null)
                return;

            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Remove(player);
            OnPlayerLeft?.Invoke(player);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            _logger?.KInformation($"[KlothoNetworkService][HandlePlayerLeaveNotification] Lobby player removed: playerId={msg.PlayerId}");
        }

        // ── Periodic RTT measurement ──────────────────────

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

        private void HandlePongMessage(int peerId, PongMessage msg)
        {
            // Calculate RTT
            long rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.Timestamp;

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = FindPlayerById(playerId);
                if (player != null)
                {
                    player.Ping = (int)rtt;

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
                }
            }
        }

        private void RelayMessage(INetworkMessage message, int excludePeerId, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key != excludePeerId)
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, deliveryMethod);
                }
            }
        }

        private void BroadcastMessagePooled(INetworkMessage message, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                if (IsHost)
                    _transport?.Broadcast(serialized.Data, serialized.Length, deliveryMethod);
                else
                    _transport?.Send(0, serialized.Data, serialized.Length, deliveryMethod);
            }
        }

        // ── Player count accounting helpers ─────────────────────────

        // Phase-branched effective slot count.
        //   Pre-GameStart: _players.Count (slot reuse on leave) + pending handshakes.
        //   Post-GameStart: Math.Max(_assignedPlayerIdCount, _nextPlayerId-1) enforces both
        //     the capacity invariant and the bot-ID invariant — covers sparse distributions
        //     where _nextPlayerId outpaces the slot count after a Pre-GameStart leave.
        //   Host-only (guests do not maintain _gameStarted / _assignedPlayerIdCount).
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
        //   P2P (LocalPlayerId == 0): host occupies slot 0, guests use [1, MaxPlayerCapacity - 1].
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

        private readonly byte[] _disconnectReasonBuf = new byte[1];

        // Reject a peer by disconnecting with the reason carried on the disconnect packet. The client
        // reads it via the transport's last-disconnect payload alongside the disconnect notification.
        private void DisconnectWithReason(int peerId, byte reason)
        {
            _disconnectReasonBuf[0] = reason;
            _transport.DisconnectPeer(peerId, _disconnectReasonBuf);
        }

        // Phase-branched slot reservation + reject action capsule.
        //   Pre-GameStart: smallest unused ID (slot reuse).
        //   Post-GameStart: monotonic _nextPlayerId++ (permanent occupation).
        //   On reject: DisconnectWithReason + immediate _peerSyncStates.Remove — the transport disconnect
        //   is async; without explicit removal, the stale entry keeps counting in CountPendingHandshakes.
        //   The reject reason rides the disconnect packet payload; the client maps it to a JoinFailReason.
        private bool TryReservePlayerSlot(int peerId, out int newPlayerId)
        {
            if (!_gameStarted)
            {
                newPlayerId = FindSmallestUnusedPlayerId();
                if (newPlayerId < 0)
                {
                    _logger?.KError($"[KlothoNetworkService] FindSmallestUnusedPlayerId returned -1: peer={peerId}, players={_players.Count}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                    DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
            }
            else
            {
                if (Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1) >= MaxPlayerCapacity)
                {
                    _logger?.KError($"[KlothoNetworkService] Post-GameStart slot overflow: assigned={_assignedPlayerIdCount}, nextId={_nextPlayerId}, max={MaxPlayerCapacity}, peer={peerId}");
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

    }
}
