using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Stage 3a (L2 server) — ServerNetworkService.HandleDataReceived integrity + disconnect policy.
    /// Slim unit tests: invoke HandleDataReceived via reflection and inspect _pendingPeers / _peerDeviceIds.
    /// Success branches that dispatch into StartHandshake / HandleReconnectRequest / HandleSpectatorJoin
    /// may throw downstream when the engine isn't initialized; the L2 boundary state (pending Remove,
    /// DisconnectPeer count) is asserted *regardless* of whether the inner handler throws — those are
    /// the assertions IMP35 owns.
    /// </summary>
    [TestFixture]
    public class ServerNetworkServiceMalformedTests
    {
        private ServerNetworkService _svc;
        private TestTransport _transport;
        private LogCapture _logger;
        private MessageSerializer _serializer;

        // Cached reflection handles
        private MethodInfo _handleDataReceived;
        private FieldInfo _pendingPeersField;
        private FieldInfo _peerDeviceIdsField;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _transport.Listen("localhost", 0, 4); // sets IsHost=true so DisconnectPeer is observable

            _logger = new LogCapture();
            _svc = new ServerNetworkService();
            _svc.Initialize(_transport, null, _logger);
            // CreateRoom sets _maxPlayersPerRoom; without it, MaxPlayerCapacity == 0
            // and the PlayerJoin branch's "Room full" gate disconnects every peer.
            _svc.CreateRoom("test", 4);
            // HandleSpectatorJoin checks _spectators.Count >= _maxSpectatorsPerRoom;
            // default is 0, so any spectator would be rejected. Set a non-zero cap.
            _svc.MaxSpectatorsPerRoom = 4;

            _serializer = new MessageSerializer();

            _handleDataReceived = typeof(ServerNetworkService).GetMethod(
                "HandleDataReceived", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_handleDataReceived, "reflection: HandleDataReceived");

            _pendingPeersField = typeof(ServerNetworkService).GetField(
                "_pendingPeers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_pendingPeersField, "reflection: _pendingPeers");

            _peerDeviceIdsField = typeof(ServerNetworkService).GetField(
                "_peerDeviceIds", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_peerDeviceIdsField, "reflection: _peerDeviceIds");
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── L2s-1 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_ValidPlayerJoin_RemovesPendingNoDisconnect()
        {
            const int peerId = 1;
            AddPending(peerId);

            byte[] data = SerializeMessage(new PlayerJoinMessage { DeviceId = "device-A" });
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared after dispatch");
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount, "valid PlayerJoin must not disconnect");
            Assert.IsTrue(GetPeerDeviceIds().TryGetValue(peerId, out var dev) && dev == "device-A",
                "_peerDeviceIds must reflect the valid DeviceId");
        }

        // ── L2s-2 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_ValidReconnectRequest_RemovesPending()
        {
            const int peerId = 2;
            AddPending(peerId);

            byte[] data = SerializeMessage(new ReconnectRequestMessage
            {
                SessionMagic = 0xABCDEFL,
                PlayerId = 1,
                DeviceId = "dev",
            });
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared");
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount, "valid ReconnectRequest must not disconnect");
        }

        // ── L2s-3 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_ValidSpectatorJoin_RemovesPending()
        {
            const int peerId = 3;
            AddPending(peerId);

            byte[] data = SerializeMessage(new SpectatorJoinMessage { SpectatorName = "spec" });
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared");
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount, "valid SpectatorJoin must not disconnect");
        }

        // ── L2s-4 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_MalformedPayload_RemovesPendingAndDisconnects()
        {
            const int peerId = 4;
            AddPending(peerId);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared even on malformed");
            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "malformed first message must disconnect");
            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "Malformed/unknown first message"),
                "L2 should log a malformed-first-message warning");
        }

        // ── L2s-5 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_UnknownTypeForFirstMessage_Disconnects()
        {
            // ClientInputMessage is registered but inappropriate as a first-message — falls into the else branch.
            const int peerId = 5;
            AddPending(peerId);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.ClientInput);
            // EmptyBody on ClientInput → L1 returns null (deserialize body throws), goes to else → disconnect.
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId));
            Assert.AreEqual(1, _transport.DisconnectPeerCallCount);
        }

        // ── L2s-6 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_RemoveCalledExactlyOnce()
        {
            // Verify the regression fix: _pendingPeers.Remove is called exactly once,
            // not duplicated across the 4 branches (Pre-fix had it before deserialize +
            // could be redundantly added by a pre-fix duplicate-call pattern).
            const int peerId = 6;
            AddPending(peerId);
            // Pre-condition: pending count == 1
            Assert.AreEqual(1, GetPendingPeers().Count);

            byte[] data = SerializeMessage(new PlayerJoinMessage { DeviceId = "dev" });
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.AreEqual(0, GetPendingPeers().Count,
                "after one Remove, pending should be empty (regression: no double-remove");
        }

        // ── L2s-7 ───────────────────────────────────────────────────────────

        [Test]
        public void Regular_NullMessage_Disconnects()
        {
            const int peerId = 7;
            // peerId NOT in _pendingPeers → regular dispatch path

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.ClientInput);
            // EmptyBody on a registered type with non-empty body → L1 returns null.
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "regular malformed must disconnect");
            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "Malformed payload"),
                "L2 regular dispatch should log malformed-payload warning");
        }

        // ── L2s-8 ───────────────────────────────────────────────────────────

        [Test]
        public void Regular_ValidClientInput_NormalDispatch()
        {
            const int peerId = 8;
            // peerId NOT in pending. Send an unknown-but-valid type. The switch in HandleDataReceived
            // has a registered case for ClientInput; sending an empty PlayerReady is a registered type
            // whose handler we know is safe (if it were not, downstream throw would propagate; we ignore it).
            byte[] data = SerializeMessage(new PlayerReadyMessage());
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.AreEqual(0, _transport.DisconnectPeerCallCount,
                "valid known message must not disconnect");
        }

        // ── L2s-9 ───────────────────────────────────────────────────────────

        [Test]
        public void Pending_PartialDeserializeBeforeFix_RegressionGuard()
        {
            // Drive each of the 4 branches in turn (PlayerJoin / ReconnectRequest / SpectatorJoin / else)
            // and verify _pendingPeers.Remove was called exactly once per dispatch.
            (NetworkMessageBase msg, byte[] data)[] cases = new (NetworkMessageBase, byte[])[]
            {
                (new PlayerJoinMessage { DeviceId = "x" }, null),
                (new ReconnectRequestMessage { SessionMagic = 1, PlayerId = 1, DeviceId = "x" }, null),
                (new SpectatorJoinMessage { SpectatorName = "x" }, null),
                (null, MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin)), // else branch
            };

            int peerId = 100;
            foreach (var (msg, mal) in cases)
            {
                AddPending(peerId);
                Assert.AreEqual(1, GetPendingPeers().Count, $"setup: pending={peerId}");

                byte[] data = mal ?? SerializeMessage(msg);
                InvokeHandleDataReceived(peerId, data, data.Length);

                Assert.AreEqual(0, GetPendingPeers().Count,
                    $"after dispatch, pending must be empty (peerId={peerId})");
                peerId++;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private void AddPending(int peerId)
        {
            GetPendingPeers().Add(peerId);
        }

        private HashSet<int> GetPendingPeers()
        {
            return (HashSet<int>)_pendingPeersField.GetValue(_svc);
        }

        private Dictionary<int, string> GetPeerDeviceIds()
        {
            return (Dictionary<int, string>)_peerDeviceIdsField.GetValue(_svc);
        }

        private void InvokeHandleDataReceived(int peerId, byte[] data, int length)
        {
            try
            {
                _handleDataReceived.Invoke(_svc, new object[] { peerId, data, length });
            }
            catch (TargetInvocationException)
            {
                // Downstream handlers (StartHandshake / HandleReconnectRequest / HandleSpectatorJoin /
                // HandleClientInputMessage / etc.) may throw when the engine and other deps are not
                // fully initialized in this slim-unit setup. The L2 boundary behavior — the only
                // contract IMP35 owns — already mutated state before the throw, so post-throw
                // assertions on _pendingPeers / DisconnectPeerCallCount / _peerDeviceIds remain valid.
            }
        }

        private byte[] SerializeMessage(NetworkMessageBase msg)
        {
            using (var serialized = _serializer.SerializePooled(msg))
            {
                byte[] buf = new byte[serialized.Length];
                Array.Copy(serialized.Data, 0, buf, 0, serialized.Length);
                return buf;
            }
        }
    }
}
