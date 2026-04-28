using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(101)]
    public partial class SkillConfigAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public int  Cooldown;
        [KlothoOrder(1)] public int  ActionLockTicks;
        [KlothoOrder(2)] public FP64 MoveSpeedOrRange;
        [KlothoOrder(3)] public FP64 RangeSqr;
        [KlothoOrder(4)] public int  KnockbackPower;
        [KlothoOrder(5)] public FP64 EffectRadius;
        [KlothoOrder(6)] public int  AuxDurationTicks;
        [KlothoOrder(7)] public FP64 ImpactOffsetDist;

        public SkillConfigAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
