using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.State;
using xpTURN.Klotho.Replay;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho engine implementation.
    /// </summary>
    public partial class KlothoEngine : IKlothoEngine
    {
        /// <summary>
        /// Interval (in ticks) for sending verified input batches to spectators.
        /// Increasing this value reduces network packet count, but spectator-perceived latency and prediction window grow proportionally.
        /// Related constants MAX_SPECTATOR_PREDICTION_TICKS and LATE_JOIN_CATCHUP_THRESHOLD_TICKS are tied to this value.
        /// </summary>
        private const int SPECTATOR_INPUT_INTERVAL = 2;

        /// <summary>
        /// Number of recent commands referenced when estimating missing remote input.
        /// Small values are sensitive to short-term patterns; large values increase inertia and slow the response to input changes.
        /// </summary>
        private const int PREDICTION_HISTORY_COUNT = 5;

        /// <summary>
        /// Safety margin tick count applied when cleaning up old data.
        /// Protects data references during rollbacks that occur beyond MaxRollbackTicks,
        /// and provides headroom for event diff/dispatch and the server retention window.
        /// </summary>
        private const int CLEANUP_MARGIN_TICKS = 10;
        
        KlothoState _state = KlothoState.Idle;
        public KlothoState State
        {
            get
            {
                return _state;
            }

            private set
            {
                _state = value;
                _logger?.ZLogInformation($"[KlothoEngine] State: {_state}");
            }
        }

        public int CurrentTick { get; private set; }
        public int LocalPlayerId => _networkService?.LocalPlayerId ?? 0;
        public int TickInterval => _simConfig.TickIntervalMs;
        public int InputDelay => _simConfig.InputDelayTicks;
        public int RecommendedExtraDelay => _recommendedExtraDelay;

        public event Action OnGameStart;
        public event Action<int> OnPreTick;
        public event Action<int> OnTickExecuted;
        public event Action<int, FrameState> OnTickExecutedWithState;
        public event Action<long, long> OnDesyncDetected;
        public event Action<int, int> OnRollbackExecuted;
        public event Action<int, string> OnRollbackFailed;
        public event Action<int> OnFrameVerified;
        public event Action OnChainAdvanceBreak;
        public event Action<int, SimulationEvent> OnEventPredicted;
        public event Action<int, SimulationEvent> OnEventConfirmed;
        public event Action<int, SimulationEvent> OnEventCanceled;
        public event Action<int, SimulationEvent> OnSyncedEvent;
        public event Action<int> OnResyncCompleted;
        public event Action OnResyncFailed;
        public event Action<int, int, RejectionReason> OnCommandRejected;
        public event Action<int, int, byte[], int> OnVerifiedInputBatchReady;

        /// <summary>
        /// Fired when player config data is received (playerId, firstTime).
        /// </summary>
        public event Action<int, bool> OnPlayerConfigReceived;

        private ISimulationCallbacks _simulationCallbacks;
        private IViewCallbacks _viewCallbacks;
        private CommandSender _commandSender;

        // Server-recommended extra InputDelay ticks. Seeded on LateJoin/Reconnect accept and updated by
        // periodic server push (mid-match RTT shifts) and client-reactive escalation.
        private int _recommendedExtraDelay;

        // Last cmd.Tick sent — used for monotonic clamp on Reconnect-induced delay decreases.
        // Sentinel int.MinValue: no prev cmd. First cmd trivially passes (targetTick > int.MinValue + 1).
        private int _lastSentCmdTick = int.MinValue;

        // [Metrics][LagReductionLatency] tracker — measures actual clamp resolution time after
        // an ApplyExtraDelay decrease. Cleared after the first non-clamped InputCommand following the decrease.
        private bool _lagReductionPending;
        private int _lagReductionPrevDelay;
        private int _lagReductionNewDelay;
        private int _lagReductionStartTick;

        // Test-only read-only accessor for _lagReductionPending. Production code must not depend on this.
        internal bool LagReductionPendingForTest => _lagReductionPending;

        private sealed class CommandSender : ICommandSender
        {
            private readonly KlothoEngine _engine;
            public CommandSender(KlothoEngine engine) => _engine = engine;
            public void Send(ICommand command) => _engine.InputCommand(command);
        }

        private static readonly CommandPlayerIdComparer s_commandComparer = new();

        private sealed class CommandPlayerIdComparer : IComparer<ICommand>
        {
            public int Compare(ICommand a, ICommand b)
            {
                bool aIsSys = a is ISystemCommand;
                bool bIsSys = b is ISystemCommand;
                if (aIsSys != bIsSys) return aIsSys ? 1 : -1;
                if (aIsSys) return ((ISystemCommand)a).OrderKey.CompareTo(((ISystemCommand)b).OrderKey);
                return a.PlayerId.CompareTo(b.PlayerId);
            }
        }

        private ISimulation _simulation;
        public ISimulation Simulation => _simulation;
        public ILogger Logger => _logger;

        private IKlothoNetworkService _networkService;
        private ILogger _logger;
        private ISimulationConfig _simConfig;
        private ISessionConfig _sessionConfig;

        // ServerDriven
        private IServerDrivenNetworkService _serverDrivenNetwork;
        private int _lastServerVerifiedTick;
        private bool _fullStateRequestPending;

        /// <summary>
        /// Simulation engine parameters. Immutable during a session.
        /// </summary>
        public ISimulationConfig SimulationConfig => _simConfig;

        /// <summary>
        /// Per-session mutable configuration.
        /// </summary>
        public ISessionConfig SessionConfig => _sessionConfig;

        /// <summary>
        /// Whether the role is server in SD mode. Returns false in P2P mode or Client mode.
        /// </summary>
        public bool IsServer => _serverDrivenNetwork?.IsServer ?? false;

        /// <summary>
        /// Whether the role is host in P2P mode. Returns false in SD mode or P2P guest.
        /// Delegates to the underlying network service; orthogonal to IsServer.
        /// </summary>
        public bool IsHost => _networkService?.IsHost ?? false;

        // Simulation stage. Default is Forward.
        // Switches to Resimulate when entering re-simulation, and returns to Forward immediately after.
        public SimulationStage Stage { get; private set; } = SimulationStage.Forward;

        private int _randomSeed;
        private readonly Dictionary<int, PlayerConfigBase> _playerConfigs = new Dictionary<int, PlayerConfigBase>();
        private InputBuffer _inputBuffer;
        private IStateSnapshotManager _snapshotManager;
        private SimpleInputPredictor _inputPredictor;

        private float _accumulator;
        private bool _consumePendingDeltaTime;
        private int _lastVerifiedTick;
        private int _lastBatchedTick = -1;
        private readonly List<int> _activePlayerIds = new List<int>();

        private readonly List<ICommand> _pendingCommands = new List<ICommand>();
        private readonly Dictionary<int, long> _localHashes = new Dictionary<int, long>();

        // Reusable lists to avoid GC.
        private readonly List<ICommand> _tickCommandsCache = new List<ICommand>();
        private readonly List<ICommand> _previousCommandsCache = new List<ICommand>();
        private readonly List<int> _hashKeysToRemoveCache = new List<int>();
        private readonly List<int> _savedTicksCache = new List<int>();

        // Tracks the last good sync tick to roll back to when desync is detected.
        private int _lastMatchedSyncTick;
        private int _pendingSyncCheckTick = -1;
        private bool _desyncDetectedForPending;

        private ICommandFactory _commandFactory;

        private EventBuffer _eventBuffer;
        private EventCollector _eventCollector;
        private EventDispatcher _dispatcher;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private long _lastTickWallMs;

        // Diagnostic — throttled chain-stall log.
        private long _lastChainStallLogMs;
#endif

        /// <summary>
        /// Whether recording is currently in progress.
        /// </summary>
        public bool IsRecording => _replaySystem?.IsRecording ?? false;

        // -- Frame References + RenderClock --

        // Frame snapshot dedicated to PreviousUpdatePredicted.
        // Synchronously refreshed once immediately on Update entry, and unaffected by subsequent rollback/resim within Update.
        // After the first allocation, the same instance is reused until session end.
        private ECS.Frame _puPFrame;
        private int _previousUpdateTick = -1;

        // Verified clock dedicated to SD Client. Created on first access and used every Update tick.
        private AdaptiveRenderClock _adaptiveRenderClock;
        private AdaptiveRenderClock AdaptiveClock => _adaptiveRenderClock ??= new AdaptiveRenderClock();
        private bool IsSDClient =>
            _simConfig.Mode == NetworkMode.ServerDriven
            && _serverDrivenNetwork != null
            && !_serverDrivenNetwork.IsServer
            && !_isReplayMode
            && !_isSpectatorMode;

        // Render time used to smoothly track the Verified timeline.
        // _lastVerifiedTick jumps discontinuously per network batch, so using it directly for alpha causes
        // misalignment between interpolation source frame swaps and alpha reset timing, producing jitter.
        // Advance a separate render time by wall-clock and clamp it with an upper bound based on _lastVerifiedTick.
        private double _verifiedRenderTimeMs;
        private bool   _verifiedRenderTimeInitialized;

        public RenderClockState RenderClock
        {
            get
            {
                // Replay uses the tick interval from the time of recording. Falls back to the current setting if no recording data is available.
                int tickMs = _isReplayMode
                    ? (_replaySystem?.CurrentReplayData?.Metadata?.TickIntervalMs ?? _simConfig.TickIntervalMs)
                    : _simConfig.TickIntervalMs;

                double accum = _isReplayMode ? _replaySystem.Accumulator : _accumulator;

                // Decompose the Verified render time into VerifiedBaseTick + VerifiedAlpha (independent of _lastVerifiedTick jumps).
                int verifiedBase;
                double verifiedAlphaMs;
                if (_verifiedRenderTimeInitialized && tickMs > 0)
                {
                    verifiedBase = (int)(_verifiedRenderTimeMs / tickMs);
                    verifiedAlphaMs = _verifiedRenderTimeMs - (double)verifiedBase * tickMs;
                }
                else
                {
                    verifiedBase = System.Math.Max(0, _lastVerifiedTick - _simConfig.InterpolationDelayTicks);
                    verifiedAlphaMs = 0;
                }

                return new RenderClockState
                {
                    PredictedBaseTick = CurrentTick - 1,
                    PredictedTimeMs = accum,
                    VerifiedBaseTick = verifiedBase,
                    VerifiedTimeMs = verifiedAlphaMs,
                    Timescale = 1f,
                    TickIntervalMs = tickMs,
                };
            }
        }

        /// <summary>
        /// Advances the Verified render time by wall-clock (deltaTime) and converges it
        /// toward the target time (_lastVerifiedTick - InterpolationDelayTicks) with a drift-proportional timescale.
        /// Even if _lastVerifiedTick jumps discontinuously, it catches up smoothly via timescale,
        /// and snaps to the target instantly when drift exceeds 10 ticks.
        /// Called at the very start of the engine Update.
        /// </summary>
        private void AdvanceVerifiedRenderTime(float deltaTime)
        {
            if (_lastVerifiedTick < 0) return;

            int tickMs = _simConfig.TickIntervalMs;
            if (tickMs <= 0) return;

            int targetBaseTick = System.Math.Max(0, _lastVerifiedTick - _simConfig.InterpolationDelayTicks);
            double targetTimeMs = (double)targetBaseTick * tickMs;

            if (!_verifiedRenderTimeInitialized)
            {
                _verifiedRenderTimeMs = targetTimeMs;
                _verifiedRenderTimeInitialized = true;
                return;
            }

            // drift (in ticks). Positive means render is ahead of target; negative means behind.
            double driftTicks = (_verifiedRenderTimeMs - targetTimeMs) / tickMs;

            // Catchup/slowdown via timescale proportional to drift.
            // drift=+1 tick -> 0.9x (10% slowdown), drift=-1 tick -> 1.1x (10% catchup).
            // Clamped to [0.5, 2.0] to keep motion within visually acceptable bounds.
            float timescale = 1f - (float)driftTicks * 0.1f;
            if      (timescale < 0.5f) timescale = 0.5f;
            else if (timescale > 2.0f) timescale = 2.0f;

            _verifiedRenderTimeMs += deltaTime * 1000.0 * timescale;

            // Safety guard for cases like long network outages or reconnect recovery where drift exceeds 10 ticks.
            double maxDriftMs = tickMs * 10;
            if (_verifiedRenderTimeMs > targetTimeMs + maxDriftMs ||
                _verifiedRenderTimeMs < targetTimeMs - maxDriftMs)
            {
                _verifiedRenderTimeMs = targetTimeMs;
            }
        }

        public FrameRef VerifiedFrame
        {
            get
            {
                if (_simulation is ECS.EcsSimulation ecsSim &&
                    ecsSim.TryGetSnapshotFrame(_lastVerifiedTick, out var frame))
                    return new FrameRef(_lastVerifiedTick, frame, FrameKind.Verified);
                return FrameRef.None(FrameKind.Verified);
            }
        }

        public FrameRef PredictedFrame
        {
            get
            {
                if (_simulation is ECS.EcsSimulation ecsSim)
                    return new FrameRef(CurrentTick, ecsSim.Frame, FrameKind.Predicted);
                return FrameRef.None(FrameKind.Predicted);
            }
        }

        public FrameRef PredictedPreviousFrame
        {
            get
            {
                int prevTick = CurrentTick - 1;
                if (_simulation is ECS.EcsSimulation ecsSim &&
                    ecsSim.TryGetSnapshotFrame(prevTick, out var frame))
                    return new FrameRef(prevTick, frame, FrameKind.PredictedPrevious);
                return FrameRef.None(FrameKind.PredictedPrevious);
            }
        }

        public FrameRef PreviousUpdatePredictedFrame
        {
            get
            {
                if (_previousUpdateTick < 0 || _puPFrame == null)
                    return FrameRef.None(FrameKind.PreviousUpdatePredicted);
                return new FrameRef(_previousUpdateTick, _puPFrame, FrameKind.PreviousUpdatePredicted);
            }
        }

        public bool TryGetFrameAtTick(int tick, out ECS.Frame frame)
        {
            if (_simulation is ECS.EcsSimulation ecsSim)
                return ecsSim.TryGetSnapshotFrame(tick, out frame);
            frame = null;
            return false;
        }

        /// <summary>
        /// Captures the PreviousUpdatePredicted snapshot.
        /// Must be called exactly once immediately on Update(dt) entry, before any simulation logic.
        /// Skipped in states without a render path: server/replay/spectator/awaiting resync.
        /// </summary>
        private void CapturePreviousUpdatePredicted()
        {
            if (_isReplayMode || _isSpectatorMode || IsServer
                || _expectingFullState
                || _expectingInitialFullState
                || _resyncState == ResyncState.Requested)
                return;

            if (_simulation is not ECS.EcsSimulation ecsSim) return;

            // Lazy-allocate after maxEntities is determined on the first Update entry.
            if (_puPFrame == null)
                _puPFrame = new ECS.Frame(ecsSim.Frame.MaxEntities, _logger);

            _puPFrame.CopyFrom(ecsSim.Frame);
            _previousUpdateTick = CurrentTick;
        }

        /// <summary>
        /// Injects SimulationConfig and SessionConfig separately.
        /// </summary>
        public KlothoEngine(ISimulationConfig simConfig, ISessionConfig sessionConfig)
        {
            _simConfig = simConfig;
            _sessionConfig = sessionConfig;
            _inputBuffer = new InputBuffer();
            _snapshotManager = new RingSnapshotManager(_simConfig.MaxRollbackTicks);
            _engineSnapshots = new EngineStateSnapshot[SnapshotCapacity];
            _inputPredictor = new SimpleInputPredictor();
            _replaySystem = new ReplaySystem();
            _randomSeed = (int)DateTime.Now.Ticks;
        }

        // -- PlayerConfig --

        /// <summary>
        /// Gets per-player custom data. Returns null if not yet received.
        /// </summary>
        public T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase
        {
            if (!_playerConfigs.TryGetValue(playerId, out var config))
                return null;
            return config as T;
        }

        /// <summary>
        /// Gets per-player custom data. Returns false if not yet received.
        /// </summary>
        public bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase
        {
            if (_playerConfigs.TryGetValue(playerId, out var raw) && raw is T typed)
            {
                config = typed;
                return true;
            }
            config = null;
            return false;
        }

        /// <summary>
        /// Called when PlayerConfigMessage is received. Stores internally and fires the event.
        /// </summary>
        internal void HandlePlayerConfigReceived(int playerId, PlayerConfigBase playerConfig)
        {
            bool firstTime = !_playerConfigs.ContainsKey(playerId);
            _playerConfigs[playerId] = playerConfig;
            OnPlayerConfigReceived?.Invoke(playerId, firstTime);
        }

        public void SetCommandFactory(ICommandFactory commandFactory)
        {
            if (commandFactory != null)
            {
                _commandFactory = commandFactory;
                _replaySystem = new ReplaySystem(commandFactory, _logger);
                _replaySystem.OnInitialStateSnapshotSet += HandleInitialStateSnapshotSet;
                _inputPredictor?.SetCommandFactory(commandFactory);
            }
        }

        public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger)
        {
            // Authoritative callers (server / P2P host) fail fast on invalid config;
            // non-authoritative callers (SD client / P2P guest) log and proceed to tolerate cross-version skew.
            bool isAuthoritative = networkService.IsHost
                || (networkService is IServerDrivenNetworkService sdn && sdn.IsServer);
            if (isAuthoritative)
            {
                _simConfig.Validate();
            }
            else if (!_simConfig.TryValidate(out string validateError))
            {
                logger?.ZLogError(
                    $"[KlothoEngine] Config validation failed from authoritative source — proceeding with deviation: {validateError}");
            }

            _simulation = simulation;
            _networkService = networkService;
            _logger = logger;
            _dispatcher = new EventDispatcher(logger, _simConfig.EventDispatchWarnMs);

            if (_simConfig.Mode == NetworkMode.ServerDriven && _simConfig.InputDelayTicks < 2)
                _logger?.ZLogWarning(
                    $"[KlothoEngine] InputDelayTicks={_simConfig.InputDelayTicks} below recommended minimum of 2 — increased jitter risk under network spikes.");
            (_inputBuffer as InputBuffer)?.SetLogger(logger);

            _activePlayerIds.Clear();
            for (int i = 0; i < networkService.Players.Count; i++)
                _activePlayerIds.Add(networkService.Players[i].PlayerId);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Diagnostic — roster snapshot at Initialize.
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < _activePlayerIds.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_activePlayerIds[i]);
                }
                _logger?.ZLogInformation($"[KlothoEngine][Roster] Initialize: PlayerCount={_activePlayerIds.Count}, active=[{sb}], LocalPlayerId={networkService.LocalPlayerId}, IsHost={networkService.IsHost}");
            }
