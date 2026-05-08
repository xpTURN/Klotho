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

        // Server-driven
        ClientInput = 80,
        VerifiedState = 81,
        InputAck = 82,
        ClientInputBundle = 83,
        PlayerBootstrapReady = 84,
        BootstrapBegin = 85,
        CommandRejected = 86,

        // Config layer
        SimulationConfig = 90,
        PlayerConfig = 91,

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
