using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(110)]
    public partial class AttackActionEvent : SimulationEvent   // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Attacker;
        [KlothoOrder] public FPVector2 AttackerPosition;
        [KlothoOrder] public FPVector2 AimDirection;
    }
}