#endif

            // Subscribe to reflect the initial-state snapshot in the engine cache when set during replay.
            _replaySystem.OnInitialStateSnapshotSet += HandleInitialStateSnapshotSet;

            // Wire up network events (branched per mode).
            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                _serverDrivenNetwork = _networkService as IServerDrivenNetworkService
                    ?? throw new InvalidOperationException(
                        "NetworkMode.ServerDriven requires IServerDrivenNetworkService");

                _networkService.OnGameStart += HandleGameStart;
                _networkService.OnFullStateRequested += HandleFullStateRequested;
                _networkService.OnLateJoinPlayerAdded += HandleLateJoinPlayerAdded;

                // SD Client must enable the initial FullState wait flag at Countdown entry.
                // It must be set before HandleGameStart so that server broadcasts arriving during Countdown are routed correctly.
                if (!_serverDrivenNetwork.IsServer)
                    _networkService.OnCountdownStarted += HandleCountdownStarted;

                _serverDrivenNetwork.OnVerifiedStateReceived += HandleVerifiedStateReceived;
                _serverDrivenNetwork.OnInputAckReceived += HandleInputAckReceived;
                _serverDrivenNetwork.OnServerFullStateReceived += HandleServerDrivenFullStateReceived;

                if (!_serverDrivenNetwork.IsServer)
                {
                    _serverDrivenNetwork.OnBootstrapBegin += HandleBootstrapBegin;
                    _serverDrivenNetwork.OnCommandRejected += HandleCommandRejected;
                }
            }
            else
            {
                _networkService.OnCommandReceived += HandleCommandReceived;
                _networkService.OnDesyncDetected += HandleNetworkDesync;
                _networkService.OnGameStart += HandleGameStart;
                _networkService.OnFullStateRequested += HandleFullStateRequested;
                _networkService.OnFullStateReceived += HandleFullStateReceived;
                _networkService.OnLateJoinPlayerAdded += HandleLateJoinPlayerAdded;

                _timeSync = new TimeSyncService();
                _networkService.OnFrameAdvantageReceived += HandleFrameAdvantage;
            }

            _simulation.OnPlayerJoinedNotification += HandlePlayerJoinedNotification;
            _simulation.Initialize();

            // Event system. SD server collects only Synced events (e.g. CommandRejectedSimEvent) for
            // network-layer unicast feedback; Regular events have no server-side subscribers and are
            // dropped at RaiseEvent to avoid GC churn. Other modes use the full collector.
            _eventBuffer = new EventBuffer(SnapshotCapacity);
            if (_simConfig.Mode == NetworkMode.ServerDriven && _serverDrivenNetwork != null && _serverDrivenNetwork.IsServer)
                _eventCollector = new SyncedOnlyEventCollector();
            else
                _eventCollector = new EventCollector();
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.Frame.EventRaiser = _eventCollector;

            _logger?.ZLogInformation($"[KlothoEngine] WarmupRegistry running");
            WarmupRegistry.RunAll();

            State = KlothoState.WaitingForPlayers;
        }

        public int RandomSeed => _randomSeed;

        public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger,
            ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null)
        {
            _simulationCallbacks = simulationCallbacks;
            _viewCallbacks = viewCallbacks;
            _commandSender = new CommandSender(this);
            Initialize(simulation, networkService, logger);
        }

        public void Initialize(ISimulation simulation, ILogger logger)
        {
            _simulation = simulation;
            _logger = logger;
            (_inputBuffer as InputBuffer)?.SetLogger(logger);

            _simulation.Initialize();

            _eventBuffer = new EventBuffer(SnapshotCapacity);
            _eventCollector = new EventCollector();
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.Frame.EventRaiser = _eventCollector;
        }

        public void Start()
        {
            Start(enableRecording: true);
        }

        public void Start(bool enableRecording)
        {
            if (State != KlothoState.WaitingForPlayers)
                return;

            CurrentTick = 0;
            _lastVerifiedTick = -1;
            _accumulator = 0;
            _consumePendingDeltaTime = true;

            // Pre-insert empty commands for the input-delay window so the verified chain advances through 0..(InputDelay-1).
            for (int t = 0; t < _simConfig.InputDelayTicks; t++)
            {
                for (int pi = 0; pi < _activePlayerIds.Count; pi++)
                {
                    var empty = CommandPool.Get<EmptyCommand>();
                    empty.PlayerId = _activePlayerIds[pi];
                    empty.Tick = t;
                    _inputBuffer.AddCommand(empty);
                }
            }

            // Initial entity creation must run before SaveSnapshot(0) so it is included in the snapshot.
            // Skipped on SD client: the authoritative initial state arrives via Initial FullState
            // broadcast and is applied through HandleInitialFullStateReceived -> ApplyFullState.
            // Calling it here would create entities that overlap the restored state (race-dependent
            // double-init).
            bool isSdClient = _simConfig.Mode == NetworkMode.ServerDriven && !IsServer;
            if (!isSdClient)
                _simulationCallbacks?.OnInitializeWorld(this);

            SaveSnapshot(0);

            if (enableRecording && !_isReplayMode)
            {
                _replaySystem.StartRecording(_activePlayerIds.Count, _simConfig, _randomSeed);
            }

            // SD server defers Running until all players ack initial FullState (or timeout) — see MarkBootstrapComplete.
            // The existing State == Running gate in Update naturally blocks UpdateServerTick during BootstrapPending.
            bool isSdServer = _simConfig.Mode == NetworkMode.ServerDriven && IsServer;
            State = isSdServer ? KlothoState.BootstrapPending : KlothoState.Running;
        }

        /// <summary>
        /// SD server only: flips BootstrapPending → Running once all players have ack'd Initial FullState
        /// (ack-complete path) or the bootstrap timeout has elapsed (timeout path).
        /// No-op + warn outside BootstrapPending to protect against duplicate / late callers (e.g. post-Reconnect ack).
        /// </summary>
        public void MarkBootstrapComplete()
        {
            if (State != KlothoState.BootstrapPending)
            {
                _logger?.ZLogWarning($"[KlothoEngine] MarkBootstrapComplete ignored (State={State})");
                return;
            }
            State = KlothoState.Running;
            _logger?.ZLogInformation($"[KlothoEngine] BootstrapPending -> Running");
        }

        /// <summary>
        /// SD server only: unicast cached Initial FullState to a single peer that missed the bootstrap ack window.
        /// Recipient recovers via the determinism-failure FullState path. No-op outside SD-server / before the cache is populated.
        /// </summary>
        public void SendBootstrapTimeoutResync(int peerId)
        {
            if (_simConfig.Mode != NetworkMode.ServerDriven || !IsServer) return;
            if (_cachedFullState == null) return;
            _networkService.SendFullStateResponse(peerId, _cachedFullStateTick, _cachedFullState, _cachedFullStateHash);
            _logger?.ZLogInformation($"[KlothoEngine][SD] Bootstrap timeout resync: peerId={peerId}, tick={_cachedFullStateTick}, size={_cachedFullState.Length}");
        }

        public void Update(float deltaTime)
        {
            // PuP snapshot capture. Run exactly once immediately on Update entry, before any simulation logic.
            // Not called again on rollback/resim paths within Update.
            CapturePreviousUpdatePredicted();

            // Advance the Verified render timeline by wall-clock. Run once before per-mode early returns.
            AdvanceVerifiedRenderTime(deltaTime);

            // Spectator mode: skip ordinary tick processing and update only the spectator system.
            if (_isSpectatorMode)
            {
                if (State == KlothoState.Running)
                    HandleSpectatorUpdate(deltaTime);
                return;
            }

            // Late Join catch-up: run verified ticks quickly without prediction.
            if (_isCatchingUp)
            {
                _networkService?.Update();
                if (State == KlothoState.Running)
                    HandleCatchupUpdate();
                return;
            }

            // Replay mode: skip ordinary tick processing and update only the replay system.
            if (_isReplayMode)
            {
                _replaySystem.Update(deltaTime);
                return;
            }

            // While awaiting spectator mode entry, _networkService may be null, so guard against it.
            if (_networkService == null)
                return;

            _networkService.Update();

            if (State != KlothoState.Running)
                return;

            // ServerDriven mode splits into server/client and follows different update paths.
            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                if (_serverDrivenNetwork.IsServer)
                    UpdateServerTick(deltaTime);
                else if (_expectingInitialFullState)
                    return;  // Awaiting initial FullState
                else
                    UpdateServerDrivenClient(deltaTime);
                return;
            }

            // During Resync, halt tick progression and only check the timeout.
            if (_resyncState == ResyncState.Requested)
            {
                CheckResyncTimeout(deltaTime);
                return;
            }

            _accumulator += deltaTime * 1000f; // accumulate in ms

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_simConfig.TickDriftWarnMultiplier > 0)
            {
                float maxAccumulator = _simConfig.TickIntervalMs * MAX_TICKS_PER_UPDATE;
                if (_accumulator > maxAccumulator)
                {
                    float dropped = _accumulator - maxAccumulator;
                    _accumulator = maxAccumulator;
                    if (dropped >= _simConfig.TickIntervalMs)
                        _logger?.ZLogWarning($"[KlothoEngine] ClientTick: Accumulator clamped: {dropped:F1}ms dropped ({dropped / _simConfig.TickIntervalMs:F1} ticks skipped)");
                }
            }
