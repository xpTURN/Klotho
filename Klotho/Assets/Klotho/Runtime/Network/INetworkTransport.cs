using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Network transport layer abstraction interface.
    /// </summary>
    public interface INetworkTransport
    {
        /// <summary>
        /// Connection state
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Local peer ID
        /// </summary>
        int LocalPeerId { get; }

        /// <summary>
        /// Start listening as server/host.
        /// Determines IPv6 vs IPv4 usage based on the host IP.
        /// Returns true on socket bind/start success, false on immediate failure.
        /// </summary>
        bool Listen(string address, int port, int maxConnections);

        /// <summary>
        /// Connect to a server as a client/guest.
        /// Returns true on socket start success (actual connection establishment is reported via OnConnected/OnDisconnected),
        /// false on immediate failure.
        /// </summary>
        bool Connect(string address, int port);

        /// <summary>
        /// Disconnect
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Disconnect a specific peer
        /// </summary>
        void DisconnectPeer(int peerId);

        /// <summary>
        /// Send data to a specific peer
        /// </summary>
        void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Send data to a specific peer (length specified, for pooled buffers)
        /// </summary>
        void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Broadcast data to all peers
        /// </summary>
        void Broadcast(byte[] data, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Broadcast data to all peers (length specified, for pooled buffers)
        /// </summary>
        void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Process received packets (called per frame)
        /// </summary>
        void PollEvents();

        /// <summary>
        /// Flush queued outbound packets without processing inbound messages
        /// </summary>
        void FlushSendQueue();

        /// <summary>
        /// Data received event
        /// </summary>
        event Action<int, byte[], int> OnDataReceived; // peerId, data, length

        /// <summary>
        /// Peer connected event
        /// </summary>
        event Action<int> OnPeerConnected;

        /// <summary>
        /// Peer disconnected event
        /// </summary>
        event Action<int> OnPeerDisconnected;

        /// <summary>
        /// Connection established event
        /// </summary>
        event Action OnConnected;

        /// <summary>
        /// Disconnected event. The argument carries the categorized disconnect reason.
        /// </summary>
        event Action<DisconnectReason> OnDisconnected;

        /// <summary>
        /// Address of the last connected remote host. Retained after disconnection.
        /// </summary>
        string RemoteAddress { get; }

        /// <summary>
        /// Port of the last connected remote host. Retained after disconnection.
        /// </summary>
        int RemotePort { get; }
    }

    /// <summary>
    /// Data delivery method
    /// </summary>
    public enum DeliveryMethod
    {
        /// <summary>
        /// Unreliable transport (UDP)
        /// </summary>
        Unreliable,

        /// <summary>
        /// Reliable transport
        /// </summary>
        Reliable,

        /// <summary>
        /// Reliable and ordered transport
        /// </summary>
        ReliableOrdered,

        /// <summary>
        /// Ordered only
        /// </summary>
        Sequenced
    }
}
