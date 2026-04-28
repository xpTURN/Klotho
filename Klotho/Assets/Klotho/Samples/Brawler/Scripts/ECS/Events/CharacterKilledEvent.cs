using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(103)]
    public partial class CharacterKilledEvent : SimulationEvent  // EventMode.Synced
    {
        public override EventMode Mode => EventMode.Synced;
        [KlothoOrder] public EntityRef Character;
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int StockRemaining;
    }
}
