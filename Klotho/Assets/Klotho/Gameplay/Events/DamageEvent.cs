using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Damage event — Regular (confirmed/cancelled after prediction)
    /// </summary>
    [KlothoSerializable(1)]
    public partial class DamageEvent : SimulationEvent
    {
        [KlothoOrder]
        public int SourceEntityId;

        [KlothoOrder]
        public int TargetEntityId;

        [KlothoOrder]
        public int DamageAmount;
    }
}
