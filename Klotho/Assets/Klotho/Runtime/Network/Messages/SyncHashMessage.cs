using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message that carries the simulation hash for a specific tick to verify state synchronization.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncHash)]
    public partial class SyncHashMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public long Hash;

        [KlothoOrder]
        public int PlayerId;
    }
}
