namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-session mutable configuration. Determined by the host when the game starts.
    /// Propagated to guests via GameStartMessage. For Late Join, supplemented via LateJoinAcceptMessage.
    /// <para><see cref="RandomSeed"/> is the exception: it is an authored request that the authority
    /// reads at match start, never a value written back here.</para>
    /// </summary>
    public interface ISessionConfig
    {
        // --- Determinism ---

        /// <summary>
        /// Deterministic random seed — the <b>authored request</b>, not the value the match ran with.
        /// All peers must initialize the RNG with the same seed to guarantee determinism.
        /// <para>0 means "pick one": the authority (P2P host / dedicated server) generates a seed when
        /// the match starts. Any other value is honoured as-is; negative values are fine, so the only
        /// seed you cannot pin explicitly is 0 itself.</para>
        /// <para>This field is never overwritten with the effective value on the authority — reading it
        /// back after match start gives you what was authored (commonly 0), NOT the seed in play. For
        /// the effective seed read <c>IKlothoEngine.RandomSeed</c> or
        /// <c>ILockstepNetworkService.RandomSeed</c>.</para>
        /// </summary>
        int RandomSeed { get; }

        // --- Membership ---

        /// <summary>
        /// Maximum number of players. Used by the network service and view initialization.
        /// </summary>
        int MaxPlayers { get; }

        /// <summary>
        /// Minimum number of players required to start the game.
        /// The host/server only triggers <c>StartGame</c> once <c>_players.Count &gt;= MinPlayers</c> and all players are ready.
        /// Range: 1 or greater, must satisfy <c>MinPlayers &lt;= MaxPlayers</c>.
        /// Out-of-range values are clamped at session build time with a warning log.
        /// </summary>
        int MinPlayers { get; }

        /// <summary>
        /// Maximum number of spectators allowed in the session.
        /// </summary>
        int MaxSpectators { get; }

        // --- LateJoin / Reconnect Policy ---

        /// <summary>
        /// Whether Late Join is allowed. If false, requests from new players to join while the game is in progress are rejected.
        /// </summary>
        bool AllowLateJoin { get; }

        /// <summary>
        /// Tick offset at which the PlayerJoinCommand is inserted on Late Join.
        /// The join command is scheduled at current tick + LateJoinDelayTicks, giving existing players time to prepare input.
        /// Range: 1 or greater. Larger values are safer but make joining slower.
        /// </summary>
        int LateJoinDelayTicks { get; }

        /// <summary>
        /// Reconnect timeout (milliseconds). If a player does not reconnect within this window after disconnecting, they are removed.
        /// Guests also stop attempting to reconnect once this time elapses.
        /// Range: 1000 or greater. Typically 10000~60000ms.
        /// </summary>
        int ReconnectTimeoutMs { get; }

        /// <summary>
        /// SD identity-validation timeout (milliseconds). A pending async ticket validation (lobby
        /// redeem) is rejected if it does not complete within this window. Server-internal only — NOT
        /// wire-propagated (clients never read it). MUST be shorter than the client connect timeout
        /// (15s) so the server rejects with a reason before the client times out generically. SD-only;
        /// P2P validation is synchronous, so it never parks and this bound does not apply there.
        /// </summary>
        int ValidationTimeoutMs { get; }

        /// <summary>
        /// Maximum reconnect retry count. When a guest sends a ReconnectRequest/FullState request to the host
        /// and receives no response, it retries. Exceeding this count is treated as reconnect failure.
        /// Range: 0 or greater. 0 means a single attempt with no retries.
        /// </summary>
        int ReconnectMaxRetries { get; }

        // --- LateJoin / Reconnect Tuning ---

        /// <summary>
        /// Safety margin (ticks) added to RTT-based extra-delay computation for LateJoin/Reconnect.
        /// Also used as the standalone fallback value when the server-side avgRtt is unavailable or out of sane range.
        /// </summary>
        int LateJoinDelaySafety { get; }

        /// <summary>
        /// Upper bound (ms) for accepting avgRtt as a sane RTT measurement.
        /// Values exceeding this are clamped to this bound (not discarded) before the
        /// extra-delay computation.
        /// </summary>
        int RttSanityMaxMs { get; }

        // --- Chain-Stall Watchdog ---

        /// <summary>
        /// Lower-bound floor (ticks) for the chain-stall abort watchdog.
        /// Effective threshold = max(ReconnectTimeoutMs / TickIntervalMs + 100, MinStallAbortTicks).
        /// Guards against misconfigurations where ReconnectTimeoutMs is unusually small or zero —
        /// without this floor the watchdog would fire almost immediately.
        /// Default 600 (30s @ 50ms tick).
        /// </summary>
        int MinStallAbortTicks { get; }

        // --- Match Start Countdown ---

        /// <summary>
        /// Game start countdown duration (milliseconds). After all players are ready,
        /// the game starts simultaneously after waiting this duration based on SharedClock.
        /// Compensates for network latency differences to guarantee a synchronized start.
        /// Range: 0 or greater. 0 means immediate start.
        /// </summary>
        int CountdownDurationMs { get; }

        // --- Match End Grace ---

        /// <summary>
        /// Post-match grace duration on abort (milliseconds). Time between OnMatchAborted fire and
        /// Room.State transition to Draining, giving clients time to display an error dialog
        /// (connection lost / chain stall notice) and server side time for abort logging. Default 1500.
        /// Range: 0 or greater. Typically shorter than EndGraceMs since abort uses an error UI, not a
        /// result screen.
        /// </summary>
        int AbortGraceMs { get; }

        /// <summary>
        /// Simulation behavior during the post-match grace window. Continue (default) keeps the
        /// simulation running so input/heartbeat/replay continuity is preserved; Pause halts tick
        /// advancement (KlothoState transitions Running -&gt; Ending). See EndGracePolicy.
        /// </summary>
        EndGracePolicy EndGracePolicy { get; }

        /// <summary>
        /// Post-match grace duration on normal end (milliseconds). Time between OnMatchEnded fire and
        /// Room.State transition to Draining, giving clients time to display the result screen and
        /// server side time for any post-processing hook. Default 5000.
        /// Range: 0 (immediate drain, debug/integration only) or greater.
        /// </summary>
        int EndGraceMs { get; }

        /// <summary>
        /// Client-side grace duration on normal end (milliseconds). Time between OnMatchEnded fire
        /// on the client and the client's self-initiated session shutdown, so the result screen
        /// plays out before the chain-stall warning storm begins. Default 4500 — 500ms shorter than
        /// EndGraceMs so the client tears down before the server drain disables verified broadcasts.
        /// Range: 0 or greater. Must stay below EndGraceMs; inversion risks chain-stall warnings.
        /// Raise EndGraceMs or lower this if P99 RTT exceeds the default margin (500ms).
        /// </summary>
        int ClientShutdownGraceMs { get; }
    }
}