#endif

            // If ahead of remote peers, trim accumulated time to slow progression.
            if (_timeSyncEnabled)
            {
                int waitFrames = _timeSync.RecommendWaitFrames(requireIdleInput: true);
                if (waitFrames > 0)
                {
                    float waitMs = waitFrames * _simConfig.TickIntervalMs;
                    float before = _accumulator;
                    float remainder = before % _simConfig.TickIntervalMs;
                    _accumulator = Math.Max(_accumulator - waitMs, remainder);
                    bool tickSkipped = _accumulator < _simConfig.TickIntervalMs && before >= _simConfig.TickIntervalMs;

                    _logger?.ZLogDebug($"[KlothoEngine] TimeSync: Waiting {waitFrames} frames (local={_timeSync.LocalAdvantageMean:F1}, remote={_timeSync.RemoteAdvantageMean:F1})");

                    if (tickSkipped)
                    {
                        _logger?.ZLogWarning($"[KlothoEngine] TimeSync: Tick skip at tick {CurrentTick} (accumulator {before:F1}ms → {_accumulator:F1}ms, waitMs={waitMs:F1})");
                    }
                }
            }

            while (_accumulator >= _simConfig.TickIntervalMs)
            {
                _accumulator -= _simConfig.TickIntervalMs;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if (_simConfig.TickDriftWarnMultiplier > 0)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_lastTickWallMs > 0)
                    {
                        long gap = nowMs - _lastTickWallMs;
                        if (gap > _simConfig.TickIntervalMs * _simConfig.TickDriftWarnMultiplier)
                            _logger?.ZLogWarning($"[KlothoEngine] Tick gap: {gap}ms (expected {_simConfig.TickIntervalMs}ms), tick={CurrentTick}");
                    }
                    _lastTickWallMs = nowMs;
                }
