using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(105)]
    public partial class RoundTimerEvent : SimulationEvent  // EventMode.Synced
    {
        public override EventMode Mode => EventMode.Synced;
        [KlothoOrder] public int RemainingSeconds;
    }
}
