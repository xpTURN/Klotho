namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Result of KlothoConnection.Connect() / Reconnect().
    /// Encapsulates the completed state of the handshake + SimulationConfig reception.
    /// Injected into KlothoSessionSetup.Connection and handed off to NetworkService.
    /// </summary>
    public class ConnectionResult
    {
        /// <summary>
        /// The (already-connected) transport used for the handshake.
        /// </summary>
        public Network.INetworkTransport Transport { get; set; }

        /// <summary>
        /// SimulationConfig received from the host.
        /// </summary>
        public ISimulationConfig SimulationConfig { get; set; }

        /// <summary>
        /// Player ID assigned by the host (Normal / LateJoin) or echoed back unchanged (Reconnect).
        /// </summary>
        public int LocalPlayerId { get; set; }

        /// <summary>
        /// Session Magic. The SyncCompleteMessage.Magic / LateJoinAccept.Magic / ReconnectAccept (via creds) value
        /// is forwarded as-is.
        /// </summary>
        public int SessionMagic { get; set; }

        /// <summary>
        /// Clock synchronization — SharedClock epoch.
        /// </summary>
        public long SharedEpoch { get; set; }

        /// <summary>
        /// Clock synchronization — local-host offset.
        /// </summary>
        public long ClockOffset { get; set; }

        /// <summary>
        /// Which guest join path produced this result.
        /// </summary>
        public JoinKind Kind { get; set; } = JoinKind.Normal;

        /// <summary>
        /// Late Join-specific payload. Valid only when Kind == JoinKind.LateJoin.
        /// </summary>
        public LateJoinPayload LateJoinPayload { get; set; }

        /// <summary>
        /// Cold-start Reconnect-specific payload. Valid only when Kind == JoinKind.Reconnect.
        /// </summary>
        public ReconnectPayload ReconnectPayload { get; set; }
    }
}
