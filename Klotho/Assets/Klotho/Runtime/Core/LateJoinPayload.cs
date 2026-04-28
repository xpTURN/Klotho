using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server payload collected by KlothoConnection on the Late Join path.
    /// Valid only when ConnectionResult.Kind == JoinKind.LateJoin.
    /// Consumed in KlothoSession.Create: SessionConfig / PlayerIds / PlayerConfigData etc. are
    /// extracted directly from AcceptMessage, while FullState is held in a separate tuple for engine seeding.
    /// </summary>
    public class LateJoinPayload
    {
        /// <summary>
        /// The original LateJoinAcceptMessage sent by the server — contains SessionConfig / PlayerIds /
        /// PlayerConnectionStates / PlayerConfigData / PlayerConfigLengths in full.
        /// The message reference is retained as-is to avoid copy cost (because
        /// MessageSerializer._messageCache reuses singletons by type, callers must ensure no message of
        /// the same type is received again before consumption is complete).
        /// </summary>
        public LateJoinAcceptMessage AcceptMessage { get; set; }

        public int FullStateTick { get; set; }
        public byte[] FullStateData { get; set; }
        public long FullStateHash { get; set; }
    }
}
