using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
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
                if (_state == value) return;
                _state = value;
                _logger?.KInformation($"[KlothoEngine] State: {_state}");
                OnStateChanged?.Invoke(value);
            }
        }

        public event Action<KlothoState> OnStateChanged;

        public int CurrentTick { get; private set; }
        public int LocalPlayerId => _networkService?.LocalPlayerId ?? 0;
        public int TickInterval => _simConfig.TickIntervalMs;
        public int InputDelay => _simConfig.InputDelayTicks;
        public int RecommendedExtraDelay => Math.Clamp(_baselineExtraDelay + _reactiveExtraDelay, 0, _simConfig.MaxRollbackTicks / 2);

        public event Action OnGameStart;
        public event Action<int> OnPreTick;
        public event Action<int> OnTickExecuted;
        public event Action<int, FrameState> OnTickExecutedWithState;
        public event Action<long, long> OnDesyncDetected;
        public event Action<int, long, long> OnHashMismatch;
        public event Action<int, int> OnRollbackExecuted;
        public event Action<int, string> OnRollbackFailed;
        public event Action<int> OnFrameVerified;
        public event Action OnChainAdvanceBreak;
        public event Action<int, SimulationEvent> OnEventPredicted;
        public event Action<int, SimulationEvent> OnEventConfirmed;
        public event Action<int, SimulationEvent> OnEventCanceled;
        public event Action<int, SimulationEvent> OnSyncedEvent;
        public event Action<int, SimulationEvent, SyncedDivergenceKind> OnSyncedEventDivergence;
        public event Action<int, IMatchEndEvent> OnMatchEnded;
        public event Action<int> OnResyncCompleted;
        public event Action<AbortReason> OnMatchAborted;
        public event Action<ResetReason> OnMatchReset;
        public event Action OnResyncFailed;
        // Invoked only under DEVELOPMENT_BUILD || UNITY_EDITOR (cleanup diagnostics).
        // Suppress CS0067 in release/server builds where neither symbol is defined.
#pragma warning disable CS0067
        public event Action<int, int, WipeKind> OnPendingWipe;
