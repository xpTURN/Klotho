using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message from the host notifying all clients of game start. Contains the random seed, tick interval, and participant list.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.GameStart)]
    public partial class GameStartMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public long StartTime; // Absolute game start time in SharedNow units

        [KlothoOrder]
        public List<int> PlayerIds = new List<int>();

        // --- SessionConfig fields ---

        [KlothoOrder]
        public int RandomSeed;

        [KlothoOrder]
        public int MaxPlayers;

        [KlothoOrder]
        public int MinPlayers;

        [KlothoOrder]
        public bool AllowLateJoin;

        [KlothoOrder]
        public int ReconnectTimeoutMs;

        [KlothoOrder]
        public int ReconnectMaxRetries;

        [KlothoOrder]
        public int LateJoinDelayTicks;

        [KlothoOrder]
        public int ResyncMaxRetries;

        [KlothoOrder]
        public int DesyncThresholdForResync;

        [KlothoOrder]
        public int CountdownDurationMs;

        [KlothoOrder]
        public int CatchupMaxTicksPerFrame;
    }
}
