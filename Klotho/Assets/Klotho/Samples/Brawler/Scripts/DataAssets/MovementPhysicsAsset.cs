using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(108)]
    public partial class MovementPhysicsAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public FP64 JumpSpeed        = FP64.FromInt(8);
        [KlothoOrder(1)] public FP64 GravityAccel     = FP64.FromInt(20);
        [KlothoOrder(2)] public FP64 MinMoveSqr       = FP64.FromDouble(0.0001);
        [KlothoOrder(3)] public FP64 SkinOffset       = FP64.FromDouble(0.3);
        [KlothoOrder(4)] public FP64 MaxFallProbe     = FP64.FromInt(5);
        [KlothoOrder(5)] public FP64 GroundEnterDepth = FP64.FromDouble(0.19);
        [KlothoOrder(6)] public FP64 GroundSnapDepth  = FP64.FromDouble(0.05);

        public MovementPhysicsAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
