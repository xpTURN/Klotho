using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(102)]
    public partial class ItemPickedUpEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder] public EntityRef Character;
        [KlothoOrder] public int ItemType;
        [KlothoOrder] public FPVector2 ItemPosition;
    }
}
