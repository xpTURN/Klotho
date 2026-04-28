using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(100)]
    public partial class AttackHitEvent : SimulationEvent   // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Attacker;
        [KlothoOrder] public EntityRef Target;
        [KlothoOrder] public int KnockbackAdded;
        [KlothoOrder] public FPVector2 HitPoint;
    }
}
