using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation engine parameters. Immutable during a session.
    /// The host has authority; guests initialize the engine using values received from the host.
    /// </summary>
    public interface ISimulationConfig
    {
        /// <summary>
        /// Tick interval (milliseconds). Determines the simulation period.
        /// 25ms = 40 ticks/sec, 50ms = 20 ticks/sec. Smaller values give faster response but increase network load.
        /// Range: 1 or greater. Typically 16~50ms.
        /// </summary>
        int TickIntervalMs { get; }

        /// <summary>
        /// Input delay tick count. Shifts the tick of local input commands to <c>CurrentTick + InputDelayTicks</c>
        /// to allow headroom for network send/receive.
        /// Larger values are more resilient to network latency but make controls feel less responsive.
        /// Range: 0 or greater. Typically 2~6. (TickIntervalMs × InputDelayTicks = effective input delay in ms)
        ///
        /// P2P: Primary parameter for absorbing host/guest network round-trip time.
        /// ServerDriven: Applied as a targetTick shift on commands sent from the client to the server
        /// (server arrival headroom = (InputDelayTicks + SDInputLeadTicks) × TickIntervalMs).
        /// The SD client's lead tick count over the server is governed by <see cref="SDInputLeadTicks"/>,
        /// while the server's input reception deadline is controlled by <see cref="HardToleranceMs"/>. (The three parameters are independent and additive.)
        /// </summary>
        int InputDelayTicks { get; }

        /// <summary>
        /// Maximum rollback tick count. The maximum range that can be rewound on prediction mismatch.
        /// Determines the snapshot ring buffer size and the input buffer retention range.
        /// Range: 1 or greater, must be at least SyncCheckInterval. Typically 30~100.
        /// </summary>
        int MaxRollbackTicks { get; }

        /// <summary>
        /// Sync hash verification interval (in ticks). State hashes are exchanged every N ticks to detect desyncs.
        /// Smaller values detect desyncs sooner but increase network traffic.
        /// Range: 1 or greater, must be at most MaxRollbackTicks.
        /// </summary>
        int SyncCheckInterval { get; }

        /// <summary>
        /// Whether prediction is enabled. If true, missing remote inputs are predicted to advance ticks, and rollback occurs on mismatch.
        /// If false, the engine waits for all inputs to arrive (transitions to Paused state).
        /// </summary>
        bool UsePrediction { get; }

        /// <summary>
        /// Maximum number of entities. Determines the EntityManager array size in EcsSimulation.
        /// </summary>
        int MaxEntities { get; }

        /// <summary>
        /// Network mode (P2P / ServerDriven).
        /// Discriminator for SD-only fields. Referenced every frame in the engine Update.
        /// </summary>
        NetworkMode Mode { get; }

        // --- ServerDriven (only valid when Mode == ServerDriven) ---

        /// <summary>
        /// Wall-clock deadline (milliseconds) for the server to accept a cmd, measured from that tick's execution time.
        /// deadline = now_at_tick(cmd.Tick) + HardToleranceMs.
        /// If 0, computed automatically (if SDInputLeadTicks=0 it falls back to the default of 10 — see <see cref="SimulationConfigExtensions.SDInputLeadTicksDefault"/>):
        ///   - Initial value: (effectiveLeadTicks + InputDelayTicks + 1) × TickIntervalMs + 60ms (assumed RTT/2) + 20ms (jitter)
        ///   - After the first handshake completes: (effectiveLeadTicks + InputDelayTicks + 1) × TickIntervalMs + avgRtt/2 + 20ms
        /// Since <see cref="SDInputLeadTicks"/> and <see cref="InputDelayTicks"/> are reflected automatically,
        /// 0 (auto) is generally recommended. Manual settings are for advanced tuning of network conditions.
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
        /// cmd reception deadline headroom = (effectiveLeadTicks + InputDelayTicks) × TickIntervalMs
        /// (since the window opens L+D ticks later, the minimum required H decreases by the same amount).
        /// effectiveLeadTicks = SDInputLeadTicks > 0 ? SDInputLeadTicks : 10 (<see cref="SimulationConfigExtensions.SDInputLeadTicksDefault"/>).
        /// Acts additively with <see cref="InputDelayTicks"/>,
        /// and is reflected in the <see cref="HardToleranceMs"/> auto-calc.
        /// </summary>
        int SDInputLeadTicks { get; }

        // --- ErrorCorrection ---

        /// <summary>
        /// Whether Error Correction is enabled. If false (default), all EC computation is disabled.
        /// Enable selectively in multiplayer / high-latency environments by switching to true.
        /// </summary>
        bool EnableErrorCorrection { get; }

        /// <summary>
        /// Snapshot interpolation delay tick count for the View layer.
        /// When computing RenderClock.VerifiedBaseTick, <c>LastVerifiedTick - InterpolationDelayTicks</c> is applied.
        /// Larger values give more jitter-absorption headroom but also increase the render delay of remote entities. Recommended range is [1, 3].
        /// On SD clients, this acts as the upper bound when AdaptiveRenderClock adjusts dynamically.
        /// </summary>
        int InterpolationDelayTicks { get; }

        /// <summary>
        /// Safety margin (ticks) added to RTT-based extra-delay computation for LateJoin/Reconnect.
        /// Also used as the standalone fallback value when the server-side avgRtt is unavailable or out of sane range.
        /// </summary>
        int LateJoinDelaySafety { get; }

        /// <summary>
        /// Upper bound (ms) for accepting avgRtt as a sane RTT measurement.
        /// Values exceeding this fall back to <see cref="LateJoinDelaySafety"/> only.
        /// </summary>
        int RttSanityMaxMs { get; }

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

        /// <summary>
        /// Lower-bound floor (ticks) for the chain-stall abort watchdog.
        /// Effective threshold = max(SessionConfig.ReconnectTimeoutMs / TickIntervalMs + 100, MinStallAbortTicks).
        /// Guards against misconfigurations where ReconnectTimeoutMs is unusually small or zero —
        /// without this floor the watchdog would fire almost immediately.
        /// Default 600 (30s @ 50ms tick).
        /// </summary>
        int MinStallAbortTicks { get; }

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
    }
}
