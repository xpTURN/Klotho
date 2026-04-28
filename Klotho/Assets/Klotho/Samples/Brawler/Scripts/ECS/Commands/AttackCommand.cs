using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(101)]
    public partial class AttackCommand : CommandBase
    {
        [KlothoOrder(0)] public FPVector2 AimDirection;  // Attack direction (XZ plane)
    }
}
