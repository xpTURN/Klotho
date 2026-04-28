using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(102)]
    public partial class UseSkillCommand : CommandBase
    {
        [KlothoOrder(0)] public int SkillSlot;        // 0 or 1
        [KlothoOrder(1)] public FPVector2 AimDirection;
    }
}
