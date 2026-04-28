using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(101)]
    public partial class DashEvent : SimulationEvent        // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Character;
        [KlothoOrder] public FPVector2 Direction;
    }
}
