using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoDataAsset(106)]
    public partial class CombatPhysicsAsset : IDataAsset
    {
        public int AssetId { get; }

        [KlothoOrder(0)]  public int DefaultKnockbackDurationTicks = 20;
        [KlothoOrder(1)]  public int HitReactionDurationTicks = 10;
        [KlothoOrder(2)]  public int PushDurationTicks        = 10;
        [KlothoOrder(3)]  public FP64 BodyRadiusSqr = FP64.One;
        [KlothoOrder(4)]  public int  ContactPower  = 5;
        [KlothoOrder(5)]  public int  ShieldDurationTicks = 100;
        [KlothoOrder(6)]  public int  BoostDurationTicks  = 100;
        [KlothoOrder(7)]  public FP64 BombRadiusSqr       = FP64.FromInt(9);
        [KlothoOrder(8)]  public int  BombBasePower       = 15;
        [KlothoOrder(9)]  public FP64 BombImpulse         = FP64.FromInt(15);

        public CombatPhysicsAsset(int assetId)
        {
            AssetId = assetId;
        }
    }
}
