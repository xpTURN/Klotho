using System;
using System.Collections.Generic;

using NUnit.Framework;

using xpTURN.Klotho.Network; // INetworkTransport, DeliveryMethod, DisconnectReason

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Executable spec for the <see cref="INetworkTransport"/> cross-call ordering contract
    /// (INetworkTransport.cs): for a given <see cref="DeliveryMethod"/>, Send and Broadcast MUST share
    /// a single per-peer ordered stream, so a Send followed by a Broadcast is delivered to each peer in
    /// call order. P2P late-join depends on this — the entitlement-bearing roster notification (Send)
    /// must reach existing peers before the PlayerJoinCommand (Broadcast) whose OnPlayerJoinedWorld
    /// callback reads that entitlement (KlothoNetworkService.LateJoin.cs). A transport that routes
    /// broadcasts through a separate channel/queue would let the command overtake the notification on
    /// some peers → GetPlayerEntitlement null → divergent tick-0 seed → hash desync.
    ///
    /// The contract was documentation-only. This locks it in code for the one in-repo transport that is
    /// pure C# routing (<see cref="RoomScopedTransport"/>) and doubles as the reference spec a 3rd-party
    /// transport can run its own implementation through (LiteNetLibTransport rides LiteNetLib SendToAll,
    /// which shares the per-DeliveryMethod channel with unicast Send; it needs sockets so is not exercised here).
    /// </summary>
    public sealed class TransportOrderingContractTests
    {
        /// <summary>
        /// Records every outbound Send in one ordered list PER PEER (Broadcast delegates to per-peer Send
        /// on the shared transport, so both land here). Models the required single ordered stream per peer.
        /// </summary>
        private sealed class RecordingTransport : INetworkTransport
        {
            public readonly Dictionary<int, List<(byte tag, DeliveryMethod method)>> PerPeer =
                new Dictionary<int, List<(byte, DeliveryMethod)>>();

            private void Record(int peerId, byte[] data, int length, DeliveryMethod method)
            {
                if (!PerPeer.TryGetValue(peerId, out var stream))
                    PerPeer[peerId] = stream = new List<(byte, DeliveryMethod)>();
                // First byte tags the payload so the test can identify notification vs. command.
                stream.Add((length > 0 ? data[0] : (byte)0, method));
            }

            public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
                => Record(peerId, data, data?.Length ?? 0, deliveryMethod);
            public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
                => Record(peerId, data, length, deliveryMethod);

            // RoomScopedTransport.Broadcast delegates to _shared.Send per room peer, so these are unused
            // for that transport; kept correct for any other transport run through this recorder.
            public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { }
            public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { }

            public bool IsConnected => true;
            public int LocalPeerId => 0;
            public int LastDisconnectPayload => -1;
            public int RemotePort => 0;
            public string RemoteAddress => string.Empty;
            public IEnumerable<int> GetConnectedPeerIds() => Array.Empty<int>();
            public bool Listen(string address, int port, int maxConnections) => true;
            public bool Connect(string address, int port) => true;
            public void Disconnect() { }
            public void DisconnectPeer(int peerId) { }
            public void DisconnectPeer(int peerId, byte[] data) { }
            public void PollEvents() { }
            public void FlushSendQueue() { }

#pragma warning disable CS0067
            public event Action<int, byte[], int> OnDataReceived;
            public event Action<int> OnPeerConnected;
            public event Action<int> OnPeerDisconnected;
            public event Action OnConnected;
            public event Action<DisconnectReason> OnDisconnected;
#pragma warning restore CS0067
        }

        private const byte Notification = 1; // late-join roster notification (Send, entitlement-bearing)
        private const byte JoinCommand = 2;  // PlayerJoinCommand (Broadcast, reads the entitlement)

        [Test]
        public void RoomScopedBroadcast_SharesPerPeerSendStream_WithSend()
        {
            var shared = new RecordingTransport();
            var scoped = new RoomScopedTransport(shared);
            scoped.AddPeer(1);
            scoped.AddPeer(2);

            // Late-join order: unicast the notification to an existing peer, THEN broadcast the join command.
            scoped.Send(1, new[] { Notification }, DeliveryMethod.ReliableOrdered);
            scoped.Broadcast(new[] { JoinCommand }, DeliveryMethod.ReliableOrdered);

            // Peer 1 (received both): notification MUST precede the command on its single ordered stream.
            var p1 = shared.PerPeer[1];
            Assert.AreEqual(2, p1.Count);
            Assert.AreEqual((Notification, DeliveryMethod.ReliableOrdered), p1[0]);
            Assert.AreEqual((JoinCommand, DeliveryMethod.ReliableOrdered), p1[1]);

            // Peer 2 (broadcast only): the command still lands on the same per-peer Send stream —
            // Broadcast did NOT route through a separate channel/queue.
            var p2 = shared.PerPeer[2];
            Assert.AreEqual((JoinCommand, DeliveryMethod.ReliableOrdered), XAssert.Single(p2));
        }

        [Test]
        public void RoomScopedBroadcast_TargetsOnlyRoomPeers()
        {
            var shared = new RecordingTransport();
            var scoped = new RoomScopedTransport(shared);
            scoped.AddPeer(1);

            scoped.Broadcast(new[] { JoinCommand }, DeliveryMethod.ReliableOrdered);

            // Only the room member's stream receives the broadcast (guards the room-scoping invariant
            // the ordering contract rides on).
            Assert.IsTrue(shared.PerPeer.ContainsKey(1));
            Assert.IsFalse(shared.PerPeer.ContainsKey(2));
        }
    }
}
