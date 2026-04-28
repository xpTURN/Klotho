using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Player ready message
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerReady)]
    public partial class PlayerReadyMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public bool IsReady;
    }
}
