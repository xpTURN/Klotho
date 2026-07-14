using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho
{
    /// <summary>
    /// SimulationConfig editable from the Unity Inspector.
    /// Used as a ScriptableObject or MonoBehaviour field on the host side.
    /// </summary>
    [CreateAssetMenu(menuName = "Klotho/SimulationConfig", fileName = "SimulationConfig")]
    public class USimulationConfig : ScriptableObject, ISimulationConfig
    {
        [field: SerializeField] public int TickIntervalMs { get; set; } = 25;
        [field: SerializeField] public int MaxEntities { get; set; } = 256;
        [field: SerializeField] public int CatchupMaxTicksPerFrame { get; set; } = 200;
        [field: SerializeField] public int InputDelayTicks { get; set; } = 4;
        [field: SerializeField] public int MaxRollbackTicks { get; set; } = 50;
        [field: SerializeField] public int SyncCheckInterval { get; set; } = 20;
        [field: SerializeField] public int ResyncMaxRetries { get; set; } = 3;
        [field: SerializeField] public int DesyncThresholdForResync { get; set; } = 3;
        [field: SerializeField] public int CorrectiveResetCooldownMs { get; set; } = 5000;
        [field: SerializeField] public int CorrectiveResetMaxAttempts { get; set; } = 2;
        [field: SerializeField] public bool AutoAbortOnRecoveryExhausted { get; set; } = true;
        [field: SerializeField] public bool UsePrediction { get; set; } = true;
        [field: SerializeField] public NetworkMode Mode { get; set; } = NetworkMode.P2P;

        [field: Header("ServerDriven")]
        [field: SerializeField] public int HardToleranceMs { get; set; } = 0;
        [field: SerializeField] public int InputResendIntervalMs { get; set; } = 50;
        [field: SerializeField] public int MaxUnackedInputs { get; set; } = 30;
        [field: SerializeField] public int ServerSnapshotRetentionTicks { get; set; } = 0;
        [field: SerializeField] public int SDInputLeadTicks { get; set; } = 0;

        [field: Header("ErrorCorrection")]
        [field: SerializeField] public bool EnableErrorCorrection { get; set; } = false;

        [field: Header("View Interpolation")]
        [field: SerializeField, Range(1, 3)] public int InterpolationDelayTicks { get; set; } = 3;

        [field: Header("P2P Quorum-Miss Watchdog")]
        [field: SerializeField] public int QuorumMissDropTicks { get; set; } = 20;

        [field: Header("Reactive Dynamic InputDelay")]
        [field: SerializeField] public int ReactiveWindowTicks { get; set; } = 80;
        [field: SerializeField] public int ReactiveEscalateThreshold { get; set; } = 3;
        [field: SerializeField] public int ReactiveStep { get; set; } = 4;
        [field: SerializeField] public int ReactiveMax { get; set; } = 40;
        [field: SerializeField] public int ServerPushGraceTicks { get; set; } = 40;
        [field: SerializeField] public int ReactiveEscalateCooldownTicks { get; set; } = 80;
        [field: SerializeField] public int ReactiveDeEscalateStableTicks { get; set; } = 160;

        [field: Header("Rollback Burst")]
        [field: SerializeField] public int RollbackBurstCount { get; set; } = 3;
        [field: SerializeField] public int RollbackWindowTicks { get; set; } = 200;

        [field: Header("Diagnostics")]
        [field: SerializeField] public int EventDispatchWarnMs { get; set; } = 5;
        [field: SerializeField] public int TickDriftWarnMultiplier { get; set; } = 2;
        // Desync-diagnostic hash history. Default 60 (≈ rollback window + RTT margin) accumulates a
        // per-tick layered hash breakdown for local desync localization; 0 disables. Diagnostic-only, not
        // wire-propagated. Carries a P2P per-tick hash cost — set 0 to drop it in a shipped player build.
        [field: SerializeField] public int DiagnosticHistoryTicks { get; set; } = 60;

        // Multi-stage. StageId authorable (default single stage);
        // MatchConfigData is runtime-only (set by lobby/host at match start), not inspector-authored.
        [field: Header("Multi-stage")]
        [field: SerializeField] public int StageId { get; set; } = 0;
        public byte[] MatchConfigData { get; set; } = null;
    }
}
