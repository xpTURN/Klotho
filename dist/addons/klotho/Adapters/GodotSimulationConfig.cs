// SimulationConfig authored from the Godot editor (Resource implementing ISimulationConfig).
// Implements ISimulationConfig so it can be injected into KlothoFlowSetup / StartHostAndListen directly.
// [GlobalClass] surfaces it in the editor's "New Resource" menu; [Export] fields persist to .tres.
// Default [Export] values mirror the engine's built-in simulation config.
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Godot
{
    [GlobalClass]
    public partial class GodotSimulationConfig : Resource, ISimulationConfig
    {
        [Export] public int TickIntervalMs { get; set; } = 25;
        [Export] public int MaxEntities { get; set; } = 256;
        [Export] public int CatchupMaxTicksPerFrame { get; set; } = 200;
        [Export] public int InputDelayTicks { get; set; } = 4;
        [Export] public int MaxRollbackTicks { get; set; } = 50;
        [Export] public int SyncCheckInterval { get; set; } = 20;
        [Export] public int ResyncMaxRetries { get; set; } = 3;
        [Export] public int DesyncThresholdForResync { get; set; } = 3;
        [Export] public int CorrectiveResetCooldownMs { get; set; } = 5000;
        [Export] public int CorrectiveResetMaxAttempts { get; set; } = 2;
        [Export] public bool AutoAbortOnRecoveryExhausted { get; set; } = true;
        [Export] public bool UsePrediction { get; set; } = true;
        [Export] public NetworkMode Mode { get; set; } = NetworkMode.P2P;

        // ServerDriven
        [Export] public int HardToleranceMs { get; set; } = 0;
        [Export] public int InputResendIntervalMs { get; set; } = 50;
        [Export] public int MaxUnackedInputs { get; set; } = 30;
        [Export] public int ServerSnapshotRetentionTicks { get; set; } = 0;
        [Export] public int SDInputLeadTicks { get; set; } = 0;

        // ErrorCorrection
        [Export] public bool EnableErrorCorrection { get; set; } = false;

        // View Interpolation
        [Export(PropertyHint.Range, "1,3")] public int InterpolationDelayTicks { get; set; } = 3;

        // P2P Quorum-Miss Watchdog
        [Export] public int QuorumMissDropTicks { get; set; } = 20;

        // Reactive Dynamic InputDelay
        [Export] public int ReactiveWindowTicks { get; set; } = 80;
        [Export] public int ReactiveEscalateThreshold { get; set; } = 3;
        [Export] public int ReactiveStep { get; set; } = 4;
        [Export] public int ReactiveMax { get; set; } = 40;
        [Export] public int ServerPushGraceTicks { get; set; } = 40;
        [Export] public int ReactiveEscalateCooldownTicks { get; set; } = 80;
        [Export] public int ReactiveDeEscalateStableTicks { get; set; } = 160;

        // Rollback Burst
        [Export] public int RollbackBurstCount { get; set; } = 3;
        [Export] public int RollbackWindowTicks { get; set; } = 200;

        // Diagnostics
        [Export] public int EventDispatchWarnMs { get; set; } = 5;
        [Export] public int TickDriftWarnMultiplier { get; set; } = 2;
        // Desync-diagnostic hash history. Default 60 (≈ rollback window + RTT margin) accumulates a
        // per-tick layered hash breakdown for local desync localization; 0 disables. Diagnostic-only, not
        // wire-propagated. Carries a P2P per-tick hash cost — set 0 to drop it in a shipped build.
        [Export] public int DiagnosticHistoryTicks { get; set; } = 60;

        // Multi-stage. StageId authorable (default single stage);
        // MatchConfigData is runtime-only (set by lobby/host at match start), not [Export]-authored.
        [Export] public int StageId { get; set; } = 0;
        public byte[] MatchConfigData { get; set; } = null;
    }
}
