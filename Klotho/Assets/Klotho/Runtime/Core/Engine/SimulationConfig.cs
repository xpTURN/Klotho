using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISimulationConfig.
    /// Created inside KlothoSession.Create(),
    /// or constructed from the deserialized result of SimulationConfigMessage.
    /// </summary>
    public class SimulationConfig : ISimulationConfig
    {
        /// <inheritdoc />
        public int TickIntervalMs { get; set; } = 25;

        /// <inheritdoc />
        public int InputDelayTicks { get; set; } = 4;

        /// <inheritdoc />
        public int MaxRollbackTicks { get; set; } = 50;

        /// <inheritdoc />
        public int SyncCheckInterval { get; set; } = 30;

        /// <inheritdoc />
        public bool UsePrediction { get; set; } = true;

        /// <inheritdoc />
        public int MaxEntities { get; set; } = 256;

        /// <inheritdoc />
        public NetworkMode Mode { get; set; } = NetworkMode.P2P;

        // --- ServerDriven ---

        /// <inheritdoc />
        public int HardToleranceMs { get; set; } = 0;

        /// <inheritdoc />
        public int InputResendIntervalMs { get; set; } = 25;

        /// <inheritdoc />
        public int MaxUnackedInputs { get; set; } = 30;

        /// <inheritdoc />
        public int ServerSnapshotRetentionTicks { get; set; } = 0;

        /// <inheritdoc />
        public int SDInputLeadTicks { get; set; } = 0;

        // --- ErrorCorrection ---

        /// <inheritdoc />
        public bool EnableErrorCorrection { get; set; } = false;

        /// <inheritdoc />
        public int InterpolationDelayTicks { get; set; } = 3;

        // --- Diagnostics ---

        /// <inheritdoc />
        public int EventDispatchWarnMs { get; set; } = 5;

        /// <inheritdoc />
        public int TickDriftWarnMultiplier { get; set; } = 2;
    }
}