#pragma warning restore CS0067
        public event Action<int, int, RejectionReason> OnCommandRejected;
        public event Action<int, int, byte[], int> OnVerifiedInputBatchReady;

        /// <summary>
        /// Fired when player config data is received (playerId, firstTime).
        /// </summary>
        public event Action<int, bool> OnPlayerConfigReceived;

        private ISimulationCallbacks _simulationCallbacks;
        private IViewCallbacks _viewCallbacks;
        private CommandSender _commandSender;

        // Extra InputDelay split. _baselineExtraDelay = server-push authority (absolute
        // SET, from RTT-based push + LateJoin/Reconnect/Sync seed). _reactiveExtraDelay = client-local
        // additive correction (PastTick/rollback-burst escalation, self-decaying), preserved across push.
        // Public RecommendedExtraDelay = clamped sum, bounded by MaxRollbackTicks/2.
        private int _baselineExtraDelay;
        private int _reactiveExtraDelay;

        // Last cmd.Tick sent — used for monotonic clamp on Reconnect-induced delay decreases.
        // Sentinel int.MinValue: no prev cmd. First cmd trivially passes (targetTick > int.MinValue + 1).
        private int _lastSentCmdTick = int.MinValue;

        // Late-join spawn floor. A late-joining guest's own cmd.Tick must not precede its joinTick —
        // the PlayerJoinCommand at joinTick creates the participant slot and fires OnPlayerJoinedWorld,
        // which seeds join-time deterministic state (e.g. loadout seeds). A command landing earlier
        // consumes pre-seed state identically on every peer: no desync, but join-time guarantees
        // (entitlement enforcement) are silently bypassed. 0 = no floor (host / normal join / SD).
        // Set once via SetLateJoinCommandFloor before catchup completes; inert once
        // CurrentTick + delays naturally passes it.
        private int _lateJoinCommandFloorTick;

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
                if (aIsSys != bIsSys) return aIsSys ? 1 : -1;   // players first, system/reliable last
                // system/reliable phase: shared total-order key chain (single source of truth) — must
                // match InputBuffer.CompareSystemCommands so server-sim and client-sim agree.
                if (aIsSys) return CommandOrdering.Compare(a, b);
                return a.PlayerId.CompareTo(b.PlayerId);   // player phase
            }
        }

        private ISimulation _simulation;
        private bool _worldInitInProgress;

        /// <summary>
        /// The registered sliced-rebake driver, or null when the game does not use one.
        ///
        /// <para><b>Resolved in <c>Initialize</c>.</b> Systems are registered by the session or the
        /// room manager BEFORE the engine is initialized on every path, so the lookup always finds
        /// one. A game that registers the driver AFTER Initialize is simply not self-wired, and that
        /// precondition is pinned by a test rather than defended at runtime.</para>
        ///
        /// <para><b>Initialize can run more than once.</b> <c>Stop</c> clears the re-entry guard and
        /// the reconnect path uses that — so this re-resolves each time rather than assuming a single
        /// call. Re-resolving is also what keeps it correct if a different simulation is ever handed
        /// in. What must NOT repeat is the claim: see <see cref="ResolveNavRebakeDriver"/>.</para>
        ///
        /// <para><b>Opt-in is the registration itself.</b> Register the driver and the engine paces
        /// its slices; do not, and every one of these points is a null check. There is no opt-out —
        /// a game that wants to pace slices itself no longer can.</para>
        /// </summary>
        private xpTURN.Klotho.Deterministic.Navigation.FPNavMeshRebakeDriver _navRebakeDriver;

        /// <summary>
        /// Finds the driver and takes the heartbeat claim. Called from both <c>Initialize</c> bodies.
        ///
        /// <para><b>The claim's return value is deliberately ignored.</b> The engine advances either
        /// way (see <c>Update</c>). Claiming still matters for two reasons: a game asking for it is
        /// refused, which is how a legacy manual wiring declines to subscribe; and the driver's
        /// <c>HeartbeatClaimAttempts</c>/<c>SliceHeartbeatWired</c> counters stay meaningful — with
        /// nobody claiming they would read 0/false and a live match would lose the only signal that
        /// says whether pacing is wired at all.</para>
        ///
        /// <para>Claiming HERE is also what makes double-advance structurally impossible: Initialize
        /// runs before <c>OnInitializeWorld</c>, so the engine holds the claim before any game door
        /// can ask for it.</para>
        /// </summary>
        private void ResolveNavRebakeDriver()
        {
            var resolved = (_simulation as xpTURN.Klotho.ECS.EcsSimulation)
                ?.GetSystem<xpTURN.Klotho.Deterministic.Navigation.FPNavMeshRebakeDriver>();

            // Same driver as before — a reconnect (Stop then Initialize) lands here. Claiming again
            // would be harmless to behaviour and wrong for READING one: the claim counter is how a
            // live match tells "the engine owns pacing" (1) from "a game is still wiring it by hand"
            // (2), and a reconnect bumping it turns a healthy peer into a false positive.
            if (ReferenceEquals(resolved, _navRebakeDriver))
                return;

            _navRebakeDriver = resolved;
            if (_navRebakeDriver == null)
                return;

            _navRebakeDriver.TryClaimSliceHeartbeat();
            _logger?.KInformation(
                $"[KlothoEngine] nav rebake driver self-wired: frame pacing and derivative " +
                $"corrections are the engine's from here");
            WarnOnUnfoldedRebakeInputs();
        }

        /// <summary>
        /// Reports the fingerprint sources a rebaking game is missing, once, where the driver is
        /// resolved.
        ///
        /// <para><b>Why the engine says this at all.</b> Registering the driver is now the whole
        /// wiring, so the one remaining thing a game can forget leaves no trace: the state hash
        /// covers components, and neither the mesh a rebake installed nor the table it was carved
        /// from is a component. Both hooks are OPTIONAL and the static fingerprint folds 0 for a
        /// missing one — so an omission looks exactly like a game that has nothing to fold, and the
        /// peer it disagrees with never says so either.</para>
        ///
        /// <para><b>Only for a game that rebakes.</b> A static navmesh cannot drift from a shape
        /// table it never reads, and a warning that a reader cannot act on is a warning they learn to
        /// scroll past — which would cost the ones below their meaning.</para>
        ///
        /// <para><b>A warning, not an error.</b> Nothing has diverged here; what is missing is the
        /// net that would report it if it did. Live-match judgement treats a driver <c>KError</c> as
        /// a stop condition, and promoting this would make every game that folds these inputs
        /// somewhere else unjudgeable.</para>
        /// </summary>
        private void WarnOnUnfoldedRebakeInputs()
        {
            var ecs = _simulation as xpTURN.Klotho.ECS.EcsSimulation;
            if (ecs == null)
                return;

            if (ecs.GetSystem<xpTURN.Klotho.Deterministic.Navigation.INavFingerprintSource>() == null)
                _logger?.KWarning(
                    $"[KlothoEngine] a nav rebake driver is registered but no INavFingerprintSource " +
                    $"is — the installed mesh is then absent from the static environment " +
                    $"fingerprint, so a rebake that ran on one peer and not the other stays " +
                    $"invisible (the state hash covers components, not the mesh). " +
                    $"FPNavAgentSystem implements it; registering the agent system is usually all " +
                    $"this takes");

            if (ecs.GetSystem<xpTURN.Klotho.ECS.IGameFingerprintSource>() == null)
                _logger?.KWarning(
                    $"[KlothoEngine] a nav rebake driver is registered but no IGameFingerprintSource " +
                    $"is — the inputs that DECIDE the mesh (the shape catalog a placement indexes " +
                    $"into, the delay that picks its tick) are outside every hash, so two builds " +
                    $"that disagree about them carve different meshes from identical commands. Fold " +
                    $"them into GetGameFingerprint. If you check them at load through the match " +
                    $"config instead, this line is a known false positive");
        }

        public ISimulation Simulation => _simulation;
        public IKLogger Logger => _logger;

        private IKlothoNetworkService _networkService;
        private IKLogger _logger;
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
        private SimpleInputPredictor _inputPredictor;

        /// <inheritdoc />
        public float PredictionAccuracy =>
            _simConfig?.Mode == NetworkMode.ServerDriven
                ? -1f // N/A sentinel: SD re-prediction never feeds UpdateAccuracy, so the predictor
                      // would otherwise report a false 1.0. -1 (outside the 0~1 range) means "not measured".
                : _inputPredictor?.Accuracy ?? 1.0f;

        private float _accumulator;
        private bool _consumePendingDeltaTime;
        private int _lastVerifiedTick = -1;
        private int _lastBatchedTick = -1;
        private readonly List<int> _activePlayerIds = new List<int>();

        private readonly List<ICommand> _pendingCommands = new List<ICommand>();
        // Per-tick stashed hashes: state hash + command digest of the executed set. The command digest
        // rides along so the deferred send classifies a desync (input vs state divergence) without
        // recomputing the command list, and a rollback resim overwrites both together (kept consistent).
        private readonly Dictionary<int, (long state, long cmd)> _localHashes = new Dictionary<int, (long, long)>();

        // Diagnostic hash-history sink. Non-null only when the simulation is an EcsSimulation AND
        // ISimulationConfig.DiagnosticHistoryTicks > 0. Cached so the per-tick cadence sites avoid a
        // repeated interface cast; a null-conditional call is a no-op when diagnostics are off.
        private xpTURN.Klotho.ECS.EcsSimulation _diagHistorySim;

        // Component-memory peak sampler. Non-null only when the simulation is an EcsSimulation AND
        // ISimulationConfig.ComponentMemoryPeakSampling is set. Per-peer-local diagnostic (like
        // _diagHistorySim): the flag is not in SimulationConfigMessage, so joining SD/P2P clients rebuild
        // config without it and never arm — measure on the authoritative peer / replay instead.
        // internal so unit tests can assert arming/peaks via InternalsVisibleTo(xpTURN.Klotho.Tests).
        internal xpTURN.Klotho.ECS.Diagnostics.ComponentMemoryPeakSampler _memSampler;

        // Idempotency guard for the per-system perf dump at Stop. Unlike _memSampler (nulled after
        // dump), the perf monitor is owned by EcsSimulation/SystemRunner and its config gate stays
        // true after Stop, so a flag is needed to keep a double Stop from re-dumping.
        private bool _sysPerfDumped;

        // Reusable lists to avoid GC.
        private readonly List<ICommand> _tickCommandsCache = new List<ICommand>();
        private readonly List<ICommand> _previousCommandsCache = new List<ICommand>();
        private readonly List<int> _hashKeysToRemoveCache = new List<int>();
        private readonly List<int> _savedTicksCache = new List<int>();

        // Tracks the last good sync tick to roll back to when desync is detected.
        // Promoted event-based: a matched hash comparison advances it monotonically;
        // a tick that ever mismatched is vetoed (_lastMismatchedSyncTick). Order-independent:
        // a mismatch arriving after a same-tick match demotes the anchor to _prevMatchedSyncTick
        // (1-step history) before rung-1 reads it, so rung-1 never rolls back to a diverged tick
        // regardless of match/mismatch arrival order.
        private int _lastMatchedSyncTick;
        private int _prevMatchedSyncTick;
        private int _lastMismatchedSyncTick = -1;
        // Check ticks executed speculatively: hash stashed in _localHashes, sent when the
        // verified chain crosses the tick (deferred send — predictions confirmed byte-equal).
        private readonly System.Collections.Generic.HashSet<int> _deferredHashSendTicks = new System.Collections.Generic.HashSet<int>();

        private ICommandFactory _commandFactory;

        private DynamicInputDelayPolicy _dynamicInputDelayPolicy;
        private ReliableCommandTracker _reliableTracker;

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
        // Drift-proportional render timescale computed by AdvanceVerifiedRenderTime, exposed via
        // RenderClock so the view can read catchup/slowdown. Reset to 1f at the start of
        // each AdvanceVerifiedRenderTime so an early-return frame (no drift computed) reports 1f
        // rather than the previous frame's value.
        private float  _verifiedRenderTimescale = 1f;

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
                    Timescale = _verifiedRenderTimescale,
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
            // Default to 1f before any early-return so a frame that computes no drift reports 1f
            // (overwritten with the clamped value once drift is computed below).
            _verifiedRenderTimescale = 1f;

            if (_lastVerifiedTick < 0) return;

            // Replay uses the tick interval from the time of recording (mirrors the RenderClock getter). Falls back to the current setting if no recording data is available.
            int tickMs = _isReplayMode
                ? (_replaySystem?.CurrentReplayData?.Metadata?.TickIntervalMs ?? _simConfig.TickIntervalMs)
                : _simConfig.TickIntervalMs;
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

            // Expose the smooth-converge multiplier. NOTE: this is the per-frame
            // [0.5,2.0] convergence rate; it does NOT reflect the >10-tick instant snap below.
            _verifiedRenderTimescale = timescale;

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

        public ECS.Frame InitFrame
        {
            get
            {
                if (!_worldInitInProgress)
                    throw new InvalidOperationException(
                        "InitFrame is only valid during ISimulationCallbacks.OnInitializeWorld. " +
                        "Use PredictedFrame for render-time reads.");
                if (_simulation is ECS.EcsSimulation ecsSim)
                    return ecsSim.Frame;
                throw new InvalidOperationException("InitFrame requires an EcsSimulation.");
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
        /// Skipped in states without a render path: server/replay/awaiting resync.
        /// </summary>
        private void CapturePreviousUpdatePredicted()
        {
            if (_isReplayMode || IsServer
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
        /// Per-player opaque entitlement blob, or null when none. Server-authoritative: on the SD
        /// server this returns the stored redeem outcome; on the SD client it is always null (the seed effect
        /// arrives baked into the Initial FullState). The game reads this during tick-0 world init
        /// (OnInitializeWorld, before SaveSnapshot(0)) to seed the simulation deterministically — the server's
        /// result then propagates to every client via the existing Initial FullState / full-state resync.
        /// </summary>
        // The server-driven path reads the server's stored redeem outcome; the P2P path reads each peer's
        // independently-verified entitlement off the concrete network service via a cast, which avoids an
        // interface change so existing network-service mocks are unaffected. Every P2P peer holds the same
        // signed bytes, so seeding the world from them in OnInitializeWorld is deterministic across peers.
        public byte[] GetPlayerEntitlement(int playerId)
            => _serverDrivenNetwork?.GetPlayerEntitlement(playerId)
               ?? (_networkService as KlothoNetworkService)?.GetPlayerEntitlement(playerId);

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

        // Canonical command digest of a tick's executed command set. Reuses the existing per-command
        // serialization over the already-canonically-ordered list (GetCommandList / s_commandComparer),
        // so an equal digest across peers means an identical command set. Returns 0 when no factory is
        // wired, in which case both peers report 0 (same build) and classification falls back to
        // state-only. Called only at check ticks / verified compares — not a per-tick steady cost.
        private long ComputeCommandDigest(List<ICommand> executedCommands)
        {
            if (_commandFactory == null || executedCommands == null) return 0;
            int size = _commandFactory.GetSerializedCommandsSize(executedCommands);
            byte[] buf = StreamPool.GetBuffer(size);
            try
            {
                int written = _commandFactory.SerializeCommandsTo(buf.AsSpan());
                return (long)xpTURN.Klotho.Deterministic.FPHash.HashBytes(
                    xpTURN.Klotho.Deterministic.FPHash.FNV_OFFSET, new ReadOnlySpan<byte>(buf, 0, written));
            }
            finally
            {
                StreamPool.ReturnBuffer(buf);
            }
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

        // Re-init guard: a second Initialize without Stop double-subscribes the
        // network handlers, so the same arrival instance reaches AddCommand twice — the
        // double-dispatch precondition. Stop() resets the flag, but passing the guard after Stop
        // only means re-subscription is clean: engine reuse (Stop -> Initialize -> Start) is
        // unsupported — Initialize does not reset session state (_pendingVerifiedQueue,
        // _inputBuffer, tick counters); create a new engine per session.
        private bool _initialized;

        private void ThrowIfAlreadyInitialized()
        {
            if (_initialized)
                throw new InvalidOperationException(
                    "KlothoEngine.Initialize called again without Stop — this double-subscribes network handlers " +
                    "(duplicate command dispatch). Create a new engine per session, or call Stop() first.");
            _initialized = true;
        }

        // Arm the component-memory peak sampler when opted in. Called from BOTH Initialize bodies
        // (network + network-less) so replay/spectator are covered too — the network-less body has no
        // hash-history arming, so a single call site would silently miss those paths. _simConfig is
        // ctor-set, so the gate is valid in both bodies. Read-only over verified frames (zero
        // sim/determinism impact); dumped + disposed in Stop.
        private void ArmComponentMemorySampler()
        {
            if (_simConfig.ComponentMemoryPeakSampling && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsMem)
                _memSampler = new xpTURN.Klotho.ECS.Diagnostics.ComponentMemoryPeakSampler(this, ecsMem);
        }

        // Arm the per-system perf monitor when opted in. Called from BOTH Initialize bodies (network +
        // network-less) so replay/spectator are covered too. No engine-side state to hold — the monitor
        // is owned by SystemRunner (only the Stop-dump idempotency flag lives here); no Dispose needed.
        // Determinism-neutral (records only into its own struct array); dumped at Stop.
        // First N executions/system folded into the perf monitor's warmup buckets (excluded from the
        // steady avg/peak/sum) so first-tick JIT, lazy init, and pool/list growth don't muddy the
        // allocation-regression signal. ~120 ticks ≈ 3s at 40Hz — well past those one-time costs.
        private const int SystemPerfWarmupExecutions = 120;

        private void ArmSystemPerfMonitor()
        {
            if (_simConfig.SystemPerfMonitoring && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSys)
                ecsSys.EnableSystemPerfMonitor(SystemPerfWarmupExecutions);
        }

        public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, IKLogger logger)
        {
            ThrowIfAlreadyInitialized();

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
                logger?.KError(
                    $"[KlothoEngine] Config validation failed from authoritative source — proceeding with deviation: {validateError}");
            }

            _simulation = simulation;
            _networkService = networkService;
            _logger = logger;
            ResolveNavRebakeDriver();
            _dispatcher = new EventDispatcher(logger, _simConfig.EventDispatchWarnMs);

            if (_simConfig.Mode == NetworkMode.ServerDriven && _simConfig.InputDelayTicks < 2)
                _logger?.KWarning(
                    $"[KlothoEngine] InputDelayTicks={_simConfig.InputDelayTicks} below recommended minimum of 2 — increased jitter risk under network spikes.");
            if (_simConfig.Mode == NetworkMode.ServerDriven && _simConfig.SDInputLeadTicks >= _simConfig.MaxRollbackTicks)
                _logger?.KWarning(
                    $"[KlothoEngine] SDInputLeadTicks={_simConfig.SDInputLeadTicks} >= MaxRollbackTicks={_simConfig.MaxRollbackTicks} — clamped to {_simConfig.GetEffectiveSDInputLeadTicks()} (prediction lead is bounded by the snapshot ring)." );
            if (_simConfig.Mode == NetworkMode.P2P && _simConfig.SyncCheckInterval * 2 > _simConfig.MaxRollbackTicks)
                _logger?.KWarning(
                    $"[KlothoEngine] SyncCheckInterval={_simConfig.SyncCheckInterval} exceeds MaxRollbackTicks/2 — clamped to {_simConfig.GetEffectiveSyncCheckInterval()} so a desync rollback to the last matched anchor stays within the rollback window.");
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
                _logger?.KInformation($"[KlothoEngine][Roster] Initialize: PlayerCount={_activePlayerIds.Count}, active=[{sb}], LocalPlayerId={networkService.LocalPlayerId}, IsHost={networkService.IsHost}");
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
                    // Pause the FullState retry/abort timer while a reconnect is in progress, and
                    // terminate if the reconnect ultimately fails — so the shorter FullState abort
                    // window does not race the (longer) reconnect. These events live on
                    // IKlothoNetworkService, the same instance as _serverDrivenNetwork.
                    _networkService.OnReconnecting += HandleSdReconnecting;
                    _networkService.OnReconnectFailed += HandleSdReconnectFailed;
                }
            }
            else
            {
                _networkService.OnCommandReceived += HandleCommandReceived;
                _networkService.OnDesyncDetected += HandleNetworkDesync;
                _networkService.OnSyncHashCompared += HandleSyncHashCompared;
                _networkService.OnGameStart += HandleGameStart;
                _networkService.OnFullStateRequested += HandleFullStateRequested;
                _networkService.OnFullStateReceived += HandleFullStateReceived;
                _networkService.OnLateJoinPlayerAdded += HandleLateJoinPlayerAdded;
                _networkService.OnResyncFailureReported += HandleResyncFailureReported;
                _networkService.OnMatchAbortReceived += HandleMatchAbortReceived;
                OnHashMismatch += HandleHashMismatchForCorrectiveReset;

                _timeSync = new TimeSyncService();
                _networkService.OnFrameAdvantageReceived += HandleFrameAdvantage;
            }

            _simulation.OnPlayerJoinedNotification += HandlePlayerJoinedNotification;
            _simulation.Initialize();

            // Arm the diagnostic hash-history ring when configured. Diagnostic-only and local, so it
            // is not part of SimulationConfigMessage; each peer honors its own DiagnosticHistoryTicks.
            if (_simConfig.DiagnosticHistoryTicks > 0 && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsDiag)
            {
                ecsDiag.SetHashHistoryCapacity(_simConfig.DiagnosticHistoryTicks);
                _diagHistorySim = ecsDiag;
            }

            ArmComponentMemorySampler();
            ArmSystemPerfMonitor();

            // The probe rides on top of the diagnostic hash-history ring: it serves answers from it and captures it at
            // detection. A service without the probe surface leaves this unwired and diagnosis stays local.
            SubscribeDesyncProbe();

            // Event system. SD server collects only Synced events (e.g. CommandRejectedSimEvent) for
            // network-layer unicast feedback; Regular events have no server-side subscribers and are
            // dropped at RaiseEvent to avoid GC churn. Other modes use the full collector.
            _eventBuffer = new EventBuffer(SnapshotCapacity, _logger);
            if (_simConfig.Mode == NetworkMode.ServerDriven && _serverDrivenNetwork != null && _serverDrivenNetwork.IsServer)
                _eventCollector = new SyncedOnlyEventCollector();
            else
                _eventCollector = new EventCollector();
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.Frame.EventRaiser = _eventCollector;

            _logger?.KInformation($"[KlothoEngine] WarmupRegistry running");
            WarmupRegistry.RunAll();

            _dynamicInputDelayPolicy = new DynamicInputDelayPolicy(this, _logger);
            _dynamicInputDelayPolicy.Attach();

            _reliableTracker = new ReliableCommandTracker(this, _logger);
            _reliableTracker.Attach();

            State = KlothoState.WaitingForPlayers;
        }

        public int RandomSeed => _randomSeed;

        public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, IKLogger logger,
            ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null)
        {
            _simulationCallbacks = simulationCallbacks;
            _viewCallbacks = viewCallbacks;
            _commandSender = new CommandSender(this);
            Initialize(simulation, networkService, logger);
        }

        /// <summary>
        /// Network-less initialization with callbacks — replay-playback sessions.
        /// Mirrors the network overload's callback wiring without creating any network
        /// subscription; chains to the 2-arg body, which owns the re-init guard.
        /// </summary>
        public void Initialize(ISimulation simulation, IKLogger logger,
            ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null)
        {
            _simulationCallbacks = simulationCallbacks;
            _viewCallbacks = viewCallbacks;
            _commandSender = new CommandSender(this);
            Initialize(simulation, logger);
        }

        public void Initialize(ISimulation simulation, IKLogger logger)
        {
            // Separate body from the network overload — guard it directly (no network
            // subscriptions here, but the same re-entry would double-attach _reliableTracker).
            ThrowIfAlreadyInitialized();

            _simulation = simulation;
            _logger = logger;
            ResolveNavRebakeDriver();
            (_inputBuffer as InputBuffer)?.SetLogger(logger);

            _simulation.Initialize();

            ArmComponentMemorySampler();
            ArmSystemPerfMonitor();

            _eventBuffer = new EventBuffer(SnapshotCapacity, _logger);
            _eventCollector = new EventCollector();
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.Frame.EventRaiser = _eventCollector;

            // Spectator path — tracker exists for symmetry, but spectator never calls IssueOnce so
            // outstanding handles stay 0 and the tick hook is effectively no-op.
            _reliableTracker = new ReliableCommandTracker(this, _logger);
            _reliableTracker.Attach();
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
#if KLOTHO_FAULT_INJECTION
            // Scenario C: arm a client-side hash divergence on the targeted SD client so
            // its resim of ForceClientDesyncAtTick mismatches the server's verified hash → desync-resync
            // FullStateRequest. Paired with DropFullStateResponsePlayerIds to exercise the recovery ladder.
            // Server is never armed; tick is monotonic so a FullState restore past the tick auto-recovers.
            if (isSdClient
                && xpTURN.Klotho.Diagnostics.FaultInjection.ForceClientDesyncAtTick >= 0
                && xpTURN.Klotho.Diagnostics.FaultInjection.ForceClientDesyncPlayerIds.Contains(LocalPlayerId)
                && _simulation is xpTURN.Klotho.ECS.EcsSimulation desyncSim)
            {
                desyncSim.ForceDesyncHashTick = xpTURN.Klotho.Diagnostics.FaultInjection.ForceClientDesyncAtTick;
                _logger?.KWarning($"[FaultInjection][SD] Client desync armed: playerId={LocalPlayerId}, atTick={desyncSim.ForceDesyncHashTick}");
            }

            // Positive control (any mode): arm a real component-state mutator on the targeted
            // player's sim. Unlike the hash salt above this mutates component state, so the diagnostic
            // localizes the actual diverging component/system. Tick-keyed + re-execution idempotent
            // (applied in EcsSimulation.Tick on every (re-)execution of the tick).
            if (xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionMutator != null
                && xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionTick >= 0
                && xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionPlayerIds.Contains(LocalPlayerId)
                && _simulation is xpTURN.Klotho.ECS.EcsSimulation corruptSim)
            {
                corruptSim.StateCorruptionTick = xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionTick;
                corruptSim.StateCorruptionMutator = xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionMutator;
                _logger?.KWarning($"[FaultInjection] State corruption armed: playerId={LocalPlayerId}, atTick={corruptSim.StateCorruptionTick}");
            }
            // Config armed the tick for this player but the game never registered a mutator: the scenario
            // will run and quietly corrupt nothing, so the operator would read a clean run as a passing
            // one. Warned HERE and not at config load, because the game registers the mutator after the
            // config is read — this is the first point where "still null" actually means missing.
            else if (xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionMutator == null
                && xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionTick >= 0
                && xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionPlayerIds.Contains(LocalPlayerId))
            {
                _logger?.KError(
                    $"[FaultInjection] State corruption INERT: playerId={LocalPlayerId} armed at tick={xpTURN.Klotho.Diagnostics.FaultInjection.StateCorruptionTick} " +
                    $"but no StateCorruptionMutator was registered — the game must supply the mutation (core owns only the tick/player gate). Nothing will be corrupted.");
            }
#endif
            if (!isSdClient)
            {
                // Engine writes deterministic participant slots into the frame
                // before any game-specific world setup runs.
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                {
                    var frame = ecsSim.Frame;
                    for (int i = 0; i < _activePlayerIds.Count; i++)
                    {
                        var slotEntity = frame.CreateEntity();
                        frame.Add(slotEntity, new xpTURN.Klotho.ECS.SessionParticipantComponent
                        {
                            PlayerId = _activePlayerIds[i],
                        });
                    }

                    // Singleton seed entity — zero-extend cast normalizes negative int seeds to a stable ulong.
                    var seedEntity = frame.CreateEntity();
                    frame.Add(seedEntity, new xpTURN.Klotho.ECS.RandomSeedComponent
                    {
                        Seed = (ulong)(uint)_randomSeed,
                    });

                    // Singleton match-end state — created here in the fixed deterministic
                    // entity-creation order (participants → seed → matchEnd) so the state hash matches
                    // across peers. The game's match-end system writes {Ended, Winner, Reason}; the engine
                    // reads it (IsMatchEndedState / GetActiveMatchEnd) at resync/restore boundaries.
                    // SD clients skip this block (!isSdClient) and receive the singleton via FullState.
                    var matchEndEntity = frame.CreateEntity();
                    frame.Add(matchEndEntity, new xpTURN.Klotho.ECS.MatchEndStateComponent());
                }

                _worldInitInProgress = true;
                try { _simulationCallbacks?.OnInitializeWorld(this); }
                finally { _worldInitInProgress = false; }

                // Establish the navmesh invariant BEFORE anything samples a derivative of it, and
                // note the position: after world init, so the driver sees the buildings a map may
                // have pre-placed, and before both the static fingerprint below and the initial
                // FullState broadcast that follows.
                //
                // The driver guarantees installed == Bake(f(frame)) from the start of every EXECUTED
                // tick. World init is the one window that guarantee has never covered: the initial
                // FullState — and the fingerprint it carries — goes out after this point and before
                // tick 0 ever runs. Without this the peer's derivative there is still the LOADED
                // mesh, which is the same walkable region as Bake({}) but not the same triangulation
                // (measured in the field: 0x21D6… vs 0xDF40…), so a client that applies that state
                // and corrects itself gets blamed for a static-environment mismatch for being right.
                //
                // Net cost is zero: this MOVES the match-start rebake that tick 0 already paid for,
                // and tick 0 then sees the tag match and skips.
                //
                // SD clients skip this whole block (!isSdClient above) and do not need it — their
                // window is closed by the same correction on the full-state apply path.
                if (_navRebakeDriver != null && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsInit)
                {
                    var initFrame = ecsInit.Frame;
                    _navRebakeDriver.CorrectNow(ref initFrame);
                }

                // One-time static geometry fingerprint. Gated to non-SD-client peers: the SD client
                // has no static loaded yet here (it arrives via Initial FullState), so logging it now
                // would record a misleading count=0. Server/P2P log a server-vs-client comparable line.
                (_simulation as xpTURN.Klotho.ECS.EcsSimulation)?.LogStaticFingerprint(_logger, "boot");
            }

            // Component-registry fingerprint. Logged on EVERY peer (no SD gate: the registry is
            // frozen before any state arrives) because the registered type SET is state-hash input —
            // Frame.CalculateHash folds every type, including empty ones. Two peers that differ here
            // can never agree on a state hash, so this line is the first thing to compare when a
            // hash mismatch shows up. Editor vs player build is the classic case: Editor-only test
            // assemblies contribute components a player build does not have.
            _logger?.KInformation(
                $"[KlothoEngine][Registry] boot: types={xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutTypeCount} " +
                $"fp=0x{xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutFingerprint:X16}");

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
                _logger?.KWarning($"[KlothoEngine] MarkBootstrapComplete ignored (State={State})");
                return;
            }
            State = KlothoState.Running;
            _logger?.KInformation($"[KlothoEngine] BootstrapPending -> Running");
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
            _logger?.KInformation($"[KlothoEngine][SD] Bootstrap timeout resync: peerId={peerId}, tick={_cachedFullStateTick}, size={_cachedFullState.Length}");
        }

        /// <summary>
        /// Fires once per <see cref="Update"/>, before any per-mode branch — the one place a host
        /// can advance FRAME-paced work and know it runs exactly once per frame in every mode.
        ///
        /// <para><b>Why an event rather than a callback interface.</b> The dedicated server never
        /// receives an <c>IViewCallbacks</c>, and most test harnesses construct the engine through
        /// the three-argument <c>Initialize</c> and so have no <c>ISimulationCallbacks</c> either.
        /// A hook on either of those would silently do nothing in exactly the paths that need
        /// covering. Subscribers reach this through the engine they already hold — the server via
        /// <c>RoomManagerConfig.OnRoomCreated</c>, a client via its session, a test directly.</para>
        ///
        /// <para><b>Contract: do not touch simulation state.</b> A frame is not a tick. The tick
        /// loop may execute the same tick many times (rollback re-execution) and may execute none
        /// at all, so anything written here would not be re-executed with the frame and would
        /// diverge. This is for peer-local derived work — advancing a sliced rebake, warming a
        /// cache — and the sliced rebake is why it exists: its progress cursor is derived state
        /// precisely so that it can be paced by frames instead of ticks.</para>
        ///
        /// <para>Exceptions are caught and logged, not propagated.</para>
        /// </summary>
        public event Action<float> OnFrameBoundary;

        public void Update(float deltaTime)
        {
            // PuP snapshot capture. Run exactly once immediately on Update entry, before any simulation logic.
            // Not called again on rollback/resim paths within Update.
            CapturePreviousUpdatePredicted();

            // Advance the Verified render timeline by wall-clock. Run once before per-mode early returns.
            AdvanceVerifiedRenderTime(deltaTime);

            // Frame-paced host work. Same placement rule as the two calls above — once per Update,
            // ahead of every per-mode early return — because that is what makes it once per FRAME
            // in every mode. A throwing subscriber is contained: this point is on every mode's
            // path, so letting it escape would take the session down with it.
            if (OnFrameBoundary != null)
            {
                try
                {
                    OnFrameBoundary(deltaTime);
                }
                catch (Exception ex)
                {
                    _logger?.KError($"[KlothoEngine] OnFrameBoundary subscriber threw — ignored: {ex.Message}\n{ex.StackTrace}");
                }
            }

            // Frame pacing for a sliced navmesh rebake. Same placement rule as the event above — once
            // per Update, ahead of every per-mode early return — because that is what makes it once
            // per FRAME in every mode, including the server-driven client that never runs world init.
            //
            // Called directly rather than through the event: the driver lives in Deterministic/,
            // which does not know this type, so it cannot subscribe itself, and leaving the
            // subscription to the game is what let a whole host miss it for an entire release.
            //
            // Outside the try above, and that is not an oversight. The driver contains its own slice
            // faults (it drops the task and reports on the next tick), so nothing should escape here
            // — if something does it is a defect in that containment, and swallowing it a second time
            // would hide the one path that is supposed to be exception-free.
            _navRebakeDriver?.AdvanceSlice(deltaTime);

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

            // Expire probe round trips whose answer never arrived. Wall-clock, off the deterministic
            // path — a diagnosis timing out changes nothing a peer simulates.
            SweepDesyncProbes();

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
                        _logger?.KWarning($"[KlothoEngine] ClientTick: Accumulator clamped: {dropped:F1}ms dropped ({dropped / _simConfig.TickIntervalMs:F1} ticks skipped)");
                }
            }
