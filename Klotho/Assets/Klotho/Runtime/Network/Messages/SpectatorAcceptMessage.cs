using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Spectator accept message. Carries every field of <see cref="Core.SimulationConfig"/> and <see cref="Core.SessionConfig"/>
    /// so the spectator engine initializes with the same determinism model and session policy as the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire format is positional serialization based on <see cref="KlothoOrderAttribute"/>, so new fields must always be
    /// <b>appended after existing fields</b>; insertion in the middle is forbidden. Server and client builds must be rolled out together.
    /// </para>
    /// <para>
    /// When a new field is added to <see cref="Core.SimulationConfig"/> or <see cref="Core.SessionConfig"/>,
    /// the same field must be added here, and the mappings in <c>CopySimulationConfigFrom</c> / <c>CopySessionConfigFrom</c>
    /// and <c>ToSimulationConfig</c> / <c>ToSessionConfig</c> must be updated together.
    /// If omitted, the spectator engine runs with default values and diverges subtly from the server.
    /// </para>
    /// <para>
    /// <c>SessionConfig.RandomSeed</c> overlaps with the top-level <see cref="RandomSeed"/>, so it is not included in this message.
    /// Exclude this field when using automatic mapping IDE assistance.
    /// </para>
    /// </remarks>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SpectatorAccept)]
    public partial class SpectatorAcceptMessage : NetworkMessageBase
    {
        [KlothoOrder] public int SpectatorId;
        [KlothoOrder] public int RandomSeed;
        [KlothoOrder] public int CurrentTick;
        [KlothoOrder] public int LastVerifiedTick;
        [KlothoOrder] public List<int> PlayerIds = new List<int>();

        // --- Full SimulationConfig (extended to include the existing TickInterval and InputDelay) ---

        [KlothoOrder] public int TickIntervalMs;
        [KlothoOrder] public int InputDelayTicks;
        [KlothoOrder] public int MaxRollbackTicks;
        [KlothoOrder] public int SyncCheckInterval;
        [KlothoOrder] public bool UsePrediction;
        [KlothoOrder] public int MaxEntities;
        [KlothoOrder] public int Mode;
        [KlothoOrder] public int HardToleranceMs;
        [KlothoOrder] public int InputResendIntervalMs;
        [KlothoOrder] public int MaxUnackedInputs;
        [KlothoOrder] public int ServerSnapshotRetentionTicks;
        [KlothoOrder] public int EventDispatchWarnMs;
        [KlothoOrder] public int TickDriftWarnMultiplier;

        // --- SessionConfig (RandomSeed is excluded since it overlaps with the top-level field, and is back-synced in ToSessionConfig()) ---

        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ResyncMaxRetries;
        [KlothoOrder] public int DesyncThresholdForResync;
        [KlothoOrder] public int CountdownDurationMs;
        [KlothoOrder] public int CatchupMaxTicksPerFrame;

        /// <summary>
        /// Populate the SimulationConfig fields from an ISimulationConfig.
        /// </summary>
        public void CopySimulationConfigFrom(Core.ISimulationConfig config)
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
            EventDispatchWarnMs = config.EventDispatchWarnMs;
            TickDriftWarnMultiplier = config.TickDriftWarnMultiplier;
        }

        /// <summary>
        /// Build a SimulationConfig from the message fields.
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
                Mode = (Core.NetworkMode)Mode,
                HardToleranceMs = HardToleranceMs,
                InputResendIntervalMs = InputResendIntervalMs,
                MaxUnackedInputs = MaxUnackedInputs,
                ServerSnapshotRetentionTicks = ServerSnapshotRetentionTicks,
                EventDispatchWarnMs = EventDispatchWarnMs,
                TickDriftWarnMultiplier = TickDriftWarnMultiplier,
            };
        }

        /// <summary>
        /// Populate the SessionConfig fields from an <see cref="Core.ISessionConfig"/>.
        /// </summary>
        /// <remarks>
        /// <c>RandomSeed</c> is not copied because it overlaps with the top-level <see cref="RandomSeed"/>.
        /// </remarks>
        /// <param name="config">Source SessionConfig to copy from.</param>
        public void CopySessionConfigFrom(Core.ISessionConfig config)
        {
            MaxPlayers = config.MaxPlayers;
            AllowLateJoin = config.AllowLateJoin;
            ReconnectTimeoutMs = config.ReconnectTimeoutMs;
            ReconnectMaxRetries = config.ReconnectMaxRetries;
            LateJoinDelayTicks = config.LateJoinDelayTicks;
            ResyncMaxRetries = config.ResyncMaxRetries;
            DesyncThresholdForResync = config.DesyncThresholdForResync;
            CountdownDurationMs = config.CountdownDurationMs;
            CatchupMaxTicksPerFrame = config.CatchupMaxTicksPerFrame;
            MinPlayers = config.MinPlayers;
        }

        /// <summary>
        /// Build a <see cref="Core.SessionConfig"/> from the message fields.
        /// </summary>
        /// <remarks>
        /// <c>RandomSeed</c> reuses the top-level <see cref="RandomSeed"/> directly, consolidating it into a single source of truth.
        /// </remarks>
        /// <returns>A new instance composed from this message's SessionConfig fields.</returns>
        public Core.SessionConfig ToSessionConfig()
        {
            return new Core.SessionConfig
            {
                RandomSeed = RandomSeed,
                MaxPlayers = MaxPlayers,
                AllowLateJoin = AllowLateJoin,
                ReconnectTimeoutMs = ReconnectTimeoutMs,
                ReconnectMaxRetries = ReconnectMaxRetries,
                LateJoinDelayTicks = LateJoinDelayTicks,
                ResyncMaxRetries = ResyncMaxRetries,
                DesyncThresholdForResync = DesyncThresholdForResync,
                CountdownDurationMs = CountdownDurationMs,
                CatchupMaxTicksPerFrame = CatchupMaxTicksPerFrame,
                MinPlayers = MinPlayers,
            };
        }
    }
}
