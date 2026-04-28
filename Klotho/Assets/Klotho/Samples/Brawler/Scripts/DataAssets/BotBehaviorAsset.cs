using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(103)]
    public partial class BotBehaviorAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public FP64        StageBoundary              = FP64.FromInt(18);
        [KlothoOrder(1)] public FP64        ChaseStopDistance           = FP64.FromDouble(1.5);
        [KlothoOrder(2)] public FP64        NavSnapMaxDist              = FP64.FromInt(3);
        [KlothoOrder(3)] public int         EvadeCooldownTicks          = 300;
        [KlothoOrder(4)] public FP64        EyeHeight                   = FP64.FromDouble(1.5);
        [KlothoOrder(5)] public FP64        TargetScoreKnockbackFactor  = FP64.FromInt(10);
        [KlothoOrder(6)] public FP64        TargetScoreStockFactor      = FP64.FromInt(100);
        [KlothoOrder(7)] public FPVector3[] EvadePoints                 =
        {
            new FPVector3(-5f, 0f, -5f),
            new FPVector3( 5f, 0f, -5f),
            new FPVector3(-5f, 0f,  5f),
            new FPVector3( 5f, 0f,  5f),
            new FPVector3( 0f, 0f, -5f),
            new FPVector3( 0f, 0f,  5f),
            new FPVector3(-5f, 0f,  0f),
            new FPVector3( 5f, 0f,  0f),
        };

        public BotBehaviorAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
