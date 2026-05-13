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
        [field: SerializeField] public int InputDelayTicks { get; set; } = 4;
        [field: SerializeField] public int MaxRollbackTicks { get; set; } = 50;
        [field: SerializeField] public int SyncCheckInterval { get; set; } = 30;
        [field: SerializeField] public bool UsePrediction { get; set; } = true;
        [field: SerializeField] public int MaxEntities { get; set; } = 256;
        [field: SerializeField] public NetworkMode Mode { get; set; } = NetworkMode.P2P;

        [Header("ServerDriven")]
        [field: SerializeField] public int HardToleranceMs { get; set; } = 0;
        [field: SerializeField] public int InputResendIntervalMs { get; set; } = 50;
        [field: SerializeField] public int MaxUnackedInputs { get; set; } = 30;
        [field: SerializeField] public int ServerSnapshotRetentionTicks { get; set; } = 0;
        [field: SerializeField] public int SDInputLeadTicks { get; set; } = 0;

        [Header("ErrorCorrection")]
        [field: SerializeField] public bool EnableErrorCorrection { get; set; } = false;

        [Header("View Interpolation")]
        [field: SerializeField, Range(1, 3)] public int InterpolationDelayTicks { get; set; } = 3;

        [Header("LateJoin/Reconnect")]
        [field: SerializeField] public int LateJoinDelaySafety { get; set; } = 2;
        [field: SerializeField] public int RttSanityMaxMs { get; set; } = 240;
        [field: SerializeField] public int QuorumMissDropTicks { get; set; } = 20;

        [Header("Diagnostics")]
        [field: SerializeField] public int EventDispatchWarnMs { get; set; } = 5;
        [field: SerializeField] public int TickDriftWarnMultiplier { get; set; } = 2;
    }
}
