using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(111)]
    public partial class SkillActionEvent : SimulationEvent   // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Caster;
        [KlothoOrder] public int ClassIndex;       // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
        [KlothoOrder] public int SkillSlot;        // 0 or 1
        [KlothoOrder] public FPVector2 CasterPosition;
        [KlothoOrder] public FPVector2 AimDirection;
        [KlothoOrder] public FPVector2 TargetPosition;
    }
}
