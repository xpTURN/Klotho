using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(100)]
    public partial class CharacterStatsAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public int  PrototypeId;
        [KlothoOrder(1)] public FP64 MoveSpeed;
        [KlothoOrder(2)] public FP64 Mass                = FP64.One;
        [KlothoOrder(3)] public FP64 Friction            = FP64.FromDouble(0.5);
        [KlothoOrder(4)] public FP64 ColliderRadius      = FP64.FromDouble(0.5);
        [KlothoOrder(5)] public FP64 ColliderHalfHeight  = FP64.FromDouble(0.5);
        [KlothoOrder(6)] public FP64 ColliderOffsetY     = FP64.One;
        [KlothoOrder(7)] public int Skill0Id;
        [KlothoOrder(8)] public int Skill1Id;

        public CharacterStatsAsset(int assetId)
        {
            AssetId     = assetId;
        }
    }
}
