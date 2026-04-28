using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(105)]
    public partial class BrawlerGameRulesAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)] public int  GameDurationSeconds = 120;
        [KlothoOrder(1)] public FP64 StageBoundsSize     = FP64.FromInt(40);
        [KlothoOrder(2)] public FP64 FallDeathY          = FP64.FromInt(-10);
        [KlothoOrder(3)] public int  RespawnTicks        = 120;
        [KlothoOrder(4)] public FP64 CharacterSpawnY     = FP64.Zero;
        [KlothoOrder(5)] public FPVector3[] SpawnPositions =
        {
            new FPVector3(-4f, 0f, -4f),
            new FPVector3( 4f, 0f, -4f),
            new FPVector3(-4f, 0f,  4f),
            new FPVector3( 4f, 0f,  4f),
        };

        public BrawlerGameRulesAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
