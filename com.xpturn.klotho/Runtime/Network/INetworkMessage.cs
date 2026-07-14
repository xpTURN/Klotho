using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Network message type
    /// </summary>
    public enum NetworkMessageType : byte
    {
        // Multi-room
        RoomHandshake = 1,

        // Lobby
        JoinRoom = 10,
        LeaveRoom = 11,
        PlayerReady = 12,
        GameStart = 13,
        PlayerJoin = 14,
        JoinReject = 15,

        // Gameplay
        Command = 20,
        CommandAck = 21,
        CommandRequest = 22,
        
        // Reliable command submit — client/guest → authority (server-driven server / P2P host),
        // reliably-ordered. Carries a serialized IReliableCommand with no client-assigned tick; the
        // authority assigns the execution tick. (CommandAck=21 / CommandRequest=22 are unused reserved
        // slots; a new append-only value is used to avoid repurposing their declared intent.)
        ReliableCommandSubmit = 23,

        // Sync
        SyncHash = 30,
        SyncHashAck = 31,
        FullState = 32,
        FullStateRequest = 33,

        // Connection
        Ping = 40,
        Pong = 41,
        Disconnect = 42,

        // Handshake
        SyncRequest = 50,
        SyncReply = 51,
        SyncComplete = 52,

        // Spectator
        SpectatorJoin = 60,
        SpectatorAccept = 61,
        SpectatorInput = 62,
        SpectatorLeave = 63,

        // Reconnect
        ReconnectRequest = 70,
        ReconnectAccept = 71,
        ReconnectReject = 72,

        // Late join
        LateJoinAccept = 73,

        // Dynamic InputDelay — server→client push when smoothed RTT change crosses asymmetric threshold.
        RecommendedExtraDelayUpdate = 74,

        // Dynamic InputDelay — client→server/host report of the client's effective
        // extra-delay (baseline + reactive) so the server/host folds the client's reactive correction
        // into its authoritative baseline. SD: client→server. P2P: guest→host (star topology, peerId 0).
        ReactiveExtraDelayReport = 92,

        // Late join notification — host (P2P) / server (SD) broadcast to existing peers on new player completion.
        LateJoinNotification = 75,

        // Recovery ladder rungs 3-4: guest→host resync-failure report and
        // host→guests abort broadcast when corrective resets are exhausted.
        ResyncFailureReport = 76,
        MatchAbort = 77,

        // Player state change: host (P2P) → existing guests on a confirmed
        // disconnect / reconnect / leave, so guests exclude a departed peer from the timing vote.
        PlayerStateNotification = 78,

        // Pre-game (lobby) roster notifications — host (P2P) / server (SD) → existing peers/clients
        // when a player completes the normal-join handshake or leaves the lobby, so every peer's
        // _players stays consistent before StartGame (in-game leave uses PlayerStateNotification).
        PlayerJoinNotification = 79,

        // Server-driven
        ClientInput = 80,
        VerifiedState = 81,
        InputAck = 82,
        ClientInputBundle = 83,
        PlayerBootstrapReady = 84,
        BootstrapBegin = 85,
        CommandRejected = 86,
        PlayerLeaveNotification = 87,

        // Config layer
        SimulationConfig = 90,
        PlayerConfig = 91,

        // Desync diagnostics (online probe). Request/Response are a diagnostic round trip between
        // the peer that detected a desync and the peer it disagrees with; VerdictReport carries the
        // SD client's diff conclusion to the server for logging. All three are diagnostic-only:
        // nothing on these paths feeds the recovery ladder or rewinds a live simulation.
        DesyncProbeRequest = 93,
        DesyncProbeResponse = 94,
        DesyncVerdictReport = 95,

        // Sample/game-specific reserved range — instead of adding sample-specific values to the Runtime enum,
        // samples freely cast values past this point for their own use (avoids reversed dependency direction)
        UserDefined_Start = 200,
    }

    /// <summary>
    /// Network message interface
    /// </summary>
    public interface INetworkMessage
    {
        /// <summary>
        /// Message type
        /// </summary>
        NetworkMessageType MessageTypeId { get; }

        /// <summary>
        /// Serialize via SpanWriter (cross-platform, GC-free)
        /// </summary>
        void Serialize(ref SpanWriter writer);

        /// <summary>
        /// Deserialize via SpanReader (cross-platform, GC-free)
        /// </summary>
        void Deserialize(ref SpanReader reader);

        /// <summary>
        /// Maximum serialized size (bytes)
        /// </summary>
        int GetSerializedSize();
    }

    /// <summary>
    /// Message serialization utility interface
    /// </summary>
    public interface IMessageSerializer
    {
        /// <summary>
        /// Serialize message
        /// </summary>
        byte[] Serialize(INetworkMessage message);

        /// <summary>
        /// Deserialize message
        /// </summary>
        INetworkMessage Deserialize(byte[] data);

        /// <summary>
        /// Register message type
        /// </summary>
        void RegisterMessageType<T>(NetworkMessageType type) where T : INetworkMessage, new();
    }
}
