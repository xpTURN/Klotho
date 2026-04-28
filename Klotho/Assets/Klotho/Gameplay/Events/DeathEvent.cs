using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Death event — Synced (dispatched only on Verified ticks)
    /// </summary>
    [KlothoSerializable(3)]
    public partial class DeathEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder]
        public int EntityId;

        [KlothoOrder]
        public int KillerEntityId;
    }
}
