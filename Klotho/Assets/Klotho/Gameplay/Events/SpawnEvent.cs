using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Spawn event — Regular
    /// </summary>
    [KlothoSerializable(2)]
    public partial class SpawnEvent : SimulationEvent
    {
        [KlothoOrder]
        public int EntityId;

        [KlothoOrder]
        public int EntityTypeId;

        [KlothoOrder]
        public int OwnerId;
    }
}
