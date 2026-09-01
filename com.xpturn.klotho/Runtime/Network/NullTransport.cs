using System;
using System.Collections.Generic;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// A transport with no socket, for single-player sessions started through
    /// <c>KlothoSessionFlow.StartLocal</c>. Every send is dropped, no event is ever raised, and there
    /// is nothing to bind or connect to.
    ///
    /// <para><b>Why this exists rather than allowing a null transport.</b> The service dereferences
    /// <c>_transport</c> in 47 places without a null guard; a solo host happens to reach none of them
    /// today, but only because each one needs a peer, a spectator, or a transport event to get there.
    /// Allowing null would turn that survey into an undocumented invariant that one late join or one
    /// reconnect breaks. Here the same 47 sites are harmless calls into no-ops.</para>
    ///
    /// <para><b>Listen and Connect return false on purpose.</b> There is no socket, and reporting
    /// success would tell a game that remote players can arrive. <c>StartHostAndListen</c> already
    /// reads false as a bind failure and tears the session down, so wiring this transport into the
    /// networked host path fails cleanly instead of silently producing an unreachable room.</para>
    /// </summary>
    public sealed class NullTransport : INetworkTransport
    {
        private readonly IKLogger _logger;

        // One warning per instance per method: the misuse is a wiring mistake, so it is worth saying
        // once, and a retry loop must not turn it into a flood.
        private bool _listenWarned;
        private bool _connectWarned;

        public NullTransport(IKLogger logger = null)
        {
            _logger = logger;
        }

        public bool IsConnected => false;

        public int LocalPeerId => 0;

        public bool Listen(string address, int port, int maxConnections)
        {
            if (!_listenWarned)
            {
                _listenWarned = true;
                _logger?.KWarning(
                    $"[NullTransport] Listen({address}:{port}) refused — this transport has no socket. " +
                    $"It is for single-player sessions via KlothoSessionFlow.StartLocal; use a real " +
                    $"transport (e.g. LiteNetLibTransport) to host for remote players.");
            }
            return false;
        }

        public bool Connect(string address, int port)
        {
            if (!_connectWarned)
            {
                _connectWarned = true;
                _logger?.KWarning(
                    $"[NullTransport] Connect({address}:{port}) refused — this transport has no socket. " +
                    $"It is for single-player sessions via KlothoSessionFlow.StartLocal; use a real " +
                    $"transport (e.g. LiteNetLibTransport) to join a remote host.");
            }
            return false;
        }

        public void Disconnect() { }

        public void DisconnectPeer(int peerId) { }

        public void DisconnectPeer(int peerId, byte[] data) { }

        public IEnumerable<int> GetConnectedPeerIds() => Array.Empty<int>();

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod) { }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod) { }

        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { }

        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { }

        public void PollEvents() { }

        public void FlushSendQueue() { }

        // Declared to satisfy the interface and never raised — nothing arrives on a transport with no
        // socket. RoomScopedTransport suppresses CS0067 for the same reason.
#pragma warning disable CS0067
        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
#pragma warning restore CS0067

        public int LastDisconnectPayload => -1;

        public string RemoteAddress => string.Empty;

        public int RemotePort => 0;
    }
}
