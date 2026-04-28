using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message used by the host to deliver the entire SimulationConfig to a guest.
    /// Sent immediately after the handshake (SyncComplete) and before the Ready exchange.
    /// The guest uses the received values to initialize EcsSimulation + KlothoEngine (host-authoritative model).
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SimulationConfig)]
    public partial class SimulationConfigMessage : NetworkMessageBase
    {
        [KlothoOrder] public int TickIntervalMs;
        [KlothoOrder] public int InputDelayTicks;
        [KlothoOrder] public int MaxRollbackTicks;
        [KlothoOrder] public int SyncCheckInterval;
        [KlothoOrder] public bool UsePrediction;
        [KlothoOrder] public int MaxEntities;
        [KlothoOrder] public int Mode; // NetworkMode as int
        [KlothoOrder] public int HardToleranceMs;
        [KlothoOrder] public int InputResendIntervalMs;
        [KlothoOrder] public int MaxUnackedInputs;
        [KlothoOrder] public int ServerSnapshotRetentionTicks;
        [KlothoOrder] public int SDInputLeadTicks;
        [KlothoOrder] public bool EnableErrorCorrection;
        [KlothoOrder] public int InterpolationDelayTicks;
        [KlothoOrder] public int EventDispatchWarnMs;
        [KlothoOrder] public int TickDriftWarnMultiplier;

        /// <summary>
        /// Populates message fields from an ISimulationConfig.
        /// </summary>
        public void CopyFrom(Core.ISimulationConfig config)
        {
            TickIntervalMs = config.TickIntervalMs;
            InputDelayTicks = config.InputDelayTicks;
            MaxRollbackTicks = config.MaxRollbackTicks;
            SyncCheckInterval = config.SyncCheckInterval;
            UsePrediction = config.UsePrediction;
            MaxEntities = config.MaxEntities;
            Mode = (int)config.Mode;
            HardToleranceMs = config.HardToleranceMs;
            InputResendIntervalMs = config.InputResendIntervalMs;
            MaxUnackedInputs = config.MaxUnackedInputs;
            ServerSnapshotRetentionTicks = config.ServerSnapshotRetentionTicks;
            SDInputLeadTicks = config.SDInputLeadTicks;
            EnableErrorCorrection = config.EnableErrorCorrection;
            InterpolationDelayTicks = config.InterpolationDelayTicks;
            EventDispatchWarnMs = config.EventDispatchWarnMs;
            TickDriftWarnMultiplier = config.TickDriftWarnMultiplier;
        }

        /// <summary>
        /// Creates a SimulationConfig from the message fields.
        /// </summary>
        public Core.SimulationConfig ToSimulationConfig()
        {
            return new Core.SimulationConfig
            {
                TickIntervalMs = TickIntervalMs,
                InputDelayTicks = InputDelayTicks,
                MaxRollbackTicks = MaxRollbackTicks,
                SyncCheckInterval = SyncCheckInterval,
                UsePrediction = UsePrediction,
                MaxEntities = MaxEntities,
                Mode = (NetworkMode)Mode,
                HardToleranceMs = HardToleranceMs,
                InputResendIntervalMs = InputResendIntervalMs,
                MaxUnackedInputs = MaxUnackedInputs,
                ServerSnapshotRetentionTicks = ServerSnapshotRetentionTicks,
                SDInputLeadTicks = SDInputLeadTicks,
                EnableErrorCorrection = EnableErrorCorrection,
                InterpolationDelayTicks = InterpolationDelayTicks,
                EventDispatchWarnMs = EventDispatchWarnMs,
                TickDriftWarnMultiplier = TickDriftWarnMultiplier,
            };
        }
    }
}
