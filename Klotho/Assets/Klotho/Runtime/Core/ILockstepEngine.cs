using System;
using Microsoft.Extensions.Logging;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Source path that invoked ApplyExtraDelay. Used to distinguish the entry context in logs and
    /// to let consumers branch on the path (e.g., metrics tagging).
    /// </summary>
    public enum ExtraDelaySource
    {
        /// <summary>Normal-join handshake completion (SyncCompleteMessage).</summary>
        Sync,
        /// <summary>Late-join handshake completion (LateJoinAcceptMessage).</summary>
        LateJoin,
        /// <summary>Reconnect handshake completion (ReconnectAcceptMessage).</summary>
        Reconnect,
        /// <summary>Mid-match server-push update (RecommendedExtraDelayUpdateMessage).</summary>
        DynamicPush,
    }

    /// <summary>
    /// Klotho engine state.
    /// </summary>
    public enum KlothoState
    {
        /// <summary>
        /// Initial state after construction. Initialize has not yet run.
        /// </summary>
        Idle,
        /// <summary>
        /// Initialize completed; waiting for the network layer to issue OnGameStart (Start to be called).
        /// </summary>
        WaitingForPlayers,
        /// <summary>
        /// SD-server only: awaiting all players' bootstrap-ready acks (or timeout) before first tick.
        /// Blocks UpdateServerTick via the existing State == Running gate.
        /// </summary>
        BootstrapPending,
        /// <summary>
        /// Active simulation. Update advances ticks each frame and dispatches events.
        /// </summary>
        Running,
        /// <summary>
        /// Lockstep client (prediction OFF) only: a player's input is missing for the current tick — halt and wait.
        /// Returns to Running automatically once CanAdvanceTick() succeeds. Replay seek also uses this transiently.
        /// </summary>
        Paused,
        /// <summary>
        /// Stop() invoked — engine has been torn down. Replay playback also lands here when finished.
        /// </summary>
        Finished
    }

    /// <summary>
    /// Frame verification state.
    /// </summary>
    public enum FrameState
    {
        /// <summary>A tick that ran with one or more predicted inputs.</summary>
        Predicted,
        /// <summary>A tick where all player inputs are confirmed and every prior tick has been verified.</summary>
        Verified
    }

    /// <summary>
    /// Simulation stage. Distinguishes Forward/Resimulate so that some logic can be bypassed during the resimulation window.
    /// </summary>
    public enum SimulationStage
    {
        /// <summary>Forward simulation (including prediction). Default.</summary>
        Forward,
        /// <summary>The verified/predicted resimulation window immediately after a rollback.</summary>
        Resimulate
    }

    /// <summary>
    /// Klotho main engine interface. Manages network synchronization and simulation execution.
    /// </summary>
    public interface IKlothoEngine
    {
        /// <summary>
        /// Simulation engine parameters. Immutable for the duration of the session.
        /// </summary>
        ISimulationConfig SimulationConfig { get; }

        /// <summary>
        /// Per-session mutable configuration.
        /// </summary>
        ISessionConfig SessionConfig { get; }

        KlothoState State { get; }

        /// <summary>
        /// The simulation instance managed by this engine.
        /// </summary>
        ISimulation Simulation { get; }

        /// <summary>
        /// Logger.
        /// </summary>
        ILogger Logger { get; }

        /// <summary>
        /// Current tick number.
        /// </summary>
        int CurrentTick { get; }

        /// <summary>
        /// Local player ID.
        /// </summary>
        int LocalPlayerId { get; }

        /// <summary>
        /// Time per tick (milliseconds).
        /// </summary>
        int TickInterval { get; }

        /// <summary>
        /// Input delay tick count. Local input is scheduled this many ticks ahead to compensate for network latency.
        /// </summary>
        int InputDelay { get; }

        /// <summary>
        /// Server-recommended extra InputDelay ticks currently applied (LateJoin/Reconnect catchup-gap compensation
        /// plus mid-match dynamic adjustments). Read by transport layer to size LateJoin prefill range so the guest's
        /// own input chain has empty cmds covering [currentTick, currentTick + InputDelay + RecommendedExtraDelay).
        /// </summary>
        int RecommendedExtraDelay { get; }

        /// <summary>
        /// Random seed used for the session. A network-agreed value used by OnInitializeWorld for deterministic initialization.
        /// </summary>
        int RandomSeed { get; }

        /// <summary>
        /// Whether the engine is currently in replay playback mode.
        /// View callbacks and game code use this to guard live-only behavior (e.g. command sending).
        /// </summary>
        bool IsReplayMode { get; }

        /// <summary>
        /// Whether the engine is currently acting as the server in ServerDriven mode.
        /// True only on the SD server; P2P peers, SD Client, Replay, and Spectator are all false.
        /// </summary>
        bool IsServer { get; }

        /// <summary>
        /// Whether the engine is currently acting as the host in P2P mode.
        /// True only on the P2P host; P2P guests, SD server/client, Replay, and Spectator are all false.
        /// Orthogonal to IsServer (SD-specific): a P2P host has IsServer=false.
        /// </summary>
        bool IsHost { get; }

        /// <summary>
        /// Current simulation stage.
        /// Forward = forward tick, Resimulate = the resimulation window immediately after a rollback.
        /// Set to Resimulate upon entering the resimulation window and reverts to Forward immediately after returning.
        /// </summary>
        SimulationStage Stage { get; }

        /// <summary>Convenience property for forward simulation. `Stage == Forward`.</summary>
        bool IsForward => Stage == SimulationStage.Forward;

        /// <summary>Convenience property for resimulation. `Stage == Resimulate`.</summary>
        bool IsResimulation => Stage == SimulationStage.Resimulate;

        /// <summary>
        /// Initialize the engine.
        /// </summary>
        void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger);

        /// <summary>
        /// Initialize the engine with callbacks.
        /// simulationCallbacks are deterministic callbacks invoked on all peers,
        /// viewCallbacks are client-only view callbacks (null is allowed).
        /// </summary>
        void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger,
            ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null);


        /// <summary>
        /// Start the game.
        /// </summary>
        void Start();

        /// <summary>
        /// Per-frame update. Advances ticks, prediction, and resimulation based on accumulated time.
        /// </summary>
        void Update(float deltaTime);

        /// <summary>
        /// Adds a local command to the input buffer and sends it over the network.
        /// extraDelay shifts cmd.Tick further into the future on top of InputDelayTicks.
        /// Used by recovery paths (e.g. LateJoin spawn cmd PastTick reject loop) to add lead margin
        /// for specific commands without changing the global InputDelayTicks.
        /// </summary>
        void InputCommand(ICommand command, int extraDelay = 0);

        /// <summary>
        /// Applies the server-recommended extra InputDelay (absolute value) used to compensate the
        /// LateJoin/Reconnect catchup gap and mid-match RTT shifts. Called from accept-message handlers
        /// and from periodic server push updates. `source` selects the log tag and metric path.
        /// </summary>
        void ApplyExtraDelay(int delay, ExtraDelaySource source);

        /// <summary>
        /// Client-reactive escalation: bumps the recommended extra InputDelay by `step` (capped at `max`)
        /// only when the new value is strictly greater than the current value. Used by client-reactive
        /// fallback when server push is delayed/missed and the client observes repeated PastTick rejects.
        /// </summary>
        void EscalateExtraDelay(int step, int max);

        /// <summary>
        /// Fired after ApplyExtraDelay/EscalateExtraDelay updates the recommended extra delay (newDelay).
        /// Used by the client-reactive fallback to track the last server-push tick (engine-tick grace window).
        /// </summary>
        event Action<int> OnExtraDelayChanged;

        /// <summary>
        /// Fired when the verified-chain advance stalls because at least one player's command is missing
        /// for the next tick. Production-active (fires regardless of build flags); the dev-only log path
        /// is separate and throttled. Used by game-side reactive fallback to count chain-break bursts.
        /// </summary>
        event Action OnChainAdvanceBreak;

        /// <summary>
        /// Stops the engine and unsubscribes from events.
        /// </summary>
        void Stop();

        /// <summary>
        /// Tick execution complete event (executedTick).
        /// </summary>
        event Action<int> OnTickExecuted;

        /// <summary>
        /// Desync detected event (localHash, remoteHash).
        /// </summary>
        event Action<long, long> OnDesyncDetected;

        /// <summary>
        /// Rollback executed event (fromTick, toTick).
        /// </summary>
        event Action<int, int> OnRollbackExecuted;

        /// <summary>
        /// Rollback failed event (requestedTick, reason).
        /// </summary>
        event Action<int, string> OnRollbackFailed;

        /// <summary>
        /// The last contiguous verified tick. -1 if nothing has been verified.
        /// </summary>
        int LastVerifiedTick { get; }

        /// <summary>
        /// Checks whether the specified tick is in the verified state.
        /// </summary>
        bool IsFrameVerified(int tick);

        /// <summary>
        /// Returns the frame state (Predicted/Verified) of the specified tick.
        /// </summary>
        FrameState GetFrameState(int tick);

        /// <summary>
        /// Raised when a tick transitions from Predicted to Verified (tick).
        /// </summary>
        event Action<int> OnFrameVerified;

        /// <summary>
        /// Tick execution event that also delivers the frame state (tick, state).
        /// </summary>
        event Action<int, FrameState> OnTickExecutedWithState;

        /// <summary>
        /// Regular event raised on a predicted tick (tick, event).
        /// </summary>
        event Action<int, SimulationEvent> OnEventPredicted;

        /// <summary>
        /// Predicted event confirmed as-is by the verification result (tick, event).
        /// </summary>
        event Action<int, SimulationEvent> OnEventConfirmed;

        /// <summary>
        /// Predicted event canceled by rollback (tick, event).
        /// </summary>
        event Action<int, SimulationEvent> OnEventCanceled;

        /// <summary>
        /// Synced event dispatched only on verified ticks (tick, event).
        /// </summary>
        event Action<int, SimulationEvent> OnSyncedEvent;

        /// <summary>
        /// Full-state resync complete event (restoredTick).
        /// </summary>
        event Action<int> OnResyncCompleted;

        /// <summary>
        /// Raised when an empty input is needed for a disconnected player. Invoked synchronously when CanAdvanceTick fails.
        /// </summary>
        event Action<int> OnDisconnectedInputNeeded;

        void NotifyPlayerDisconnected(int playerId);
        void NotifyPlayerReconnected(int playerId);
        void NotifyPlayerLeft(int playerId);
        void PauseForReconnect();
        void ForceInsertCommand(ICommand cmd);
        /// <summary>
        /// Range-fills empty commands for [fromTick, toTickInclusive] of a single player and
        /// triggers a single chain-advance pass. Used by reactive fill to catch the chain
        /// up to CurrentTick when a peer is presumed-dropped or transport-disconnected.
        /// Each inserted entry is sealed so a late real packet at the same (tick, playerId)
        /// is silently dropped — preserves InputBuffer ↔ simulation state consistency.
        /// </summary>
        void ForceInsertEmptyCommandsRange(int playerId, int fromTick, int toTickInclusive);
        bool HasCommand(int tick, int playerId);

        /// <summary>
        /// Returns true if (tick, playerId) is currently sealed in the InputBuffer (by a
        /// prior range-fill placeholder). Network layer uses this to suppress relay of late
        /// real packets that would overwrite the sealed empty on other peers.
        /// </summary>
        bool IsCommandSealed(int tick, int playerId);

        /// <summary>
        /// Requests a deferred rollback to targetTick. Merged at frame end if multiple
        /// requests target different ticks (smallest wins).
        /// </summary>
        void RequestRollback(int targetTick);

        /// <summary>
        /// Raised when full-state resync fails after the maximum retry count.
        /// </summary>
        event Action OnResyncFailed;

        /// <summary>
        /// Raised on a client when the server rejects one of this client's commands (tick, commandTypeId, reason).
        /// Hint-only — game code may clear local latches or surface to UI; loss is tolerated by design.
        /// </summary>
        event Action<int, int, RejectionReason> OnCommandRejected;

        // ── Frame References ──

        /// <summary>
        /// Frame at the latest verified tick. Used as the source for remote entity snapshot interpolation.
        /// Frame is null if outside the ring buffer range.
        /// </summary>
        FrameRef VerifiedFrame { get; }

        /// <summary>
        /// Live frame at CurrentTick. Used for local player predictive responsiveness.
        /// </summary>
        FrameRef PredictedFrame { get; }

        /// <summary>
        /// Frame at CurrentTick - 1. One side of the interpolation pair that absorbs the rate difference between the render clock and the simulation clock.
        /// Frame is null if outside the ring buffer range.
        /// </summary>
        FrameRef PredictedPreviousFrame { get; }

        /// <summary>
        /// Head frame at the entry point of the previous Update.
        /// The delta vs. the current Predicted becomes the input for visual correction (error visual) due to rollback.
        /// </summary>
        FrameRef PreviousUpdatePredictedFrame { get; }

        /// <summary>
        /// View-layer render clock state. Used by both the prediction path and the snapshot interpolation path.
        /// </summary>
        RenderClockState RenderClock { get; }

        /// <summary>
        /// Looks up the frame at the specified tick from the ring buffer. Returns false if out of range.
        /// </summary>
        bool TryGetFrameAtTick(int tick, out Frame frame);

        // ── Error Correction ──

        /// <summary>
        /// Error correction settings.
        /// </summary>
        ErrorCorrectionSettings ErrorCorrectionSettings { get; set; }

        /// <summary>
        /// Position delta produced by the rollback in this frame.
        /// Returns (0,0,0) if there was no rollback or the entity is not tracked.
        /// </summary>
        (float x, float y, float z) GetPositionDelta(int entityIndex);

        /// <summary>
        /// Y-axis rotation delta (radians) produced by the rollback in this frame.
        /// </summary>
        float GetYawDelta(int entityIndex);

        /// <summary>
        /// Whether a teleport was detected for the entity during the rollback in this frame.
        /// If true, the view-side visual correction accumulation must be reset.
        /// </summary>
        bool HasEntityTeleported(int entityIndex);

        /// <summary>
        /// Whether the engine is running in spectator mode. Prediction and rollback are not performed in spectator mode.
        /// </summary>
        bool IsSpectatorMode { get; }

        /// <summary>
        /// Starts the engine in spectator mode.
        /// </summary>
        void StartSpectator(SpectatorStartInfo info);

        /// <summary>
        /// Injects a confirmed command into the input buffer. Used in spectator mode and during Late Join catchup.
        /// </summary>
        void ReceiveConfirmedCommand(ICommand command);

        void StartCatchingUp();
        void StopCatchingUp();
        void ConfirmCatchupTick(int tick);
        event Action OnCatchupComplete;

        void ExpectFullState();
        void CancelExpectFullState();

        /// <summary>
        /// Raised periodically with a batch of verified inputs (startTick, tickCount, data, dataLength).
        /// Handlers must process synchronously; the data buffer is released immediately after return.
        /// </summary>
        event Action<int, int, byte[], int> OnVerifiedInputBatchReady;

        /// <summary>
        /// Serializes verified inputs in the [fromTick, toTick] range.
        /// Returns false if the range is outside the input buffer.
        /// </summary>
        bool TrySerializeVerifiedInputRange(int fromTick, int toTick, out byte[] data, out int dataLength);

        /// <summary>
        /// Returns the nearest snapshot tick within the input buffer range. Returns -1 if none exists.
        /// </summary>
        int GetNearestSnapshotTickWithinBuffer();

        /// <summary>
        /// Engine initialization for spectator mode. Operates without a network service.
        /// </summary>
        void Initialize(ISimulation simulation, ILogger logger);

        // ── PlayerConfig ──

        /// <summary>
        /// Returns the per-player custom config data. Null if not yet received.
        /// </summary>
        T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase;

        /// <summary>
        /// Returns the per-player custom config data. Returns false if not yet received.
        /// </summary>
        bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase;

        /// <summary>
        /// Raised when player config data is received (playerId, firstTime).
        /// firstTime is true if this is the first time the config for this player is received.
        /// </summary>
        event Action<int, bool> OnPlayerConfigReceived;
    }
}