#endif

                // Input-collection callback right before tick execution.
                if (_simulationCallbacks != null)
                    _simulationCallbacks.OnPollInput(LocalPlayerId, CurrentTick, _commandSender);
                else
                    OnPreTick?.Invoke(CurrentTick);

                int inputTick = CurrentTick + _simConfig.InputDelayTicks;

                // If the local player did not issue a command for this tick, auto-inject an empty command.
                if (!_inputBuffer.HasCommandForTick(inputTick, LocalPlayerId))
                {
                    var empty = CommandPool.Get<EmptyCommand>();
                    empty.PlayerId = LocalPlayerId;
                    InputCommand(empty);
                }

                if (CanAdvanceTick())
                {
                    ExecuteTick();
                }
                else if (_simConfig.UsePrediction)
                {
                    ExecuteTickWithPrediction();
                }
                else
                {
                    // If inputs are missing and prediction is disabled, halt the tick and wait.
                    State = KlothoState.Paused;
                    break;
                }
            }

            // Flush commands accumulated in OnPreTick onto the network within this frame.
            _networkService.FlushSendQueue();

            // Apply any rollback request deferred during the tick loop here.
            FlushPendingRollback();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Chain-stall warning (throttled 1s).
            // After CleanupOldData cap on _lastVerifiedTick, wipe is prevented when lag is high,
            // but the stall itself still indicates a network/sim issue worth surfacing.
            {
                int lag = CurrentTick - _lastVerifiedTick;
                int stallWarnThreshold = _simConfig.MaxRollbackTicks + CLEANUP_MARGIN_TICKS;
                if (lag >= stallWarnThreshold)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (nowMs - _lastChainStallLogMs >= 1000)
                    {
                        _lastChainStallLogMs = nowMs;
                        _logger?.ZLogWarning($"[KlothoEngine][ChainStall] lag={lag} >= stallWarnThreshold={stallWarnThreshold} (CurrentTick={CurrentTick}, _lastVerifiedTick={_lastVerifiedTick}) — Chain stalled, awaiting quorum / reconnect");
                    }
                }
            }