#endif

            // If ahead of remote peers, trim accumulated time to slow progression.
            if (_timeSyncEnabled)
            {
                int waitFrames = _timeSync.RecommendWaitFrames(requireIdleInput: true);
                if (waitFrames > 0 && !_throttleNeedsTick)
                {
                    if (_throttleBudget <= 0)
                        _throttleBudget = waitFrames; // one-shot arm (≤ MAX_FRAME_ADVANTAGE)

                    float waitMs = waitFrames * _simConfig.TickIntervalMs;
                    float before = _accumulator;
                    float remainder = before % _simConfig.TickIntervalMs;
                    _accumulator = Math.Max(_accumulator - waitMs, remainder);
                    bool tickSkipped = _accumulator < _simConfig.TickIntervalMs && before >= _simConfig.TickIntervalMs;

                    _logger?.KDebug($"[KlothoEngine] TimeSync: Waiting {waitFrames} frames (local={_timeSync.LocalAdvantageMean:F1}, remote={_timeSync.RemoteAdvantageMean:F1})");

                    // Budget is in frame (tick-time) units — decrement only when the trim actually
                    // consumed one tick's worth (tickSkipped), NOT per update. Per-update decrement
                    // under-waits by interval/dt at high fps; the harness (dt == interval) masks it.
                    if (tickSkipped)
                    {
                        _throttleBudget--;

                        // E-lite: supplementary sample so the means keep tracking reality while
                        // frozen. Same tickSkipped gate as the budget — keeps the window at one
                        // sample per tick-time in every regime (normal ticks / frozen skips).
                        SampleAdvantageFrame();

                        _logger?.KWarning($"[KlothoEngine] TimeSync: Tick skip at tick {CurrentTick} (accumulator {before:F1}ms → {_accumulator:F1}ms, waitMs={waitMs:F1}, budget={_throttleBudget})");
                    }
                    if (_throttleBudget <= 0)
                        _throttleNeedsTick = true;
                }
                else if (waitFrames == 0)
                {
                    // Mid-wait release (mean collapsed via the E-lite samples or a fresh tick
                    // sample) — discard leftover budget so the next engagement re-arms with its
                    // own recommendation instead of inheriting a stale, shorter one.
                    _throttleBudget = 0;
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
                            _logger?.KWarning($"[KlothoEngine] Tick gap: {gap}ms (expected {_simConfig.TickIntervalMs}ms), tick={CurrentTick}");
                    }
                    _lastTickWallMs = nowMs;
                }
