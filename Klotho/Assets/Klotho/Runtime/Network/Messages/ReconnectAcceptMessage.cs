using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectAccept)]
    public partial class ReconnectAcceptMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int CurrentTick;
        [KlothoOrder] public long SharedEpoch;
        [KlothoOrder] public long ClockOffset;
        [KlothoOrder] public int PlayerCount;
        [KlothoOrder] public List<int> PlayerIds = new List<int>();
        [KlothoOrder] public List<byte> PlayerConnectionStates = new List<byte>();

        // --- SessionConfig fields (same layout as LateJoinAcceptMessage) ---

        [KlothoOrder] public int RandomSeed;
        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ResyncMaxRetries;
        [KlothoOrder] public int DesyncThresholdForResync;
        [KlothoOrder] public int CountdownDurationMs;
        [KlothoOrder] public int CatchupMaxTicksPerFrame;
    }
}
