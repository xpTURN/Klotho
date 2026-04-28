using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(104)]
    public partial class BotDifficultyAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public int  DecisionCooldown;
        [KlothoOrder(1)] public int  AttackCooldownBase;
        [KlothoOrder(2)] public FP64 EvadeMargin;
        [KlothoOrder(3)] public int  EvadeKnockbackPct;
        [KlothoOrder(4)] public int  SkillExtraDelay;

        public BotDifficultyAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
