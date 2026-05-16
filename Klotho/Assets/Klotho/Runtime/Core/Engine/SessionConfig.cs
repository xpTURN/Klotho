namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISessionConfig.
    /// Constructed from KlothoSessionSetup values inside KlothoSession.Create(),
    /// or from the deserialized result of GameStartMessage / LateJoinAcceptMessage.
    /// </summary>
    public class SessionConfig : ISessionConfig
    {
        /// <inheritdoc />
        public int RandomSeed { get; set; } = 0;

        /// <inheritdoc />
        public int MaxPlayers { get; set; } = 4;

        /// <inheritdoc />
        public int MinPlayers { get; set; } = 2;

        /// <inheritdoc />
        public bool AllowLateJoin { get; set; } = true;

        /// <inheritdoc />
        public int ReconnectTimeoutMs { get; set; } = 60000;

        /// <inheritdoc />
        public int ReconnectMaxRetries { get; set; } = 3;

        /// <inheritdoc />
        public int LateJoinDelayTicks { get; set; } = 10;

        /// <inheritdoc />
        public int ResyncMaxRetries { get; set; } = 3;

        /// <inheritdoc />
        public int DesyncThresholdForResync { get; set; } = 3;

        /// <inheritdoc />
        public int CorrectiveResetCooldownMs { get; set; } = 5000;

        /// <inheritdoc />
        public int CountdownDurationMs { get; set; } = 3000;

        /// <inheritdoc />
        public int CatchupMaxTicksPerFrame { get; set; } = 200;

        /// <inheritdoc />
        public int MaxSpectators { get; set; } = 0;
    }
}