#endif

                // Pause-grace auto-stop — emit StopCommand instead of game OnPollInput.
                // Gate on (latch START) && (current state, RELEASE). The latch term keeps
                // injection verified-timed — IsMatchEndedState is true even on predicted ticks, so a
                // state-only gate would inject StopCommand during prediction and replay it if a rollback
                // un-ends the match. The state term releases the gate when a watermark-below rollback
                // restores a non-ended state (un-freezes), without un-firing the one-way latch.
                bool pauseGraceFired = false;
                if (_matchEndedDispatched
                    && _simulation.IsMatchEndedState
                    && _sessionConfig.EndGracePolicy == EndGracePolicy.Pause)
                {
                    var stop = CommandPool.Get<StopCommand>();
                    InputCommand(stop);
                    pauseGraceFired = true;
                }
                else if (_simulationCallbacks != null)
                {
                    _simulationCallbacks.OnPollInput(LocalPlayerId, CurrentTick, _commandSender);
                }
                else
                {
                    OnPreTick?.Invoke(CurrentTick);
                }

                int inputTick = CurrentTick + _simConfig.InputDelayTicks;

                // Auto-inject EmptyCommand only when (a) no LocalPlayer cmd this tick AND (b) Pause grace not active.
                // Pause grace already emitted StopCommand at inputTick + RecommendedExtraDelay; the auto-inject
                // would land in the same slot and overwrite stop via last-write-wins.
                //
                // Check the ACTUAL landing slot, not the bare inputTick. InputCommand stamps
                // at inputTick + RecommendedExtraDelay and then clamps up to _lastSentCmdTick (monotonic),
                // so this tick's real cmd (submitted by OnPollInput above, which already advanced
                // _lastSentCmdTick) lands at max(inputTick + RecommendedExtraDelay, _lastSentCmdTick).
                // Checking the bare inputTick (pre-extra, pre-clamp) missed it whenever RecommendedExtraDelay
                // > 0 and re-injected an empty onto the real cmd's slot. _lastSentCmdTick is int.MinValue
                // before the first send, so max() degrades to inputTick + RecommendedExtraDelay then.
                // (This subsumes the former pre-clamp-slot DEBUG sentinel: a collision is now impossible here,
                // and the SD send path's DroppedDuplicate suppression is the residual runtime backstop.)
                int autoInjectTick = Math.Max(inputTick + RecommendedExtraDelay, _lastSentCmdTick);
                if (!pauseGraceFired && !_inputBuffer.HasCommandForTick(autoInjectTick, LocalPlayerId))
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
                        _logger?.KWarning($"[KlothoEngine][ChainStall] lag={lag} >= stallWarnThreshold={stallWarnThreshold} (CurrentTick={CurrentTick}, _lastVerifiedTick={_lastVerifiedTick}) — Chain stalled, awaiting quorum / reconnect");
                    }
                }
            }
