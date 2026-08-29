using System.Collections.Generic;
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
        [field: SerializeField, Range(1, 4)] public int InterpolationDelayTicks { get; set; } = 3;

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
        // Component-memory peak sampler gate (dev/measurement only). Default off (opt-in), distinct from
        // DiagnosticHistoryTicks. Off ⇒ no subscription, zero cost. Per-peer local (not wire-propagated).
        [field: SerializeField] public bool ComponentMemoryPeakSampling { get; set; } = false;
        // Per-system perf monitor gate (dev/measurement only). Default off (opt-in). Off ⇒ no
        // Stopwatch/GC calls, zero cost. Per-peer local (not wire-propagated). Dumped at engine Stop.
        [field: SerializeField] public bool SystemPerfMonitoring { get; set; } = false;

        // Multi-stage. StageId authorable (default single stage);
        // MatchConfigData is runtime-only (set by lobby/host at match start), not inspector-authored.
        [field: Header("Multi-stage")]
        [field: SerializeField] public int StageId { get; set; } = 0;
        public byte[] MatchConfigData { get; set; } = null;

        // Component storage. Unity can't serialize Dictionary → author as a pair list.
        // Process-global: must be identical across every session in the process (see ISimulationConfig).
        [field: Header("Component storage (maxCount overrides)")]
        [SerializeField] private ComponentMaxCountEntry[] _componentMaxCountOverrides = System.Array.Empty<ComponentMaxCountEntry>();

        public IReadOnlyDictionary<int, int> ComponentMaxCountOverrides
        {
            get
            {
                var d = new Dictionary<int, int>();
                if (_componentMaxCountOverrides != null)
                    foreach (var e in _componentMaxCountOverrides)
                        if (e.MaxCount > 0) d[e.TypeId] = e.MaxCount;
                return d;
            }
        }

        // Reservation-pruning denylist: component typeIds this session skips reserving. Empty = no
        // pruning (reserve all). Process-global; carried on the same wire (SimulationConfigMessage).
        [field: Header("Component storage (denylist / reservation pruning)")]
        [SerializeField] private int[] _prunedComponentTypeIds = System.Array.Empty<int>();

        // Runtime-only override (NOT serialized) — lets the P2P host inject a code-resolved denylist
        // without mutating the .asset (Editor play mode would persist a serialized-field write, corrupting
        // the saved setting — same reason MatchConfigData is runtime-only). Non-null wins over the array.
        [System.NonSerialized] private int[] _runtimePrunedComponentTypeIds;

        public void SetRuntimePrunedComponentTypeIds(int[] typeIds) => _runtimePrunedComponentTypeIds = typeIds;

        public IReadOnlyCollection<int> PrunedComponentTypeIds => _runtimePrunedComponentTypeIds ?? _prunedComponentTypeIds;

        [System.Serializable]
        public struct ComponentMaxCountEntry
        {
            public int TypeId;
            public int MaxCount;
        }
    }
}
