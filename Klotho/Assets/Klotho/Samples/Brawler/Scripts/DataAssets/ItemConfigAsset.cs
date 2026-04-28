using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(107)]
    public partial class ItemConfigAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public int  SpawnIntervalTicks   = 200;
        [KlothoOrder(1)] public int  MaxItems             = 4;
        [KlothoOrder(2)] public int  ItemLifetimeTicks    = 600;
        [KlothoOrder(3)] public FP64 SpawnMinRange        = FP64.FromInt(4);
        [KlothoOrder(4)] public FP64 SpawnMaxRange        = FP64.FromInt(8);
        [KlothoOrder(5)] public FP64 BoostSpeedMultiplier = FP64.FromDouble(1.5);
        [KlothoOrder(6)] public FP64 PickupRadiusSqr      = FP64.FromDouble(0.4);

        public ItemConfigAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
