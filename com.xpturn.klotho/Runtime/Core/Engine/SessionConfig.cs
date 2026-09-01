namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISessionConfig.
    /// Constructed from KlothoSessionSetup values inside KlothoSession.Create(),
    /// or from the deserialized result of GameStartMessage / LateJoinAcceptMessage.
    /// </summary>
    public class SessionConfig : ISessionConfig
    {
        // --- Determinism ---

        /// <inheritdoc />
        public int RandomSeed { get; set; } = 0;

        // --- Membership ---

        /// <inheritdoc />
        public int MaxPlayers { get; set; } = 4;

        /// <inheritdoc />
        public int MinPlayers { get; set; } = 2;

        /// <inheritdoc />
        public int MaxSpectators { get; set; } = 0;

        // --- LateJoin / Reconnect Policy ---

        /// <inheritdoc />
        public bool AllowLateJoin { get; set; } = true;

        /// <inheritdoc />
        public int LateJoinDelayTicks { get; set; } = 10;

        /// <inheritdoc />
        public int ReconnectTimeoutMs { get; set; } = 60000;

        /// <inheritdoc />
        public int ValidationTimeoutMs { get; set; } = 5000;

        /// <inheritdoc />
        public int ReconnectMaxRetries { get; set; } = 3;

        // --- LateJoin / Reconnect Tuning ---

        /// <inheritdoc />
        public int LateJoinDelaySafety { get; set; } = 2;

        /// <inheritdoc />
        public int RttSanityMaxMs { get; set; } = 240;

        // --- Chain-Stall Watchdog ---

        /// <inheritdoc />
        public int MinStallAbortTicks { get; set; } = 600;

        // --- Match Start Countdown ---

        /// <inheritdoc />
        public int CountdownDurationMs { get; set; } = 3000;

        // --- Match End Grace ---

        /// <inheritdoc />
        public int AbortGraceMs { get; set; } = 1500;

        /// <inheritdoc />
        public EndGracePolicy EndGracePolicy { get; set; } = EndGracePolicy.Continue;

        /// <inheritdoc />
        public int EndGraceMs { get; set; } = 5000;

        /// <inheritdoc />
        public int ClientShutdownGraceMs { get; set; } = 4500;

        /// <summary>
        /// A copy of this config, for a caller that needs its own instance — a room that must not share
        /// state with its neighbours, an entry point that adjusts a value without writing to the caller's
        /// object. Mirrors <c>SimulationConfig.Clone</c>, and is memberwise so a field added later is
        /// copied without anyone remembering to add it here.
        ///
        /// <para>Distinct from <see cref="CopyOf"/>, which converts ANY <see cref="ISessionConfig"/>
        /// (a Unity ScriptableObject, say) into a mutable SessionConfig and therefore has to name every
        /// field. Prefer this one when the source is already a SessionConfig.</para>
        /// </summary>
        public SessionConfig Clone() => (SessionConfig)MemberwiseClone();

        /// <summary>
        /// A field-for-field copy of any <see cref="ISessionConfig"/>, as a mutable SessionConfig.
        ///
        /// <para>Exists so an entry point can adjust a config without writing to the caller's object.
        /// That matters more than it sounds: the Unity inspector config is a ScriptableObject, and a
        /// value written into one during play mode outlives play mode — a single-player entry that
        /// forced MinPlayers = 1 in place would leave the shared config at 1 for the next multiplayer
        /// match. SimulationConfig has Clone for the same reason; this is that gap closed.</para>
        /// </summary>
        internal static SessionConfig CopyOf(ISessionConfig src) => new SessionConfig
        {
            RandomSeed = src.RandomSeed,
            MaxPlayers = src.MaxPlayers,
            MinPlayers = src.MinPlayers,
            MaxSpectators = src.MaxSpectators,
            AllowLateJoin = src.AllowLateJoin,
            LateJoinDelayTicks = src.LateJoinDelayTicks,
            ReconnectTimeoutMs = src.ReconnectTimeoutMs,
            ValidationTimeoutMs = src.ValidationTimeoutMs,
            ReconnectMaxRetries = src.ReconnectMaxRetries,
            LateJoinDelaySafety = src.LateJoinDelaySafety,
            RttSanityMaxMs = src.RttSanityMaxMs,
            MinStallAbortTicks = src.MinStallAbortTicks,
            CountdownDurationMs = src.CountdownDurationMs,
            AbortGraceMs = src.AbortGraceMs,
            EndGracePolicy = src.EndGracePolicy,
            EndGraceMs = src.EndGraceMs,
            ClientShutdownGraceMs = src.ClientShutdownGraceMs,
        };
    }
}
