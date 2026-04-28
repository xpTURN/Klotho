using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(107)]
    public partial class GroundSlamEvent : SimulationEvent  // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Character;
        [KlothoOrder] public FPVector2 Position;
        [KlothoOrder] public FP64 Radius;
    }
}