#endif
        }

        public event Action<int> OnExtraDelayChanged;

        public void ApplyExtraDelay(int delay, ExtraDelaySource source)
        {
            int prev = _recommendedExtraDelay;
            // LagReductionLatency tracker — measures mid-match natural clamp resolution time.
            // Reconnect path is excluded because catchup advances CurrentTick in a jump, making
            // actualTicks reflect catchup duration rather than clamp resolution.
            if (source == ExtraDelaySource.Reconnect)
            {
                // Stale guard: a prior DynamicPush DOWN may have set pending; the first unclamped
                // InputCommand after Reconnect catchup would emit stale prev/new with distorted
                // actualTicks. Force-clear to suppress the false measurement.
                _lagReductionPending = false;
            }
            else if (delay < prev)
            {
                _lagReductionPending = true;
                _lagReductionPrevDelay = prev;
                _lagReductionNewDelay = delay;
                _lagReductionStartTick = CurrentTick;
            }
            _recommendedExtraDelay = delay;
            // DynamicPush fires mid-match (rate-limited 500ms) — Debug to avoid prod noise.
            // Sync/LateJoin/Reconnect are 1-shot accept events — Information for operational trace.
            if (source == ExtraDelaySource.DynamicPush)
                _logger?.ZLogDebug($"[KlothoEngine][{source}] Recommended extra delay applied: {delay} ticks (CurrentTick={CurrentTick}, prev={prev})");
            else
                _logger?.ZLogInformation($"[KlothoEngine][{source}] Recommended extra delay applied: {delay} ticks (CurrentTick={CurrentTick}, prev={prev})");
            OnExtraDelayChanged?.Invoke(delay);
        }

        public void EscalateExtraDelay(int step, int max)
        {
            int newDelay = Math.Min(_recommendedExtraDelay + step, max);
            if (newDelay > _recommendedExtraDelay)
            {
                _logger?.ZLogWarning($"[KlothoEngine][DynamicDelay] Reactive escalate: prev={_recommendedExtraDelay}, new={newDelay}");
                _recommendedExtraDelay = newDelay;
                OnExtraDelayChanged?.Invoke(newDelay);
            }
        }

        public void InputCommand(ICommand command, int extraDelay = 0)
        {
            // Target tick reflecting input delay. extraDelay adds per-command lead margin
            // (used by recovery paths — e.g. spawn cmd PastTick reject escalation).
            // _recommendedExtraDelay compensates LateJoin/Reconnect catchup gap and mid-match RTT shifts.
            int targetTick = CurrentTick + _simConfig.InputDelayTicks + extraDelay + _recommendedExtraDelay;

            if (command is CommandBase cmdBase)
            {
                cmdBase.PlayerId = LocalPlayerId;

#if KLOTHO_FAULT_INJECTION
                // PastTick path: shift cmd.Tick to trigger ServerInputCollector's
                // `tick <= _lastExecutedTick` reject branch. Negative delta = past.
                int tickDelta = xpTURN.Klotho.Diagnostics.FaultInjection.ForceTickOffsetDelta;
                if (tickDelta != 0)
                {
                    int shifted = targetTick + tickDelta;
                    _logger?.ZLogWarning($"[FaultInjection][SD] ForceTickOffset: cmd.Tick {targetTick} → {shifted} (delta={tickDelta}, type={cmdBase.GetType().Name})");
                    targetTick = shifted;
                }
#endif

                // Prevent non-monotonic cmd.Tick when _recommendedExtraDelay decreases on Reconnect / mid-match.
                // Applied after fault injection to keep production-ordering invariant in fault tests.
                // Strict-less-than: same-tick multiple cmds are legal in lockstep, so equal targetTick passes through.
                bool clampEngaged = targetTick < _lastSentCmdTick;
                if (clampEngaged)
                {
                    int clamped = _lastSentCmdTick;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.ZLogDebug($"[KlothoEngine] cmd.Tick monotonic clamp: computed={targetTick}, clamped={clamped}");
#endif
                    targetTick = clamped;
                }

                // Forward gap fill — when _recommendedExtraDelay increases mid-match, cmd.Tick jumps
                // forward leaving the in-between ticks with no local cmd. Subsequent frames target
                // later ticks and never revisit the gap, so the chain stalls at the first missing
                // tick. Emit empty cmds across the gap to keep the chain unbroken. The per-call
                // extraDelay margin is preserved (e.g., spawn cmd recovery lead).
                if (_simConfig.Mode != NetworkMode.ServerDriven && _lastSentCmdTick >= 0)
                {
                    int fillEnd = targetTick - extraDelay - 1;
                    int fillStart = _lastSentCmdTick + 1;
                    if (fillStart <= fillEnd)
                    {
                        for (int t = fillStart; t <= fillEnd; t++)
                        {
                            var fillEmpty = CommandPool.Get<EmptyCommand>();
                            fillEmpty.PlayerId = LocalPlayerId;
                            fillEmpty.Tick = t;
                            _networkService.SendCommand(fillEmpty);
                            _lastSentCmdTick = t;
                        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                        _logger?.ZLogInformation($"[KlothoEngine][GapFill] Forward gap filled: [{fillStart}, {fillEnd}], count={fillEnd - fillStart + 1}");
#endif
                    }
                }

                if (targetTick > _lastSentCmdTick)
                    _lastSentCmdTick = targetTick;

                // LagReductionLatency: first non-clamped InputCommand after an ApplyExtraDelay decrease
                // marks the natural resolution. Emit one-shot, then clear pending state.
                if (_lagReductionPending && !clampEngaged)
                {
                    int expectedTicks = _lagReductionPrevDelay - _lagReductionNewDelay;
                    int actualTicks = CurrentTick - _lagReductionStartTick;
                    _logger?.ZLogInformation(
                        $"[Metrics][LagReductionLatency] {{\"prevDelay\":{_lagReductionPrevDelay},\"newDelay\":{_lagReductionNewDelay},\"expectedTicks\":{expectedTicks},\"actualTicks\":{actualTicks}}}");
                    _lagReductionPending = false;
                }

                cmdBase.Tick = targetTick;
            }

            // ServerDriven mode places it in the local buffer and then sends to the server.
            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                _inputBuffer.AddCommand(command);
                _serverDrivenNetwork.SendClientInput(targetTick, command);
                return;
            }

            // P2P broadcasts over the network.
            _networkService.SendCommand(command);
        }

        public void Stop()
        {
            if (_replaySystem.IsRecording)
            {
                int totalTicks = CurrentTick + _simConfig.InputDelayTicks;
                _replaySystem.StopRecording(totalTicks);
            }

            State = KlothoState.Finished;

            if (_networkService != null)
            {
                if (_simConfig.Mode == NetworkMode.ServerDriven)
                {
                    _networkService.OnGameStart -= HandleGameStart;
                    _networkService.OnFullStateRequested -= HandleFullStateRequested;
                    _networkService.OnLateJoinPlayerAdded -= HandleLateJoinPlayerAdded;

                    if (_serverDrivenNetwork != null && !_serverDrivenNetwork.IsServer)
                        _networkService.OnCountdownStarted -= HandleCountdownStarted;

                    if (_serverDrivenNetwork != null)
                    {
                        _serverDrivenNetwork.OnVerifiedStateReceived -= HandleVerifiedStateReceived;
                        _serverDrivenNetwork.OnInputAckReceived -= HandleInputAckReceived;
                        _serverDrivenNetwork.OnServerFullStateReceived -= HandleServerDrivenFullStateReceived;

                        if (!_serverDrivenNetwork.IsServer)
                        {
                            _serverDrivenNetwork.OnBootstrapBegin -= HandleBootstrapBegin;
                            _serverDrivenNetwork.OnCommandRejected -= HandleCommandRejected;
                        }
                    }
                }
                else
                {
                    _networkService.OnCommandReceived -= HandleCommandReceived;
                    _networkService.OnDesyncDetected -= HandleNetworkDesync;
                    _networkService.OnGameStart -= HandleGameStart;
                    _networkService.OnFrameAdvantageReceived -= HandleFrameAdvantage;
                    _networkService.OnFullStateRequested -= HandleFullStateRequested;
                    _networkService.OnFullStateReceived -= HandleFullStateReceived;
                    _networkService.OnLateJoinPlayerAdded -= HandleLateJoinPlayerAdded;
                }
            }
        }

        private bool CanAdvanceTick()
        {
            if (_inputBuffer.HasAllCommands(CurrentTick, _activePlayerIds.Count))
                return true;

            if (_disconnectedPlayerIds.Count > 0)
                OnDisconnectedInputNeeded?.Invoke(CurrentTick);

            return _inputBuffer.HasAllCommands(CurrentTick, _activePlayerIds.Count);
        }

        private void ExecuteTick()
        {
            // To avoid GC, fetch the command list directly from the buffer each tick.
            var commands = _inputBuffer.GetCommandList(CurrentTick);

            if (commands.Count > 0)
            {
                for (int di = 0; di < commands.Count; di++)
                {
                    //_logger?.ZLogInformation($"[KlothoEngine] ExecuteTick: tick={CurrentTick}, cmd[{di}] typeId={commands[di].CommandTypeId} player={commands[di].PlayerId}");
                }
            }

            if (_replaySystem.IsRecording)
            {
                _replaySystem.RecordTick(CurrentTick, commands);
            }

            // Save per-tick state snapshots for use in rollback.
            SaveSnapshot(CurrentTick);

#if DEBUG
            // SyncTest mode performs Tick + Rollback + re-simulation inside the runner.
            if (_syncTestEnabled && _syncTestRunner != null)
            {
                _eventCollector.BeginTick(CurrentTick);
                var syncResult = _syncTestRunner.RunTick(CurrentTick, commands);
                if (syncResult.Status == SyncTestStatus.Fail)
                {
                    _logger?.ZLogError($"[KlothoEngine] SyncTest: Desync detected at tick {syncResult.Tick}! Expected: 0x{syncResult.ExpectedHash:X16}, Got: 0x{syncResult.ActualHash:X16}");
                }
            }
            else
#endif
            {
                _eventCollector.BeginTick(CurrentTick);
                _simulation.Tick(commands);
            }

            // Store collected events into the tick buffer.
            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            // Periodic state-hash synchronization check.
            if (_networkService != null && CurrentTick % _simConfig.SyncCheckInterval == 0)
            {
                // If no desync was reported on the previously pending sync tick, promote it to matched.
                if (_pendingSyncCheckTick >= 0 && !_desyncDetectedForPending)
                {
                    _lastMatchedSyncTick = _pendingSyncCheckTick;
                    _consecutiveDesyncCount = 0;
                }

                long hash = _simulation.GetStateHash();
                _localHashes[CurrentTick] = hash;
                _networkService.SendSyncHash(CurrentTick, hash);
                //_logger?.ZLogDebug($"[KlothoEngine] SyncCheck: tick={CurrentTick}, hash=0x{hash:X16}");

                _pendingSyncCheckTick = CurrentTick;
                _desyncDetectedForPending = false;
            }

            // TimeSync: update advantage history, idle input, and local tick.
            if (_timeSyncEnabled)
            {
                // A positive localAdv means local is ahead of remote.
                // AdvanceFrame takes (how far local is behind, how far remote is behind), so the sign is inverted.
                float localAdv = CalculateLocalAdvantage();
                _timeSync.AdvanceFrame(-localAdv, localAdv);
                _networkService.SetLocalTick(CurrentTick);

                bool hasActiveInput = false;
                for (int i = 0; i < commands.Count; i++)
                {
                    if (commands[i].PlayerId == LocalPlayerId &&
                        commands[i].CommandTypeId != EmptyCommand.TYPE_ID)
                    {
                        hasActiveInput = true;
                        break;
                    }
                }
                _timeSync.RecordInput(!hasActiveInput);
            }

            // Advance the verified chain by one only when there is no prediction gap.
            // If a predicted tick was inserted, TryAdvanceVerifiedChain handles it later.
            if (CurrentTick == _lastVerifiedTick + 1)
            {
                _lastVerifiedTick = CurrentTick;
                OnFrameVerified?.Invoke(CurrentTick);
                FireVerifiedInputBatch();
            }

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            FrameState state = executedTick <= _lastVerifiedTick
                ? FrameState.Verified : FrameState.Predicted;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, state);

            DispatchTickEvents(executedTick, state);

            CleanupOldData();
        }

        private void ExecuteTickWithPrediction()
        {
            SaveSnapshot(CurrentTick);

            // Reuse a cached list to avoid GC allocations.
            _tickCommandsCache.Clear();

            var received = _inputBuffer.GetCommandList(CurrentTick);
            for (int i = 0; i < received.Count; i++)
            {
                _tickCommandsCache.Add(received[i]);
            }

            // For players whose input has not been received, predict and insert based on recent history.
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int playerId = _activePlayerIds[pi];
                if (!_inputBuffer.HasCommandForTick(CurrentTick, playerId))
                {
                    GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT);
                    var predicted = _inputPredictor.PredictInput(playerId, CurrentTick, _previousCommandsCache);
                    _tickCommandsCache.Add(predicted);
                    _pendingCommands.Add(predicted);
                }
            }

            _eventCollector.BeginTick(CurrentTick);
            _tickCommandsCache.Sort(s_commandComparer);
            _simulation.Tick(_tickCommandsCache);

            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            // TimeSync must keep advancing even during prediction. If it halts, SenderTick stops
            // and remote peers' TimeSync forcibly slows down.
            if (_timeSyncEnabled)
            {
                float localAdv = CalculateLocalAdvantage();
                _timeSync.AdvanceFrame(-localAdv, localAdv);
                _networkService.SetLocalTick(CurrentTick);

                bool hasActiveInput = false;
                for (int i = 0; i < _tickCommandsCache.Count; i++)
                {
                    if (_tickCommandsCache[i].PlayerId == LocalPlayerId &&
                        _tickCommandsCache[i].CommandTypeId != EmptyCommand.TYPE_ID)
                    {
                        hasActiveInput = true;
                        break;
                    }
                }
                _timeSync.RecordInput(!hasActiveInput);
            }

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, FrameState.Predicted);

            // During prediction, dispatch only Regular events; Synced events are buffered.
            DispatchTickEvents(executedTick, FrameState.Predicted);
        }

        /// <summary>
        /// Fills a cached list with previous commands to avoid GC.
        /// </summary>
        private void GetPreviousCommands(int playerId, int count, int fromTick = -1)
        {
            _previousCommandsCache.Clear();
            int startTick = fromTick >= 0 ? fromTick - 1 : CurrentTick - 1;
            for (int t = startTick; t >= 0 && _previousCommandsCache.Count < count; t--)
            {
                var cmd = _inputBuffer.GetCommand(t, playerId);
                if (cmd != null)
                    _previousCommandsCache.Add(cmd);
            }
        }

        private void HandleCommandReceived(ICommand command)
        {
            _inputBuffer.AddCommand(command);

            // To avoid GC, find the matching prediction with a manual loop instead of a lambda.
            ICommand predicted = null;
            for (int i = 0; i < _pendingCommands.Count; i++)
            {
                var c = _pendingCommands[i];
                if (c.Tick == command.Tick && c.PlayerId == command.PlayerId)
                {
                    predicted = c;
                    break;
                }
            }

            if (predicted != null)
            {
                _inputPredictor.UpdateAccuracy(predicted, command);
                _pendingCommands.Remove(predicted);

                // Only request rollback when the prediction differs from the actual input.
                if (!CommandDataEquals(predicted, command))
                {
                    _logger?.ZLogWarning($"[KlothoEngine] PredictionMismatch: Prediction mismatch tick={command.Tick}, player={command.PlayerId}, predicted={predicted.CommandTypeId}, actual={command.CommandTypeId}");
                    RequestRollback(command.Tick);
                }
            }

            // If we were Paused and can now progress again, return to Running.
            if (State == KlothoState.Paused && CanAdvanceTick())
            {
                State = KlothoState.Running;
            }

            // Advance the verified chain only when there is no pending rollback.
            if (!_hasPendingRollback)
            {
                TryAdvanceVerifiedChain();
            }
        }

        private bool CommandDataEquals(ICommand a, ICommand b)
        {
            if (a.CommandTypeId != b.CommandTypeId) return false;
            int sizeA = a.GetSerializedSize();
            int sizeB = b.GetSerializedSize();
            if (sizeA != sizeB) return false;

            Span<byte> bufA = stackalloc byte[sizeA];
            Span<byte> bufB = stackalloc byte[sizeB];
            var writerA = new SpanWriter(bufA);
            var writerB = new SpanWriter(bufB);
            a.Serialize(ref writerA);
            b.Serialize(ref writerB);
            return bufA.Slice(0, writerA.Position).SequenceEqual(bufB.Slice(0, writerB.Position));
        }

        /// <summary>
        /// Enables the initial FullState wait flag at the SD Client's Countdown entry.
        /// Must be called before HandleGameStart so that server broadcasts arriving during Countdown
        /// are routed onto the initial FullState path.
        /// </summary>
        private void HandleCountdownStarted(long startTime)
        {
            _expectingInitialFullState = true;
        }

        private void HandleGameStart()
        {
            // After all players are confirmed, refresh the player count and ID list.
            _activePlayerIds.Clear();
            for (int i = 0; i < _networkService.Players.Count; i++)
                _activePlayerIds.Add(_networkService.Players[i].PlayerId);
            _randomSeed = _networkService.RandomSeed;
            _logger?.ZLogInformation($"[KlothoEngine] HandleGameStart: Game start: playerCount={_activePlayerIds.Count}");

            if (_simConfig.Mode == NetworkMode.ServerDriven)
                _lastServerVerifiedTick = 0;
            else
                EnableTimeSync();

            Start(); // Internally runs OnInitializeWorld -> SaveSnapshot(0)

            if (_simConfig.Mode == NetworkMode.ServerDriven && !_serverDrivenNetwork.IsServer)
            {
                ApplySDWarmUpLead();
            }

            _viewCallbacks?.OnGameStart(this);
            // At this point State=Running and entities already exist.
            // OnGameStart subscribers in game code call SetInitialStateSnapshot to populate the _cachedFullState cache.
            OnGameStart?.Invoke();

            // SD Server broadcasts the authoritative tick-0 state to all remote SD Clients as a bootstrap.
            if (_simConfig.Mode == NetworkMode.ServerDriven && _serverDrivenNetwork.IsServer)
            {
                // Always re-serialize: pre-game late-join (e.g. spectator joining during Lobby/Sync)
                // may have populated _cachedFullState with the empty pre-OnInitializeWorld state via
                // HandleFullStateRequested, leaving _cachedFullStateTick=0. The post-OnInitializeWorld
                // state is the authoritative tick-0 broadcast.
                {
                    var (data, hash) = _simulation.SerializeFullStateWithHash();
                    _cachedFullState = data;
                    _cachedFullStateHash = hash;
                    _cachedFullStateTick = 0;
                }
                _serverDrivenNetwork.BroadcastFullState(0, _cachedFullState, _cachedFullStateHash);
                _logger?.ZLogInformation($"[KlothoEngine][SD] Initial FullState broadcast: size={_cachedFullState.Length}, hash=0x{_cachedFullStateHash:X16}");

                // Diagnostic — per-component hash breakdown for desync root-cause analysis.
                // Debug level: steady-state, not surfaced in normal logs.
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                    ecsSimDiag.LogComponentHashes(_logger, "ServerInit", atDebugLevel: true);
            }
        }

        private void CleanupOldData()
        {
            // Never wipe data at ticks the chain has not advanced past — those entries are
            // still required to resume chain advance. Stall recovery (reconnect / catchup)
            // depends on inputs and pending events remaining intact through the stall window.
            int rawCleanupTick = CurrentTick - _simConfig.MaxRollbackTicks - CLEANUP_MARGIN_TICKS;
            int cleanupTick = System.Math.Min(rawCleanupTick, _lastVerifiedTick);
            if (cleanupTick > 0)
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                // Diagnostic — log InputBuffer entries about to be wiped while still beyond
                // chain advance reach (t > _lastVerifiedTick). Surfaces host self-wipe during
                // P2P quorum stall — wiped player commands are unrecoverable.
                _inputBuffer.LogPendingWipe(cleanupTick, _lastVerifiedTick, CurrentTick);
#endif

                _inputBuffer.ClearBefore(cleanupTick);
                _networkService?.ClearOldData(cleanupTick);
                int eventCleanFrom = System.Math.Max(0, cleanupTick - CLEANUP_MARGIN_TICKS);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                // Diagnostic — log Synced events that are about to be wiped while still pending
                // dispatch (chain advance has not reached them). Indicates chain advance lag
                // exceeding (MaxRollbackTicks + 2*CLEANUP_MARGIN_TICKS) → events permanently lost.
                for (int t = eventCleanFrom; t < cleanupTick; t++)
                {
                    if (t <= _lastVerifiedTick) continue; // already chain-advanced — Synced was dispatched
                    var pendingEvts = _eventBuffer.GetEvents(t);
                    for (int ei = 0; ei < pendingEvts.Count; ei++)
                    {
                        var evt = pendingEvts[ei];
                        if (evt.Mode == EventMode.Synced)
                            _logger?.ZLogWarning($"[KlothoEngine][Cleanup] Pending Synced event WIPED: tick={t}, typeId={evt.EventTypeId}, _lastVerifiedTick={_lastVerifiedTick}, CurrentTick={CurrentTick}, lag={CurrentTick - _lastVerifiedTick}");
                    }
                }
#endif

                _eventBuffer.ClearRange(eventCleanFrom, cleanupTick);

                // Collect keys to remove in a cached list to avoid GC, then remove them all at once.
                _hashKeysToRemoveCache.Clear();
                foreach (var key in _localHashes.Keys)
                {
                    if (key < cleanupTick)
                        _hashKeysToRemoveCache.Add(key);
                }
                for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
                {
                    _localHashes.Remove(_hashKeysToRemoveCache[i]);
                }
            }
        }

    }
}
