using System;
using System.Collections.Generic;

using xpTURN.Klotho.Network; // INetworkTransport, DeliveryMethod, DisconnectReason

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Minimal in-memory <see cref="INetworkTransport"/> for headless ServerNetworkService tests.
    /// Most members are no-ops; the test drives peer lifecycle via <see cref="RaiseConnect"/>/
    /// <see cref="RaiseDisconnect"/> and inspects <see cref="Disconnected"/> to assert eviction.
    /// </summary>
    internal sealed class FakeTransport : INetworkTransport
    {
        public readonly List<int> Disconnected = new List<int>();

        public bool IsConnected => true;
        public int LocalPeerId => 0;
        public int LastDisconnectPayload => -1;
        public int RemotePort => 0;
        public string RemoteAddress => string.Empty;
        public IEnumerable<int> GetConnectedPeerIds() => Array.Empty<int>();

        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;

        // Drive peer lifecycle from the test (mirrors what a real transport would raise on the loop thread).
        public void RaiseConnect(int peerId) => OnPeerConnected?.Invoke(peerId);
        public void RaiseDisconnect(int peerId) => OnPeerDisconnected?.Invoke(peerId);
        public void RaiseData(int peerId, byte[] data, int length) => OnDataReceived?.Invoke(peerId, data, length);

        public bool Listen(string address, int port, int maxConnections) => true;
        public bool Connect(string address, int port) => true;
        public void Disconnect() { }
        public void DisconnectPeer(int peerId) => Disconnected.Add(peerId);
        public void DisconnectPeer(int peerId, byte[] data) => Disconnected.Add(peerId);
        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod) { }
        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod) { }
        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { }
        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { }
        public void PollEvents() { }
        public void FlushSendQueue() { }

        // Quiet the unused-event warnings (these are part of the interface but not exercised here).
        private void Touch() { OnConnected?.Invoke(); OnDisconnected?.Invoke(default); OnDataReceived?.Invoke(0, null, 0); }
    }
}
