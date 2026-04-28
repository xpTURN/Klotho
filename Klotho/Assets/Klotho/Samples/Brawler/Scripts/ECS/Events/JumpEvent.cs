using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(106)]
    public partial class JumpEvent : SimulationEvent        // EventMode.Regular
    {
        [KlothoOrder] public EntityRef Character;
    }
}