#endif
        }

        public event Action<int> OnExtraDelayChanged;

        public void ApplyExtraDelay(int delay, ExtraDelaySource source)
        {
            int prevEffective = RecommendedExtraDelay;
            if (source == ExtraDelaySource.DynamicPush)
            {
                // Mid-match server push sets the authoritative baseline. On an UP push (server now covers
                // the client's need), MIGRATE the now-covered reactive into baseline — drain reactive by
                // the increase so effective neither ratchets to the clamp nor overshoots, and authority
                // transfers to the server. On a DOWN push (server RTT-improve), PRESERVE
                // the client's reactive correction (a server estimate drop must not wipe a
                // locally-needed lead). Net: effective on UP = max(newBaseline, prevEffective).
                if (delay >= _baselineExtraDelay)
                    _reactiveExtraDelay = Math.Max(0, prevEffective - delay);
                _baselineExtraDelay = delay;
            }
            else
            {
                // Seed events (Sync/LateJoin/Reconnect) are an authoritative restart: the catchup jump
                // makes residual reactive meaningless, so clear it.
                _baselineExtraDelay = delay;
                _reactiveExtraDelay = 0;
            }

            // DynamicPush fires mid-match (rate-limited 500ms) — Debug to avoid prod noise.
            // Sync/LateJoin/Reconnect are 1-shot accept events — Information for operational trace.
            if (source == ExtraDelaySource.DynamicPush)
                _logger?.KDebug($"[KlothoEngine][{source}] Baseline extra delay applied: {delay} ticks (CurrentTick={CurrentTick}, prevEffective={prevEffective}, reactive={_reactiveExtraDelay})");
            else
                _logger?.KInformation($"[KlothoEngine][{source}] Baseline extra delay applied: {delay} ticks (CurrentTick={CurrentTick}, prevEffective={prevEffective}, reactive reset)");

            FinalizeExtraDelayChange(prevEffective, isReconnect: source == ExtraDelaySource.Reconnect);
        }

        /// <summary>
        /// Floors locally-issued cmd.Tick at the late-joiner's own joinTick so the first gameplay
        /// command (e.g. spawn) never targets a tick before the PlayerJoinCommand that seeds
        /// join-time state — regardless of catchup speed or extra-delay drift. Called on the
        /// late-join guest path only (KlothoNetworkService.SubscribeEngine); send-side only, so no
        /// determinism impact — other peers execute received commands at their carried tick.
        /// </summary>
        public void SetLateJoinCommandFloor(int joinTick)
        {
            _lateJoinCommandFloorTick = joinTick;
            _logger?.KInformation($"[KlothoEngine][LateJoin] Command floor set: joinTick={joinTick} (CurrentTick={CurrentTick})");
        }

        public void EscalateExtraDelay(int step, int max)
        {
            int clampMax = _simConfig.MaxRollbackTicks / 2;
            // Effective backstop: never let baseline+reactive exceed the rollback-budget clamp.
            if (RecommendedExtraDelay >= clampMax) return;
            // Reactive-alone cap = max (ReactiveMax).
            int newReactive = Math.Min(_reactiveExtraDelay + step, max);
            if (newReactive <= _reactiveExtraDelay) return;

            int prevEffective = RecommendedExtraDelay;
            _reactiveExtraDelay = newReactive;
            int newEffective = RecommendedExtraDelay;
            if (newEffective == prevEffective) return; // clamp absorbed the bump — no observable change
            _logger?.KWarning($"[KlothoEngine][DynamicDelay] Reactive escalate: reactive={_reactiveExtraDelay}, effective {prevEffective}->{newEffective}");
            FinalizeExtraDelayChange(prevEffective, isReconnect: false);
        }

        // Client-reactive de-escalation: decays the reactive correction by `step`
        // toward 0 during stable intervals. Baseline (server authority) is untouched. No-op when reactive
        // is already 0 — avoids spurious OnExtraDelayChanged when only server baseline is in effect.
        public void DeEscalateExtraDelay(int step)
        {
            if (_reactiveExtraDelay <= 0) return;
            int prevEffective = RecommendedExtraDelay;
            _reactiveExtraDelay = Math.Max(0, _reactiveExtraDelay - step);
            int newEffective = RecommendedExtraDelay;
            if (newEffective == prevEffective) return; // sum clamped — reactive change not yet observable
            _logger?.KDebug($"[KlothoEngine][DynamicDelay] Reactive de-escalate: reactive={_reactiveExtraDelay}, effective {prevEffective}->{newEffective}");
            FinalizeExtraDelayChange(prevEffective, isReconnect: false);
        }

        // Shared post-mutation bookkeeping: LagReductionLatency tracker (measures clamp resolution after
        // an effective decrease) + OnExtraDelayChanged notification (fires the effective/clamped value).
        private void FinalizeExtraDelayChange(int prevEffective, bool isReconnect)
        {
            int newEffective = RecommendedExtraDelay;
            // Reconnect excluded: catchup advances CurrentTick in a jump, so actualTicks would reflect
            // catchup duration rather than clamp resolution (Reconnect stale-guard).
            if (isReconnect)
            {
                _lagReductionPending = false;
            }
            else if (newEffective < prevEffective)
            {
                _lagReductionPending = true;
                _lagReductionPrevDelay = prevEffective;
                _lagReductionNewDelay = newEffective;
                _lagReductionStartTick = CurrentTick;
            }
            OnExtraDelayChanged?.Invoke(newEffective);
        }

        public IReliableCommandHandle IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null)
            => _reliableTracker.Issue(commandFactory, policy ?? ReliabilityPolicy.Default);

        // Route a reliable command to the authority for execution-tick placement. Returns true if the
        // reliable channel handled it (server-driven client → reliably-ordered submit to the server).
        // Returns false when the channel is unavailable — peer-to-peer (which uses the legacy slot
        // path) or a server-side issue — so ReliableCommandTracker falls back to the legacy slot path.
        internal bool SubmitReliable(ICommand command)
        {
            if (_simConfig.Mode == NetworkMode.ServerDriven
                && _serverDrivenNetwork != null
                && !_serverDrivenNetwork.IsServer)
            {
                _serverDrivenNetwork.SendReliableCommand(command);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Submit a command for the local player.
        ///
        /// <para><b>Ownership contract</b>: the caller transfers sole ownership of <paramref name="command"/>
        /// to the engine on entry. The caller MUST NOT retain or reuse the instance after this call returns —
        /// the engine may store it in InputBuffer (SD), serialize it through the transport (P2P), and
        /// eventually return it to CommandPool via cleanup. Violating this contract risks pool poisoning.</para>
        /// </summary>
        public void InputCommand(ICommand command, int extraDelay = 0)
        {
            // Target tick reflecting input delay. extraDelay adds per-command lead margin
            // (used by recovery paths — e.g. spawn cmd PastTick reject escalation).
            // RecommendedExtraDelay compensates LateJoin/Reconnect catchup gap and mid-match RTT shifts.
            int targetTick = CurrentTick + _simConfig.InputDelayTicks + extraDelay + RecommendedExtraDelay;

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
                    _logger?.KWarning($"[FaultInjection][SD] ForceTickOffset: cmd.Tick {targetTick} → {shifted} (delta={tickDelta}, type={cmdBase.GetType().Name})");
                    targetTick = shifted;
                }
#endif

                // Late-join spawn floor — lift targetTick to the joiner's joinTick so no local command
                // precedes the PlayerJoinCommand that seeds join-time state. The catchup prefill
                // (HandleCatchupComplete) covers the widened local-chain gap [currentTick, joinTick),
                // and reals piling onto the floor tick reuse the monotonic-clamp semantics below
                // ("same-tick multiple cmds are legal"). Inert (0) except on a late-join guest.
                if (targetTick < _lateJoinCommandFloorTick)
                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[KlothoEngine][LateJoin] cmd.Tick joinTick floor: computed={targetTick}, floored={_lateJoinCommandFloorTick}");
#endif
                    targetTick = _lateJoinCommandFloorTick;
                }

                // Prevent non-monotonic cmd.Tick when RecommendedExtraDelay decreases on Reconnect / mid-match.
                // Applied after fault injection to keep production-ordering invariant in fault tests.
                // Strict-less-than: same-tick multiple cmds are legal in lockstep, so equal targetTick passes through.
                bool clampEngaged = targetTick < _lastSentCmdTick;
                if (clampEngaged)
                {
                    int clamped = _lastSentCmdTick;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[KlothoEngine] cmd.Tick monotonic clamp: computed={targetTick}, clamped={clamped}");
#endif
                    targetTick = clamped;
                }

                // Forward gap fill — when RecommendedExtraDelay increases mid-match, cmd.Tick jumps
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
                        _logger?.KInformation($"[KlothoEngine][GapFill] Forward gap filled: [{fillStart}, {fillEnd}], count={fillEnd - fillStart + 1}");
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
                    _logger?.KInformation(
                        $"[Metrics][LagReductionLatency] {{\"prevDelay\":{_lagReductionPrevDelay},\"newDelay\":{_lagReductionNewDelay},\"expectedTicks\":{expectedTicks},\"actualTicks\":{actualTicks}}}");
                    _lagReductionPending = false;
                }

                cmdBase.Tick = targetTick;
            }

            // ServerDriven mode places it in the local buffer and then sends to the server.
            if (_simConfig.Mode == NetworkMode.ServerDriven)
            {
                // Send to the server ONLY the command the local buffer actually kept
                // (Stored). A duplicate (tick, playerId) collision — an auto-inject empty over a real
                // cmd, or a delay-decrease clamp piling reals onto one tick — is
                // DroppedDuplicate (keep-first); sending it anyway lets the server's last-write-wins
                // keep a different cmd than the local buffer kept, desyncing (button eaten). Suppressing
                // the send makes local and server both keep the first cmd, independent of arrival order.
                // System commands route through AddSystemCommand and always return Stored, so they are
                // never suppressed (e.g. PlayerJoin). Dropped* arrivals are sole-owned here and the buffer
                // never returns them, so return them to the pool; AlreadyStored/Replaced are
                // buffer-owned and must NOT be returned.
                var storeResult = _inputBuffer.AddCommandChecked(command);
                if (storeResult == CommandStoreResult.Stored)
                    _serverDrivenNetwork.SendClientInput(targetTick, command);
                else if (storeResult == CommandStoreResult.DroppedDuplicate
                         || storeResult == CommandStoreResult.DroppedSealed)
                {
#if DEBUG || DEVELOPMENT_BUILD
                    // Smoke signal: confirms a colliding send was suppressed (empty / real)
                    // so local↔server stay keep-first. Pre-fix this cmd would have been sent and the
                    // server's last-write-wins would diverge.
                    _logger?.KDebug($"[KlothoEngine][SendGate] Suppressed duplicate send: tick={targetTick}, playerId={LocalPlayerId}, type={command.GetType().Name}, result={storeResult}, extraDelay={RecommendedExtraDelay}");
#endif
                    CommandPool.Return(command);
                }
                return;
            }

            // P2P broadcasts over the network.
            _networkService.SendCommand(command);
        }

        public void AbortMatch(AbortReason reason)
        {
            if (State.IsEnded()) return;
            _logger?.KWarning($"[KlothoEngine][AbortMatch] reason={reason}, State {State}->Aborted");
            State = KlothoState.Aborted;
            OnMatchAborted?.Invoke(reason);
        }

        /// <summary>
        /// Transitions Running -> Ending. Idempotent: no-op when called from Ending / Finished /
        /// Aborted / Idle / WaitingForPlayers / BootstrapPending / Paused. While in Ending, the
        /// State == Running gate in Update() blocks ExecuteTick, so the simulation tick freezes.
        /// _networkService.Update() (run before the gate) keeps transport-level keepalives flowing.
        /// Currently not invoked in the normal end flow — EndGracePolicy.Pause halts characters via
        /// client-issued StopCommand on the deterministic stream instead. Retained for API stability
        /// and potential future use (e.g., admin-triggered freeze).
        /// </summary>
        public void EnterEnding()
        {
            if (State != KlothoState.Running)
            {
                _logger?.KDebug($"[KlothoEngine][EnterEnding] ignored, State={State} (only Running -> Ending allowed)");
                return;
            }
            _logger?.KInformation($"[KlothoEngine][EnterEnding] State Running->Ending at tick={CurrentTick}");
            State = KlothoState.Ending;
        }

        /// <summary>
        /// Single source for the replay TotalTicks clamp: the last tick actually RECORDED (the
        /// recorder's CurrentTick). Trailing unverified (predicted) ticks past it are not authoritatively
        /// reproducible, so they must not extend the replay. This is the recorded tick in BOTH modes —
        /// unlike _lastVerifiedTick, which on the SD client is entry.Tick (= executionTick + 1, one past
        /// the recorded executionTick) and would extend the replay by one never-recorded tail tick. The
        /// corrective-reset truncation uses the SAME value (captured BEFORE ApplyFullState overwrites _lastVerifiedTick).
        /// </summary>
        private int ComputeReplayTotalTicks() => ((IReplayRecorder)_replaySystem).CurrentTick;

        public void Stop()
        {
            if (_replaySystem.IsRecording)
            {
                // Clamp to the verified line (was CurrentTick + InputDelayTicks — an
                // over-count that replayed never-reproducible predicted/empty tail ticks). < 0 (nothing
                // verified) → empty replay, avoiding a negative TotalTicks/DurationMs.
                int totalTicks = ComputeReplayTotalTicks();
                _replaySystem.StopRecording(totalTicks < 0 ? 0 : totalTicks);
            }

            _dynamicInputDelayPolicy?.Detach();
            _reliableTracker?.Detach();

            State = KlothoState.Finished;

            // Dump the component-memory peak report once while the sim is still alive (Stop never nulls
            // _simulation), then dispose the sampler (unsubscribe). Peak column = accumulated PeakCounts;
            // reserved/live = final-frame snapshot. ringHeaps = RollbackCapacity + 1 (rollback ring + live
            // frame). Guarded by _memSampler != null so a double Stop is idempotent (no re-dump).
            if (_memSampler != null)
            {
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsMem)
                {
                    var report = xpTURN.Klotho.ECS.Diagnostics.ComponentMemoryAnalyzer.Capture(
                        ecsMem.Frame, ecsMem.RollbackCapacity + 1, _memSampler.PeakCounts);
                    report.ObservedTicks = _memSampler.ObservedTicks;
                    _logger?.KLog(KLogLevel.Information, $"[Mem] component-memory peak report:\n{report.ToText()}");
                }
                _memSampler.Dispose();
                _memSampler = null;
            }

            // Sibling to the memreport dump: per-system perf profile (time + per-tick alloc, incl. resim).
            // Guarded by _sysPerfDumped (the config gate stays true after Stop) so a double Stop is
            // idempotent. Monitor is owned by SystemRunner — nothing to dispose here.
            if (!_sysPerfDumped && _simConfig.SystemPerfMonitoring
                && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsPerf)
            {
                _sysPerfDumped = true;
                var log = ecsPerf.AppendSystemPerfLog();
                if (log != null)
                    _logger?.KLog(KLogLevel.Information, $"[SysPerf] per-system profile (incl. resim ticks):\n{log}");
            }

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
                            _networkService.OnReconnecting -= HandleSdReconnecting;
                            _networkService.OnReconnectFailed -= HandleSdReconnectFailed;
                        }
                    }
                }
                else
                {
                    _networkService.OnCommandReceived -= HandleCommandReceived;
                    _networkService.OnDesyncDetected -= HandleNetworkDesync;
                    _networkService.OnSyncHashCompared -= HandleSyncHashCompared;
                    _networkService.OnGameStart -= HandleGameStart;
                    _networkService.OnFrameAdvantageReceived -= HandleFrameAdvantage;
                    _networkService.OnFullStateRequested -= HandleFullStateRequested;
                    _networkService.OnFullStateReceived -= HandleFullStateReceived;
                    _networkService.OnLateJoinPlayerAdded -= HandleLateJoinPlayerAdded;
                    _networkService.OnResyncFailureReported -= HandleResyncFailureReported;
                    _networkService.OnMatchAbortReceived -= HandleMatchAbortReceived;
                    OnHashMismatch -= HandleHashMismatchForCorrectiveReset;
                }
            }

            // Mirror the non-network subscriptions Initialize makes: without these,
            // a Stop -> Initialize cycle re-subscribes the same simulation/replay instances and
            // the handlers fire twice (e.g. HandleHashMismatchForCorrectiveReset would consume
            // the corrective-reset budget twice per mismatch).
            UnsubscribeDesyncProbe();

            if (_simulation != null)
                _simulation.OnPlayerJoinedNotification -= HandlePlayerJoinedNotification;
            if (_replaySystem != null)
                _replaySystem.OnInitialStateSnapshotSet -= HandleInitialStateSnapshotSet;

            _initialized = false;
        }

        private bool CanAdvanceTick()
        {
            if (_inputBuffer.HasAllCommands(CurrentTick, _activePlayerIds))
                return true;

            if (_disconnectedPlayerIds.Count > 0)
                OnDisconnectedInputNeeded?.Invoke(CurrentTick);

            return _inputBuffer.HasAllCommands(CurrentTick, _activePlayerIds);
        }

        private void ExecuteTick()
        {
            // To avoid GC, fetch the command list directly from the buffer each tick.
            var commands = _inputBuffer.GetCommandList(CurrentTick);

            if (commands.Count > 0)
            {
                for (int di = 0; di < commands.Count; di++)
                {
                    //_logger?.KInformation($"[KlothoEngine] ExecuteTick: tick={CurrentTick}, cmd[{di}] typeId={commands[di].CommandTypeId} player={commands[di].PlayerId}");
                }
            }

            // Replay recording moved from here (execution time) to the verification-promotion
            // points (the inline branch below + TryAdvanceVerifiedChain) via RecordVerifiedTick.
            // Recording at execution missed predicted ticks and left
            // stale empty-fills uncorrected after late real commands rolled the state back.

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
                    _logger?.KError($"[KlothoEngine] SyncTest: Desync detected at tick {syncResult.Tick}! Expected: 0x{syncResult.ExpectedHash:X16}, Got: 0x{syncResult.ActualHash:X16}");
                }
            }
            else
