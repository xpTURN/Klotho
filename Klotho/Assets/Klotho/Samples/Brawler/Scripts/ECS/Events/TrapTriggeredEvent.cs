using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(109)]
    public partial class TrapTriggeredEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder] public EntityRef Character;
        [KlothoOrder] public FPVector2 TrapPosition;
    }
}
