using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server payload collected by KlothoConnection on the cold-start Reconnect path.
    /// Valid only when ConnectionResult.Kind == JoinKind.Reconnect.
    /// Mirrors LateJoinPayload — AcceptMessage carries SessionConfig + PlayerIds, FullState is held separately
    /// for engine seeding.
    /// </summary>
    public class ReconnectPayload
    {
        /// <summary>
        /// The original ReconnectAcceptMessage sent by the host — contains SessionConfig block / RandomSeed /
        /// PlayerIds / PlayerConnectionStates / SharedClock data.
        /// </summary>
        public ReconnectAcceptMessage AcceptMessage { get; set; }

        public int FullStateTick { get; set; }
        public byte[] FullStateData { get; set; }
        public long FullStateHash { get; set; }
    }
}
