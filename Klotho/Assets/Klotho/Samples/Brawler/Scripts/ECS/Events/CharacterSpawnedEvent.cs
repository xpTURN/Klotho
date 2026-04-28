using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(108)]
    public partial class CharacterSpawnedEvent : SimulationEvent  // EventMode.Regular
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int CharacterClass;
    }
}