#endif
            {
                _eventCollector.BeginTick(CurrentTick);
                _simulation.Tick(commands);
            }

            // Store collected events into the tick buffer.
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            WarnIfReclaimingPendingSynced(CurrentTick);
#endif
            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            // Periodic state-hash synchronization check. Anchor promotion is event-based:
            // HandleSyncHashCompared advances _lastMatchedSyncTick on matched comparisons.
            // Send immediately only when this tick is verified at execution (chain-continuous,
            // no pending rollback) — otherwise the hash may be computed on top of a mispredicted
            // earlier tick; stash and defer to the verified promotion, same as the
            // speculative-execution path.
            if (_networkService != null && CurrentTick % _simConfig.GetEffectiveSyncCheckInterval() == 0)
            {
                long hash = _simulation.GetStateHash();
                // Must stay adjacent to the GetStateHash above: Record copies the breakdown that
                // call filled, and SendSyncHash below can synchronously drive the whole desync-detection
                // chain (compare → HandleNetworkDesync), which must not slip between compute and record.
                _diagHistorySim?.RecordHashHistory(CurrentTick);
                long cmdHash = ComputeCommandDigest(commands);   // Digest of the executed set
                _localHashes[CurrentTick] = (hash, cmdHash);
                if (CurrentTick == _lastVerifiedTick + 1 && !_hasPendingRollback)
                    _networkService.SendSyncHash(CurrentTick, hash, cmdHash);
                else
                    _deferredHashSendTicks.Add(CurrentTick);
            }
            else if (_networkService != null)
            {
                // P2P per-tick history (config-gated). Off ⇒ no-op ⇒ L1 stays check-tick granularity.
                // Networked engines only: a replay engine runs ExecuteTick with a null service and must
                // not pay a per-tick hash for a diagnostic ring nothing reads.
                _diagHistorySim?.ComputeAndRecordHashHistory(CurrentTick);
            }

            // TimeSync: update advantage history, idle input, and local tick.
            // SenderTick is the REMOTE peers' timesync input — it must stay truthful regardless
            // of whether OUR timesync is enabled (late-join/reconnect guests never pass
            // HandleGameStart, so _timeSyncEnabled stays false on a fresh engine and the
            // piggyback would freeze at the restore tick).
            // `?.` is REQUIRED here (not defensive): replay engines run ExecuteTick with a
            // null service (networkless Initialize) — see the null-guarded
            // SendSyncHash above. The old in-guard dereference was only shielded by
            // _timeSyncEnabled staying false on replay engines.
            _networkService?.SetLocalTick(CurrentTick);
            // Push the measured advantage OUTSIDE the timesync guard (parallel to
            // SetLocalTick) so a throttle-disabled guest still reports a truthful SenderAdvantage
            // to the host. CalculateLocalAdvantage reads _remoteTicks, which is populated regardless
            // of _timeSyncEnabled. Only the wire value rounds; the window mean keeps the fraction.
            _networkService?.SetLocalAdvantage((int)System.Math.Round(CalculateLocalAdvantage()));

            if (_timeSyncEnabled)
            {
                // Feed the window with local advantage + the true (exchanged) remote advantage,
                // mirror fallback until a remote sample arrives.
                SampleAdvantageFrame();

                // Normal sampling has resumed on this tick — the throttle may re-engage.
                _throttleNeedsTick = false;
                _throttleBudget = 0;

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
                RecordVerifiedTick(CurrentTick);   // Record at promotion, before event dispatch
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
                    var predicted = PredictInputOrEmpty(playerId, CurrentTick, -1);
                    _tickCommandsCache.Add(predicted);
                    _pendingCommands.Add(predicted);
                }
            }

            _eventCollector.BeginTick(CurrentTick);
            _tickCommandsCache.Sort(s_commandComparer);
            _simulation.Tick(_tickCommandsCache);

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            WarnIfReclaimingPendingSynced(CurrentTick);
#endif
            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            // Deferred sync-hash: stash the hash for check ticks executed speculatively (same
            // P2P gate as the ExecuteTick send). Sent when the verified chain crosses this tick;
            // skipping entirely would leave no comparison and stall anchor promotion.
            if (_networkService != null && CurrentTick % _simConfig.GetEffectiveSyncCheckInterval() == 0)
            {
                long hash = _simulation.GetStateHash();
                _diagHistorySim?.RecordHashHistory(CurrentTick);   // Adjacent to the compute (contract)
                _localHashes[CurrentTick] = (hash, ComputeCommandDigest(_tickCommandsCache));   // Command digest
                _deferredHashSendTicks.Add(CurrentTick);
            }
            else if (_networkService != null)
            {
                // P2P per-tick history; networked engines only (replay pays no diagnostic hash).
                _diagHistorySim?.ComputeAndRecordHashHistory(CurrentTick);
            }

            // TimeSync must keep advancing even during prediction. If it halts, SenderTick stops
            // and remote peers' TimeSync forcibly slows down.
            // SenderTick must stay truthful even when timesync is disabled (see the
            // hoisted twin in ExecuteTick). `?.` is REQUIRED: replay engines run with a null service.
            _networkService?.SetLocalTick(CurrentTick);
            // Truthful advantage push outside the guard (twin of the ExecuteTick hoist).
            _networkService?.SetLocalAdvantage((int)System.Math.Round(CalculateLocalAdvantage()));

            if (_timeSyncEnabled)
            {
                // True (exchanged) remote advantage with mirror fallback.
                SampleAdvantageFrame();

                // Normal sampling has resumed on this tick — the throttle may re-engage.
                _throttleNeedsTick = false;
                _throttleBudget = 0;

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

        // Prediction for a missing player's input. A confirmed-disconnected peer
        // (_disconnectedPlayerIds — populated on guests via propagation) has its slot
        // filled by the host with empty (and sealed); predict empty to match so the host's empty
        // fill does not mismatch a repeat-last prediction and force a rollback every tick for the
        // whole disconnect window, which also pollutes the reactive DynamicInputDelay
        // escalation. Speculative only: the prediction goes into _pendingCommands and the
        // verified chain uses the host's authoritative fill — the choice (empty vs repeat-last)
        // cannot change the verified result/StateHash, only whether a rollback is requested.
        // empty must be a fresh pooled EmptyCommand (NOT the CreateEmptyCommand reuse-template):
        // it is stored per (playerId, tick) in _pendingCommands until reconciled.
        private ICommand PredictInputOrEmpty(int playerId, int tick, int fromTick)
        {
            if (_disconnectedPlayerIds.Contains(playerId))
            {
                var empty = CommandPool.Get<EmptyCommand>();
                empty.PlayerId = playerId;
                empty.Tick = tick;
                return empty;
            }

            GetPreviousCommands(playerId, PREDICTION_HISTORY_COUNT, fromTick);
            return _inputPredictor.PredictInput(playerId, tick, _previousCommandsCache);
        }

        /// <summary>
        /// Fills a cached list with previous commands to avoid GC.
        /// </summary>
        private void GetPreviousCommands(int playerId, int count, int fromTick = -1)
        {
            _previousCommandsCache.Clear();
            int startTick = fromTick >= 0 ? fromTick - 1 : CurrentTick - 1;
            // Scan floor: the buffer holds nothing below OldestTick (empty-buffer sentinel 0 is a
            // safe floor), and prediction relevance ends within the rollback window — bounds the
            // former unbounded back-scan to tick 0 for sparse players.
            int floorTick = Math.Max(_inputBuffer.OldestTick, startTick - _simConfig.MaxRollbackTicks);
            if (floorTick < 0) floorTick = 0;
            for (int t = startTick; t >= floorTick && _previousCommandsCache.Count < count; t--)
            {
                var cmd = _inputBuffer.GetCommand(t, playerId);
                if (cmd != null)
                    _previousCommandsCache.Add(cmd);
            }
        }

        private void HandleCommandReceived(ICommand command)
        {
            // Checked add: the arrival is deserialize-born and unused by the network
            // service after dispatch, so this handler is its sole owner. A sealed drop returns
            // it to the pool at the end of ReconcileConfirmedAgainstPrediction (relocation of the
            // Return removed from the buffer's seal guard — placing it after the last field read
            // there also closes the old pattern of reading a pool-returned instance). Duplicate
            // drops stay GC-delegated.
            var storeResult = _inputBuffer.AddCommandChecked(command);
            ReconcileConfirmedAgainstPrediction(command, storeResult, resyncOnDeepRollback: false);
        }

        // Shared reconcile of a confirmed command (network arrival via HandleCommandReceived,
        // OR catchup confirmed input via ReceiveConfirmedCommand) against the local speculative
        // prediction. Extracted so the catchup path reconciles identically and a late mispredicted
        // remote input rolls back + re-simulates instead of being promoted frozen. resyncOnDeepRollback:
        // catchup callers pass true so a rollback target below the snapshot-ring window (CurrentTick -
        // MaxRollbackTicks) falls back to FullStateResync instead of a partial clamp re-sim that cannot
        // correct the mispredicted gap tick; the normal arrival path passes false to keep the
        // existing clamp-and-continue behavior. storeResult is by-value — the overwrite branch's
        // reassignment and the trailing DroppedSealed Return both read this local copy.
        private void ReconcileConfirmedAgainstPrediction(ICommand command, CommandStoreResult storeResult, bool resyncOnDeepRollback)
        {
            // A reconnecting player's real input must overwrite an UNSEALED empty
            // placeholder on EVERY P2P peer — the host's proactive proxy-fill (InjectDisconnected-
            // PlayerInputs) or a guest's gap-fill / relayed empty. keep-first drops the real as
            // DroppedDuplicate because the empty was seated first, leaving that peer's buffer on
            // empty while the reconnected player simulated real -> per-peer desync. real != empty,
            // so keep-first is wrong on host AND guest alike; the empty is a provisional placeholder
            // and the reconnect-real reaches every peer via relay, so all converge to the real
            // (the former host-only handling was an asymmetry that left guests on empty). This handler is
            // subscribed only on the non-ServerDriven (P2P) branch, so no mode guard is needed.
            // Unsealed is required: a sealed slot is a committed range fill and stays DroppedSealed
            // (the seal guard precedes the overwrite branch). RequestRollback is explicit because
            // the proactive empty was a confirmed proxy, not a prediction, so the prediction-mismatch
            // path below won't fire (predicted == null); it self-no-ops when tick >= CurrentTick
            // (frontier fills are picked up naturally when the sim reaches the tick).
            if (storeResult == CommandStoreResult.DroppedDuplicate
                && !(command is EmptyCommand)
                && !_inputBuffer.IsSealed(command.Tick, command.PlayerId)
                && _inputBuffer.GetCommand(command.Tick, command.PlayerId) is EmptyCommand)
            {
                storeResult = _inputBuffer.AddCommandChecked(command, overwriteExisting: true);
                RequestRollbackOrResync(command.Tick, resyncOnDeepRollback);
            }

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
                _pendingCommands.Remove(predicted);

                // Single byte-equality comparison shared by the rollback decision and accuracy
                // accounting: accuracy = 1 - (fraction of arrived predictions that
                // forced a rollback). The old type-only accuracy judgment overcounted.
                bool match = CommandDataEquals(predicted, command);
                _inputPredictor.UpdateAccuracy(match);

                // Only request rollback when the prediction differs from the actual input.
                if (!match)
                {
                    _logger?.KWarning($"[KlothoEngine] PredictionMismatch: Prediction mismatch tick={command.Tick}, player={command.PlayerId}, predicted={predicted.CommandTypeId}, actual={command.CommandTypeId}");
                    RequestRollbackOrResync(command.Tick, resyncOnDeepRollback);
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

            if (storeResult == CommandStoreResult.DroppedSealed)
                CommandPool.Return(command);
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
            _logger?.KInformation($"[KlothoEngine] HandleGameStart: Game start: playerCount={_activePlayerIds.Count}");

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
            // OnGameStart subscribers (custom snapshot path, other handlers) still fire here.
            OnGameStart?.Invoke();

            // Replay InitialStateSnapshot auto-inject — non-replay + recording + (P2P / SD-Server) only.
            // SD-Client receives the snapshot via server FullState broadcast (ServerDrivenClient.cs HandleInitialFullStateReceived).
            if (!_isReplayMode
                && _replaySystem?.IsRecording == true
                && !(_simConfig.Mode == NetworkMode.ServerDriven && !_serverDrivenNetwork.IsServer))
            {
                var (data, hash) = _simulation.SerializeFullStateWithHash();
                _replaySystem.SetInitialStateSnapshot(data, hash);
                _logger?.KInformation(
                    $"[KlothoEngine][Replay] InitialStateSnapshot injected: size={data.Length}, hash=0x{hash:X16}");
            }

            // SD Server or P2P Host broadcasts the authoritative tick-0 state to all remote peers as a bootstrap.
            // Pre-connect spectators receive the broadcast directly; late-joining spectators use the cached
            // copy via HandleFullStateRequested → SendFullStateResponse.
            bool isSdServer = _simConfig.Mode == NetworkMode.ServerDriven && _serverDrivenNetwork != null && _serverDrivenNetwork.IsServer;
            bool isP2PHost = _simConfig.Mode == NetworkMode.P2P && _networkService != null && _networkService.IsHost;
            if (isSdServer || isP2PHost)
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
                if (isSdServer)
                    _serverDrivenNetwork.BroadcastFullState(0, _cachedFullState, _cachedFullStateHash);
                else
                    _networkService.BroadcastFullState(0, _cachedFullState, _cachedFullStateHash);
                _logger?.KInformation($"[KlothoEngine][Bootstrap] Initial FullState broadcast: mode={(isSdServer ? "SD" : "P2P")}, size={_cachedFullState.Length}, hash=0x{_cachedFullStateHash:X16}");

                // Diagnostic — per-component hash breakdown for desync root-cause analysis.
                // Debug level: ServerInit fires once per match, kept off the release log stream.
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                    ecsSimDiag.LogComponentHashes(_logger, "ServerInit", logLevel: KLogLevel.Debug);
            }
        }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Dev guard: slot reuse at execution destroys the previous occupant's
        // events. If that occupant is still beyond chain advance (lag > ring capacity), its
        // pending Synced events are permanently lost — surface it instead of failing silently
        // (the "host self-wipe during P2P quorum stall" scenario).
        private void WarnIfReclaimingPendingSynced(int tick)
        {
            if (_networkService == null) return;
            int occupant = _eventBuffer.GetSlotOccupantTick(tick);
            if (occupant < 0 || occupant >= tick || occupant <= _lastVerifiedTick) return;

            var evts = _eventBuffer.GetEvents(tick);
            for (int i = 0; i < evts.Count; i++)
            {
                if (evts[i].Mode == EventMode.Synced)
                {
                    _logger?.KWarning($"[KlothoEngine][EventBuffer] Pending Synced event WIPED by slot reuse: occupantTick={occupant}, reusedFor={tick}, _lastVerifiedTick={_lastVerifiedTick}, lag={CurrentTick - _lastVerifiedTick}");
                    OnPendingWipe?.Invoke(occupant, -1, WipeKind.SyncedEvent);
                    break;
                }
            }
        }
#endif

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
                _inputBuffer.LogPendingWipe(cleanupTick, _lastVerifiedTick, CurrentTick,
                    (t, pid) => OnPendingWipe?.Invoke(t, pid, WipeKind.Input));
#endif

                _inputBuffer.ClearBefore(cleanupTick);
                _networkService?.ClearOldData(cleanupTick);

                // No event-buffer trim here: slots self-clean at execution time via
                // ClearTick(CurrentTick) on wrap reuse. A nominal-tick range clear on the ring
                // wiped LIVE events 8~18 ticks old (capacity MaxRollbackTicks+2 < trim distance
                // MaxRollbackTicks+10..20 always wraps), corrupting rollback event diffs and
                // destroying pending Synced events. Pending-Synced loss on slot reuse is now
                // surfaced by WarnIfReclaimingPendingSynced at the reuse point.

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
