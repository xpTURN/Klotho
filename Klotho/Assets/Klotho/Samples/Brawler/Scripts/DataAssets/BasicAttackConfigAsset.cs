using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(102)]
    public partial class BasicAttackConfigAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public FP64 MeleeRangeSqr  = FP64.FromInt(4);
        [KlothoOrder(1)] public int  BasePower      = 10;
        [KlothoOrder(2)] public int  ActionLockTicks = 15;
        [KlothoOrder(3)] public int  HitStunTicks   = 6;

        public BasicAttackConfigAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
