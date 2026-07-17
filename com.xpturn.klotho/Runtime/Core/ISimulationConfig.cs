using System.Collections.Generic;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation engine parameters. Immutable during a session.
    /// The host has authority; guests initialize the engine using values received from the host.
    /// </summary>
    public interface ISimulationConfig
    {
        // --- Tick Loop ---

        /// <summary>
        /// Tick interval (milliseconds). Determines the simulation period.
        /// 25ms = 40 ticks/sec, 50ms = 20 ticks/sec. Smaller values give faster response but increase network load.
        /// Range: 1 or greater. Typically 16~50ms.
        /// </summary>
        int TickIntervalMs { get; }

        /// <summary>
        /// Maximum number of entities. Determines the EntityManager array size in EcsSimulation.
        /// </summary>
        int MaxEntities { get; }

        /// <summary>
        /// Maximum number of ticks executed per frame during Late Join catch-up.
        /// The upper bound on ticks executed in a single frame while catching up to the current tick after a Late Join.
        /// Larger values catch up faster but may cause frame hitching.
        /// Range: 1 or greater. Typically 100~500.
        /// </summary>
        int CatchupMaxTicksPerFrame { get; }

        // --- Input ---

        /// <summary>
        /// Input delay tick count. Shifts the tick of local input commands to <c>CurrentTick + InputDelayTicks</c>
        /// to allow headroom for network send/receive.
        /// Larger values are more resilient to network latency but make controls feel less responsive.
        /// Range: 0 or greater. Typically 2~6. (TickIntervalMs × InputDelayTicks = effective input delay in ms)
        ///
        /// P2P: Primary parameter for absorbing host/guest network round-trip time.
        /// ServerDriven: Applied as a targetTick shift on commands sent from the client to the server
        /// (server arrival headroom = (InputDelayTicks + SDInputLeadTicks) × TickIntervalMs).
        /// The SD client's lead tick count over the server is governed by <see cref="SDInputLeadTicks"/>.
        /// (The two parameters are independent and additive; the server accepts inputs until the tick executes.)
        /// </summary>
        int InputDelayTicks { get; }

        // --- Rollback ---

        /// <summary>
        /// Maximum rollback tick count. The maximum range that can be rewound on prediction mismatch.
        /// Determines the snapshot ring buffer size and the input buffer retention range.
        /// Range: 1 or greater, must be at least SyncCheckInterval. Typically 30~100.
        /// </summary>
        int MaxRollbackTicks { get; }

        // --- Sync / Resync ---

        /// <summary>
        /// Sync hash verification interval (in ticks). State hashes are exchanged every N ticks to detect desyncs.
        /// Smaller values detect desyncs sooner but increase network traffic.
        /// Range: 1 or greater, must be at most MaxRollbackTicks.
        /// </summary>
        int SyncCheckInterval { get; }

        /// <summary>
        /// Maximum retry count for Full State Resync.
        /// When desyncs occur DesyncThresholdForResync times in a row, a Resync is attempted;
        /// if this count is exceeded, the OnResyncFailed event is raised.
        /// Range: 1 or greater.
        /// </summary>
        int ResyncMaxRetries { get; }

        /// <summary>
        /// Threshold of consecutive desyncs that triggers a full state resync.
        /// When sync hash mismatches are detected this many times in a row, a Full State Resync is requested instead of a rollback.
        /// Range: 1 or greater. 1 means an immediate Resync on the first desync.
        /// </summary>
        int DesyncThresholdForResync { get; }

        /// <summary>
        /// Minimum interval between consecutive corrective resets (milliseconds).
        /// Prevents broadcast storms when persistent hash divergence fires OnHashMismatch repeatedly.
        /// Range: 1000 or greater. Default 5000.
        /// </summary>
        int CorrectiveResetCooldownMs { get; }

        /// <summary>
        /// Maximum corrective-reset attempts the host spends per divergence episode (recovery
        /// ladder rung 3). Attempts decay back to zero after a quiet period of
        /// max(CorrectiveResetCooldownMs x 2, ResyncMaxRetries x RESYNC_TIMEOUT_MS +
        /// CorrectiveResetCooldownMs) without failure reports (must exceed the worst-case
        /// RetryExhausted cadence). Host-local — not propagated
        /// via SimulationConfigMessage. Range: 1 or greater. Default 2.
        /// </summary>
        int CorrectiveResetMaxAttempts { get; }

        /// <summary>
        /// When corrective-reset attempts are exhausted (rung 4), whether the host
        /// broadcasts MatchAbort and aborts locally with AbortReason.StateDivergence.
        /// False logs an error and leaves the decision to the game layer. Host-local. Default true.
        /// </summary>
        bool AutoAbortOnRecoveryExhausted { get; }

        // --- Prediction ---

        /// <summary>
        /// Whether prediction is enabled. If true, missing remote inputs are predicted to advance ticks, and rollback occurs on mismatch.
        /// If false, the engine waits for all inputs to arrive (transitions to Paused state).
        /// </summary>
        bool UsePrediction { get; }

        // --- Network Mode ---

        /// <summary>
        /// Network mode (P2P / ServerDriven).
        /// Discriminator for SD-only fields. Referenced every frame in the engine Update.
        /// </summary>
        NetworkMode Mode { get; }

        // --- ServerDriven (only valid when Mode == ServerDriven) ---

        /// <summary>
        /// Deprecated — has no effect. The server's effective input deadline is the
        /// tick's execution moment: inputs missing at execution are substituted with EmptyCommand,
        /// later arrivals are past-tick rejected, and chronic lateness self-corrects via client
        /// lead escalation (DynamicInputDelayPolicy + server recommended-delay push).
        /// The property and its wire fields are retained for serialized-asset and message
        /// compatibility only.
        /// </summary>
        int HardToleranceMs { get; }

        /// <summary>
        /// Interval (milliseconds) at which the client resends unacknowledged inputs.
        /// </summary>
        int InputResendIntervalMs { get; }

        /// <summary>
        /// Cap on the accumulated unacknowledged inputs. The client warns when this is exceeded.
        /// </summary>
        int MaxUnackedInputs { get; }

        /// <summary>
        /// Number of slots in the server snapshot ring buffer. If 0, computed automatically: TickRate x 10.
        /// Independent of MaxRollbackTicks — retains past snapshots to serve FullStateRequest responses.
        /// </summary>
        int ServerSnapshotRetentionTicks { get; }

        /// <summary>
        /// Initial lead tick count the client should secure when the game starts in SD mode.
        /// If 0, the default of 10 is used.
        /// On LateJoin/Reconnect recovery, the same value is used to re-establish the lead.
        /// Ignored in P2P mode.
        /// effectiveLeadTicks = SDInputLeadTicks > 0 ? SDInputLeadTicks : 10 (<see cref="SimulationConfigExtensions.SDInputLeadTicksDefault"/>).
        /// Acts additively with <see cref="InputDelayTicks"/>.
        /// </summary>
        int SDInputLeadTicks { get; }

        // --- Error Correction ---

        /// <summary>
        /// Whether Error Correction is enabled. If false (default), all EC computation is disabled.
        /// Enable selectively in multiplayer / high-latency environments by switching to true.
        /// </summary>
        bool EnableErrorCorrection { get; }

        /// <summary>
        /// Snapshot interpolation delay tick count for the View layer.
        /// When computing RenderClock.VerifiedBaseTick, <c>LastVerifiedTick - InterpolationDelayTicks</c> is applied.
        /// Larger values give more jitter-absorption headroom but also increase the render delay of remote entities. Recommended range is [1, 3].
        /// Fixed value — applied as-is by the live render clock (AdvanceVerifiedRenderTime); no dynamic adjustment.
        /// </summary>
        int InterpolationDelayTicks { get; }

        // --- P2P Quorum-Miss Watchdog ---

        /// <summary>
        /// P2P quorum-miss watchdog threshold (ticks). If a remote peer's input is missing at
        /// _lastVerifiedTick + 1 for at least this many ticks (CurrentTick - _lastVerifiedTick),
        /// the peer is presumed-dropped and reactive empty-fill is activated before the
        /// transport-level DisconnectTimeout fires.
        /// Range: 0 or greater. 0 disables the watchdog. Typically 20 (1s @ 50ms tick).
        /// Tuning: too low causes false-positive rollback thrash on normal jitter; too high
        /// delays recovery. Safe range 10~80, sweet spot 20~40.
        /// </summary>
        int QuorumMissDropTicks { get; }

        // --- Reactive Dynamic InputDelay ---

        /// <summary>
        /// Sliding-window length (ticks) over which non-spawn PastTick reject counts accumulate
        /// for client-reactive Dynamic InputDelay escalation. Resets the reject counter when the
        /// current tick advances past windowStart + ReactiveWindowTicks.
        /// </summary>
        int ReactiveWindowTicks { get; }

        /// <summary>
        /// Reject count within ReactiveWindowTicks that triggers a reactive EscalateExtraDelay call.
        /// </summary>
        int ReactiveEscalateThreshold { get; }

        /// <summary>
        /// Step size (ticks) added to RecommendedExtraDelay per reactive escalation.
        /// </summary>
        int ReactiveStep { get; }

        /// <summary>
        /// Upper bound (ticks) on RecommendedExtraDelay enforced by reactive escalation.
        /// </summary>
        int ReactiveMax { get; }

        /// <summary>
        /// Grace window (ticks) following a server-pushed ExtraDelay change during which
        /// reactive triggers are suppressed (absorbs in-flight commands and resim residue).
        /// </summary>
        int ServerPushGraceTicks { get; }

        /// <summary>
        /// Minimum gap (ticks) between successive reactive escalations to prevent runaway bumps.
        /// </summary>
        int ReactiveEscalateCooldownTicks { get; }

        /// <summary>
        /// Stable-interval dwell (ticks) with no PastTick reject / rollback-burst before the client
        /// decays its reactive extra-delay correction by one ReactiveStep.
        /// Biased conservative (≳ 2× escalate cooldown) to avoid thrash during transient lulls.
        /// </summary>
        int ReactiveDeEscalateStableTicks { get; }

        // --- Rollback Burst ---

        /// <summary>
        /// Rollback event count within RollbackWindowTicks that triggers a reactive escalation
        /// on the P2P guest fallback path.
        /// </summary>
        int RollbackBurstCount { get; }

        /// <summary>
        /// Sliding-window length (ticks) over which rollback events accumulate for burst detection.
        /// </summary>
        int RollbackWindowTicks { get; }

        // --- Diagnostics (DEVELOPMENT_BUILD / UNITY_EDITOR only) ---

        /// <summary>
        /// Warning threshold for OnEvent* handler execution time (milliseconds).
        /// If a handler takes at least this long, a warning log is emitted.
        /// 0 or less disables runtime instrumentation.
        /// </summary>
        int EventDispatchWarnMs { get; }

        /// <summary>
        /// Warning multiplier for tick loop interval drift.
        /// If the actual tick interval exceeds TickIntervalMs × this multiplier, a warning log is emitted.
        /// 0 or less disables instrumentation.
        /// </summary>
        int TickDriftWarnMultiplier { get; }

        /// <summary>
        /// Number of recent ticks retained in the desync-diagnostic hash history. 0 disables it.
        /// When &gt; 0 the simulation accumulates a per-tick layered <c>StateHashBreakdown</c> into a
        /// pre-allocated numeric ring (zero-GC on the steady path), so a desync can be localized to a
        /// component type or system participant from any role's local log, not just the SD prediction
        /// path. Unlike the state hash itself this is diagnostic-only and never crosses the wire
        /// (not part of SimulationConfigMessage), so peers may run different values.
        ///
        /// <para>Cost is asymmetric: the SD server / SD verified-resim paths already hash every tick,
        /// so accumulation is a free byproduct there. The P2P forward/rollback paths only hash on check
        /// ticks, so a non-zero value buys a NEW per-tick full-state hash (~0.1% of the tick budget for a
        /// typical world). Off ⇒ P2P history degrades gracefully to check-tick granularity. The default is
        /// 60 (diagnostics ON, opt-OUT): a desync is rare and expensive to reproduce, so the small steady
        /// cost is paid by default and a game that cannot afford it sets 0. Replay engines (null network
        /// service) never accumulate regardless — there is no peer to diverge from, and replaying a
        /// recording against its own capture is a separate feature.</para>
        /// </summary>
        int DiagnosticHistoryTicks { get; }

        /// <summary>
        /// Gate for the component-memory peak sampler (dev/measurement only). Default false (opt-in) —
        /// distinct from <see cref="DiagnosticHistoryTicks"/> (desync hash history, opt-out). When true,
        /// a <c>ComponentMemoryPeakSampler</c> may be wired to accumulate per-type high-water live counts
        /// for component-storage sizing. Read-only, zero-GC on the steady path; off ⇒ no subscription.
        /// Per-peer local diagnostic — not carried in <c>SimulationConfigMessage</c>.
        /// </summary>
        bool ComponentMemoryPeakSampling { get; }

        // --- Multi-stage ---

        /// <summary>
        /// Stage selector for a single dedicated server hosting multiple stages. Opaque to the core
        /// (never interpreted here) — the game looks it up against its baked stage definitions in
        /// RegisterSystems/OnInitializeWorld. 0 = the default single stage (no-regression default).
        /// Authoritative value set by the server/host and propagated to joiners via SimulationConfigMessage.
        /// </summary>
        int StageId { get; }

        /// <summary>
        /// Opaque game-defined match-config payload (game mode / ruleset / difficulty / etc.), serialized
        /// by the game. The core only carries the bytes — meaning is owned by the game (cf.
        /// PlayerConfigMessage.ConfigData, at match scope). null/empty = none (no-regression default).
        /// </summary>
        byte[] MatchConfigData { get; }

        // --- Component storage ---

        /// <summary>
        /// Per-component maxCount overrides (typeId → max concurrent instances) — caps dense/components
        /// slots at <c>min(value, MaxEntities)</c>, layering in front of the build-baked
        /// <see cref="ECS.KlothoComponentAttribute.MaxCount"/>. Empty = each type keeps its attribute cap.
        /// Process-global layout input: must be uniform across all concurrent sessions/rooms in a process
        /// (same trust model as <see cref="MaxEntities"/>). ServerDriven = server push via
        /// SimulationConfigMessage; P2P = same asset. Immutable during a session.
        /// </summary>
        IReadOnlyDictionary<int, int> ComponentMaxCountOverrides { get; }

        /// <summary>
        /// Reservation-pruning denylist: component typeIds this session skips reserving.
        /// null/empty = reserve every registered type (no pruning, current byte-identical behavior).
        /// When non-empty, these typeIds (minus the engine <c>[KlothoCoreComponent]</c> base-set, which is
        /// force-excluded by the registry) get NO storage layout — they are elided from heap/serialize/hash
        /// entirely; everything else is reserved. Process-global layout input like
        /// <see cref="ComponentMaxCountOverrides"/>: must be uniform across peers (build-fixed) and is carried
        /// on the same wire (<c>SimulationConfigMessage</c>) so server-pushed clients prune the identical set.
        /// Declare type-based (resolve via <c>ComponentStorageRegistry.GetTypeId&lt;T&gt;()</c>), never as raw
        /// magic ints. Only list a type here when NO registered system touches it (a <c>Filter</c>/<c>Has</c>
        /// opens its storage even at 0 instances — pruning a touched type throws at <c>GetStorage</c>).
        /// </summary>
        IReadOnlyCollection<int> PrunedComponentTypeIds { get; }

        /// <summary>
        /// Runtime-only prune-set injection: makes <see cref="PrunedComponentTypeIds"/> return
        /// <paramref name="typeIds"/> WITHOUT mutating any authored/serialized backing (Unity .asset /
        /// Godot .tres), so an editor/host code path can enable pruning for a session without persisting to
        /// the saved config. null/empty = no pruning. Uniform across all implementations — the injection
        /// site (e.g. a P2P host) calls this polymorphically on <see cref="ISimulationConfig"/>.
        /// </summary>
        void SetRuntimePrunedComponentTypeIds(int[] typeIds);
    }
}
