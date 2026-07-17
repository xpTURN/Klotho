using System.Collections.Generic;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISimulationConfig.
    /// Created inside KlothoSession.Create(),
    /// or constructed from the deserialized result of SimulationConfigMessage.
    /// </summary>
    public class SimulationConfig : ISimulationConfig
    {
        // --- Tick Loop ---

        /// <inheritdoc />
        public int TickIntervalMs { get; set; } = 25;

        /// <inheritdoc />
        public int MaxEntities { get; set; } = 256;

        /// <inheritdoc />
        public int CatchupMaxTicksPerFrame { get; set; } = 200;

        // --- Input ---

        /// <inheritdoc />
        public int InputDelayTicks { get; set; } = 4;

        // --- Rollback ---

        /// <inheritdoc />
        public int MaxRollbackTicks { get; set; } = 50;

        // --- Sync / Resync ---

        /// <inheritdoc />
        public int SyncCheckInterval { get; set; } = 20;

        /// <inheritdoc />
        public int ResyncMaxRetries { get; set; } = 3;

        /// <inheritdoc />
        public int DesyncThresholdForResync { get; set; } = 3;

        /// <inheritdoc />
        public int CorrectiveResetCooldownMs { get; set; } = 5000;

        /// <inheritdoc />
        public int CorrectiveResetMaxAttempts { get; set; } = 2;

        /// <inheritdoc />
        public bool AutoAbortOnRecoveryExhausted { get; set; } = true;

        // --- Prediction ---

        /// <inheritdoc />
        public bool UsePrediction { get; set; } = true;

        // --- Network Mode ---

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

        // --- Error Correction ---

        /// <inheritdoc />
        public bool EnableErrorCorrection { get; set; } = false;

        /// <inheritdoc />
        public int InterpolationDelayTicks { get; set; } = 3;

        // --- P2P Quorum-Miss Watchdog ---

        /// <inheritdoc />
        public int QuorumMissDropTicks { get; set; } = 20;

        // --- Reactive Dynamic InputDelay ---

        /// <inheritdoc />
        public int ReactiveWindowTicks { get; set; } = 80;

        /// <inheritdoc />
        public int ReactiveEscalateThreshold { get; set; } = 3;

        /// <inheritdoc />
        public int ReactiveStep { get; set; } = 4;

        /// <inheritdoc />
        public int ReactiveMax { get; set; } = 40;

        /// <inheritdoc />
        public int ServerPushGraceTicks { get; set; } = 40;

        /// <inheritdoc />
        public int ReactiveEscalateCooldownTicks { get; set; } = 80;

        /// <inheritdoc />
        public int ReactiveDeEscalateStableTicks { get; set; } = 160;

        // --- Rollback Burst ---

        /// <inheritdoc />
        public int RollbackBurstCount { get; set; } = 3;

        /// <inheritdoc />
        public int RollbackWindowTicks { get; set; } = 200;

        // --- Diagnostics ---

        /// <inheritdoc />
        public int EventDispatchWarnMs { get; set; } = 5;

        /// <inheritdoc />
        public int TickDriftWarnMultiplier { get; set; } = 2;

        /// <inheritdoc />
        public int DiagnosticHistoryTicks { get; set; } = 60;

        /// <inheritdoc />
        public bool ComponentMemoryPeakSampling { get; set; } = false;

        // --- Multi-stage ---

        /// <inheritdoc />
        public int StageId { get; set; } = 0;

        /// <inheritdoc />
        public byte[] MatchConfigData { get; set; } = null;

        // --- Component storage ---

        /// <summary>
        /// Per-component maxCount overrides (typeId → cap). JSON authoring: <c>{"11":12,"25":20}</c>.
        /// Exposed to the core as <see cref="ISimulationConfig.ComponentMaxCountOverrides"/> (IReadOnly).
        /// </summary>
        public Dictionary<int, int> ComponentMaxCountOverrides { get; set; } = new Dictionary<int, int>();

        IReadOnlyDictionary<int, int> ISimulationConfig.ComponentMaxCountOverrides => ComponentMaxCountOverrides;

        /// <summary>
        /// Reservation-pruning denylist. Empty = no pruning (reserve all registered types).
        /// Prefer the type-based builder <see cref="PruneComponent{T}"/> over hardcoding typeIds.
        /// </summary>
        public List<int> PrunedComponentTypeIds { get; set; } = new List<int>();

        IReadOnlyCollection<int> ISimulationConfig.PrunedComponentTypeIds => PrunedComponentTypeIds;

        /// <summary>
        /// Type-based denylist declaration — resolves T's typeId via the registry so the game
        /// never hardcodes a magic int. Engine <c>[KlothoCoreComponent]</c> types are force-excluded by the
        /// registry, so listing one is a no-op. List only types NO registered system touches. Call at setup
        /// (before EcsSimulation ctor).
        /// </summary>
        public SimulationConfig PruneComponent<T>() where T : unmanaged, xpTURN.Klotho.ECS.IComponent
        {
            int id = xpTURN.Klotho.ECS.ComponentStorageRegistry.GetTypeId<T>();
            if (!PrunedComponentTypeIds.Contains(id)) PrunedComponentTypeIds.Add(id);
            return this;
        }

        /// <inheritdoc />
        // Code-built config has no serialized backing, so the runtime injection simply replaces the list
        // (both the concrete List accessors and the ISimulationConfig getter then observe it).
        public void SetRuntimePrunedComponentTypeIds(int[] typeIds)
            => PrunedComponentTypeIds = typeIds != null ? new List<int>(typeIds) : new List<int>();

        /// <summary>
        /// Per-room shallow copy. Used when a per-room match config is stamped (StageId/MatchConfigData)
        /// so a shared config instance is not mutated across rooms with different stages. MatchConfigData
        /// is treated as immutable (opaque), so a reference copy is safe.
        /// </summary>
        public SimulationConfig Clone() => (SimulationConfig)MemberwiseClone();

        /// <summary>
        /// Enforces config invariants. Currently: ReactiveMax ≤ MaxRollbackTicks/2
        /// — the reactive escalation ceiling must not exceed the rollback-budget clamp, else the client
        /// reactive path could push effective extra-delay past the budget the engine clamps to.
        ///
        /// <para><paramref name="throwOnError"/>: authored/editor configs pass <c>true</c> (fail fast in
        /// development); wire-received configs (<see cref="Network.SimulationConfigMessage"/>) pass
        /// <c>false</c> so a malformed/hostile server config clamps+warns rather than crashing the client
        /// mid-handshake. The engine's RecommendedExtraDelay clamp is the runtime backstop regardless.</para>
        /// </summary>
        public void Validate(xpTURN.Klotho.Logging.IKLogger logger = null, bool throwOnError = false)
        {
            int clampMax = MaxRollbackTicks / 2;
            if (ReactiveMax > clampMax)
            {
                string msg = $"[SimulationConfig] ReactiveMax({ReactiveMax}) > MaxRollbackTicks/2({clampMax}) " +
                             "violates the rollback-budget invariant.";
                if (throwOnError)
                    throw new System.ArgumentOutOfRangeException(nameof(ReactiveMax), msg);
                logger?.KWarning($"{msg} Clamping ReactiveMax to {clampMax}.");
                ReactiveMax = clampMax;
            }
        }
    }
}
