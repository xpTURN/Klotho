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

        /// <summary>
        /// Per-player roster at spectate time (PlayerId + ConnectionState + ReadyState + Account + DisplayName).
        /// Replaces the former <c>List&lt;int&gt; PlayerIds</c>; <see cref="RosterEntry.PlayerId"/> carries the id so a
        /// separate list would be pure duplication. Wire-format change is intentional and not backward compatible —
        /// server and client builds must be rolled out together (single package).
        /// </summary>
        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();

        // --- SimulationConfig (canonical order matches ISimulationConfig) ---

        [KlothoOrder] public int TickIntervalMs;
        [KlothoOrder] public int MaxEntities;
        [KlothoOrder] public int CatchupMaxTicksPerFrame;
        [KlothoOrder] public int InputDelayTicks;
        [KlothoOrder] public int MaxRollbackTicks;
        [KlothoOrder] public int SyncCheckInterval;
        [KlothoOrder] public int ResyncMaxRetries;
        [KlothoOrder] public int DesyncThresholdForResync;
        [KlothoOrder] public int CorrectiveResetCooldownMs;
        [KlothoOrder] public bool UsePrediction;
        [KlothoOrder] public int Mode;
        [KlothoOrder] public int HardToleranceMs;
        [KlothoOrder] public int InputResendIntervalMs;
        [KlothoOrder] public int MaxUnackedInputs;
        [KlothoOrder] public int ServerSnapshotRetentionTicks;
        [KlothoOrder] public int SDInputLeadTicks;
        [KlothoOrder] public bool EnableErrorCorrection;
        [KlothoOrder] public int InterpolationDelayTicks;
        [KlothoOrder] public int QuorumMissDropTicks;
        [KlothoOrder] public int ReactiveWindowTicks;
        [KlothoOrder] public int ReactiveEscalateThreshold;
        [KlothoOrder] public int ReactiveStep;
        [KlothoOrder] public int ReactiveMax;
        [KlothoOrder] public int ServerPushGraceTicks;
        [KlothoOrder] public int ReactiveEscalateCooldownTicks;
        [KlothoOrder] public int ReactiveDeEscalateStableTicks;
        [KlothoOrder] public int RollbackBurstCount;
        [KlothoOrder] public int RollbackWindowTicks;
        [KlothoOrder] public int EventDispatchWarnMs;
        [KlothoOrder] public int TickDriftWarnMultiplier;
        [KlothoOrder] public int StageId;
        [KlothoOrder] public byte[] MatchConfigData;

        // --- SessionConfig (RandomSeed is excluded since it overlaps with the top-level field, and is back-synced in ToSessionConfig()) ---

        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public int MaxSpectators;
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        [KlothoOrder] public int LateJoinDelaySafety;
        [KlothoOrder] public int RttSanityMaxMs;
        [KlothoOrder] public int MinStallAbortTicks;
        [KlothoOrder] public int CountdownDurationMs;
        [KlothoOrder] public int AbortGraceMs;
        [KlothoOrder] public int EndGracePolicy;
        [KlothoOrder] public int EndGraceMs;
        [KlothoOrder] public int ClientShutdownGraceMs;

        // --- SimulationConfig layout inputs (appended after SessionConfig — parallel arrays, the same
        //     form as SimulationConfigMessage, so a spectator builds the room's layout) ---

        [KlothoOrder] public List<int> MaxCountOverrideTypeIds = new List<int>();
        [KlothoOrder] public List<int> MaxCountOverrideValues = new List<int>();
        [KlothoOrder] public List<int> PrunedComponentTypeIds = new List<int>();

        /// <summary>
        /// Populate the SimulationConfig fields from an ISimulationConfig.
        /// </summary>
        public void CopySimulationConfigFrom(Core.ISimulationConfig config)
        {
            TickIntervalMs = config.TickIntervalMs;
            MaxEntities = config.MaxEntities;
            CatchupMaxTicksPerFrame = config.CatchupMaxTicksPerFrame;
            InputDelayTicks = config.InputDelayTicks;
            MaxRollbackTicks = config.MaxRollbackTicks;
            SyncCheckInterval = config.SyncCheckInterval;
            ResyncMaxRetries = config.ResyncMaxRetries;
            DesyncThresholdForResync = config.DesyncThresholdForResync;
            CorrectiveResetCooldownMs = config.CorrectiveResetCooldownMs;
            UsePrediction = config.UsePrediction;
            Mode = (int)config.Mode;
            HardToleranceMs = config.HardToleranceMs;
            InputResendIntervalMs = config.InputResendIntervalMs;
            MaxUnackedInputs = config.MaxUnackedInputs;
            ServerSnapshotRetentionTicks = config.ServerSnapshotRetentionTicks;
            SDInputLeadTicks = config.SDInputLeadTicks;
            EnableErrorCorrection = config.EnableErrorCorrection;
            InterpolationDelayTicks = config.InterpolationDelayTicks;
            QuorumMissDropTicks = config.QuorumMissDropTicks;
            ReactiveWindowTicks = config.ReactiveWindowTicks;
            ReactiveEscalateThreshold = config.ReactiveEscalateThreshold;
            ReactiveStep = config.ReactiveStep;
            ReactiveMax = config.ReactiveMax;
            ServerPushGraceTicks = config.ServerPushGraceTicks;
            ReactiveEscalateCooldownTicks = config.ReactiveEscalateCooldownTicks;
            ReactiveDeEscalateStableTicks = config.ReactiveDeEscalateStableTicks;
            RollbackBurstCount = config.RollbackBurstCount;
            RollbackWindowTicks = config.RollbackWindowTicks;
            EventDispatchWarnMs = config.EventDispatchWarnMs;
            TickDriftWarnMultiplier = config.TickDriftWarnMultiplier;
            StageId = config.StageId;
            MatchConfigData = config.MatchConfigData;

            MaxCountOverrideTypeIds.Clear();
            MaxCountOverrideValues.Clear();
            if (config.ComponentMaxCountOverrides != null)
            {
                foreach (var kv in config.ComponentMaxCountOverrides)
                {
                    MaxCountOverrideTypeIds.Add(kv.Key);
                    MaxCountOverrideValues.Add(kv.Value);
                }
            }

            PrunedComponentTypeIds.Clear();
            if (config.PrunedComponentTypeIds != null)
                foreach (var id in config.PrunedComponentTypeIds)
                    PrunedComponentTypeIds.Add(id);
        }

        /// <summary>
        /// Build a SimulationConfig from the message fields.
        /// </summary>
        public Core.SimulationConfig ToSimulationConfig()
        {
            var config = new Core.SimulationConfig
            {
                TickIntervalMs = TickIntervalMs,
                MaxEntities = MaxEntities,
                CatchupMaxTicksPerFrame = CatchupMaxTicksPerFrame,
                InputDelayTicks = InputDelayTicks,
                MaxRollbackTicks = MaxRollbackTicks,
                SyncCheckInterval = SyncCheckInterval,
                ResyncMaxRetries = ResyncMaxRetries,
                DesyncThresholdForResync = DesyncThresholdForResync,
                CorrectiveResetCooldownMs = CorrectiveResetCooldownMs,
                UsePrediction = UsePrediction,
                Mode = (Core.NetworkMode)Mode,
                HardToleranceMs = HardToleranceMs,
                InputResendIntervalMs = InputResendIntervalMs,
                MaxUnackedInputs = MaxUnackedInputs,
                ServerSnapshotRetentionTicks = ServerSnapshotRetentionTicks,
                SDInputLeadTicks = SDInputLeadTicks,
                EnableErrorCorrection = EnableErrorCorrection,
                InterpolationDelayTicks = InterpolationDelayTicks,
                QuorumMissDropTicks = QuorumMissDropTicks,
                ReactiveWindowTicks = ReactiveWindowTicks,
                ReactiveEscalateThreshold = ReactiveEscalateThreshold,
                ReactiveStep = ReactiveStep,
                ReactiveMax = ReactiveMax,
                ServerPushGraceTicks = ServerPushGraceTicks,
                ReactiveEscalateCooldownTicks = ReactiveEscalateCooldownTicks,
                ReactiveDeEscalateStableTicks = ReactiveDeEscalateStableTicks,
                RollbackBurstCount = RollbackBurstCount,
                RollbackWindowTicks = RollbackWindowTicks,
                EventDispatchWarnMs = EventDispatchWarnMs,
                TickDriftWarnMultiplier = TickDriftWarnMultiplier,
                StageId = StageId,
                MatchConfigData = MatchConfigData,
            };

            int n = System.Math.Min(MaxCountOverrideTypeIds?.Count ?? 0, MaxCountOverrideValues?.Count ?? 0);
            for (int i = 0; i < n; i++)
                config.ComponentMaxCountOverrides[MaxCountOverrideTypeIds[i]] = MaxCountOverrideValues[i];

            if (PrunedComponentTypeIds != null)
                config.PrunedComponentTypeIds.AddRange(PrunedComponentTypeIds);

            return config;
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
            MinPlayers = config.MinPlayers;
            MaxSpectators = config.MaxSpectators;
            AllowLateJoin = config.AllowLateJoin;
            LateJoinDelayTicks = config.LateJoinDelayTicks;
            ReconnectTimeoutMs = config.ReconnectTimeoutMs;
            ReconnectMaxRetries = config.ReconnectMaxRetries;
            LateJoinDelaySafety = config.LateJoinDelaySafety;
            RttSanityMaxMs = config.RttSanityMaxMs;
            MinStallAbortTicks = config.MinStallAbortTicks;
            CountdownDurationMs = config.CountdownDurationMs;
            AbortGraceMs = config.AbortGraceMs;
            EndGracePolicy = (int)config.EndGracePolicy;
            EndGraceMs = config.EndGraceMs;
            ClientShutdownGraceMs = config.ClientShutdownGraceMs;
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
                MinPlayers = MinPlayers,
                MaxSpectators = MaxSpectators,
                AllowLateJoin = AllowLateJoin,
                LateJoinDelayTicks = LateJoinDelayTicks,
                ReconnectTimeoutMs = ReconnectTimeoutMs,
                ReconnectMaxRetries = ReconnectMaxRetries,
                LateJoinDelaySafety = LateJoinDelaySafety,
                RttSanityMaxMs = RttSanityMaxMs,
                MinStallAbortTicks = MinStallAbortTicks,
                CountdownDurationMs = CountdownDurationMs,
                AbortGraceMs = AbortGraceMs,
                EndGracePolicy = (Core.EndGracePolicy)EndGracePolicy,
                EndGraceMs = EndGraceMs,
                ClientShutdownGraceMs = ClientShutdownGraceMs,
            };
        }
    }
}
