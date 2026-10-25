using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Server-driven server: a pre-auth peer gets only its first-message types decoded, a command that
    /// fails to decode inside a well-formed message disconnects its sender and goes back to the pool, and a
    /// zero-copy length past the end disconnects instead of reading what the pooled buffer still holds.
    /// </summary>
    [TestFixture]
    public class InboundLengthServiceTests
    {
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private static Dictionary<NetworkMessageType, INetworkMessage> CacheOf(MessageSerializer ser)
            => (Dictionary<NetworkMessageType, INetworkMessage>)typeof(MessageSerializer).GetField("_messageCache", Priv).GetValue(ser);

        private static int PooledStopCommands()
        {
            var pools = (Dictionary<int, Stack<ICommand>>)typeof(CommandPool)
                .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return pools.TryGetValue(StopCommand.TYPE_ID, out var s) ? s.Count : 0;
        }

        // Make sure the pool holds an instance, so a decode that loses its instance shows as a drop.
        private static int SeedPool()
        {
            CommandPool.Return(CommandPool.Get<StopCommand>());
            return PooledStopCommands();
        }

        private static byte[] TruncatedStopCommand()
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, StopCommand.TYPE_ID);
            return b;
        }

        private static byte[] Bytes(MessageSerializer ser, INetworkMessage m) => ser.Serialize(m);

        private static void InvokeNoThrow(object target, string method, params object[] args)
        {
            try { target.GetType().GetMethod(method, Priv).Invoke(target, args); }
            catch (TargetInvocationException e) { Assert.Fail($"{method} threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
        }

        // ── SD server fixture ───────────────────────────────────────────────

        private ServerNetworkService _svc;
        private TestTransport _transport;
        private LogCapture _log;
        private MessageSerializer _ser;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _transport = new TestTransport();
            _transport.Listen("localhost", 0, 4);
            _log = new LogCapture();
            _svc = new ServerNetworkService();
            // A real factory: a null one would make every decode throw and pass the malformed-command checks for the wrong reason.
            _svc.Initialize(_transport, new CommandFactory(), _log);
            _svc.CreateRoom("test", 4);
            _ser = new MessageSerializer();
        }

        [TearDown]
        public void TearDown() => TestTransport.Reset();

        private MessageSerializer ServerSerializer() => (MessageSerializer)typeof(ServerNetworkService).GetField("_messageSerializer", Priv).GetValue(_svc);

        // ── pre-auth: only the first-message types are decoded ─────────

        [Test]
        public void ServerPending_OtherFirstMessageType_IsRefusedWithoutDecoding()
        {
            const int peerId = 11;
            var pending = (Dictionary<int, long>)typeof(ServerNetworkService).GetField("_pendingPeers", Priv).GetValue(_svc);
            pending[peerId] = 0L;
            var data = Bytes(_ser, new GameStartMessage());

            InvokeNoThrow(_svc, "HandleDataReceived", peerId, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount);
            Assert.IsFalse(pending.ContainsKey(peerId));
            Assert.IsFalse(CacheOf(ServerSerializer()).ContainsKey(NetworkMessageType.GameStart), "GameStart must not be deserialized pre-auth");
        }

        [Test]
        public void RoomRouter_FirstMessageNotHandshake_IsRefusedWithoutDecoding()
        {
            var router = new RoomRouter(_transport, _log);
            var routerSer = (MessageSerializer)typeof(RoomRouter).GetField("_serializer", Priv).GetValue(router);
            var data = Bytes(_ser, new GameStartMessage());

            InvokeNoThrow(router, "RouteFirstMessage", 21, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount);
            Assert.IsFalse(CacheOf(routerSer).ContainsKey(NetworkMessageType.GameStart), "GameStart must not be deserialized by the router");

            // Control: a handshake is still decoded (it is then refused for want of a room).
            var hs = Bytes(_ser, new RoomHandshakeMessage { RoomId = 3 });
            InvokeNoThrow(router, "RouteFirstMessage", 22, hs, hs.Length);
            Assert.IsTrue(CacheOf(routerSer).ContainsKey(NetworkMessageType.RoomHandshake));
            router.Dispose();
        }

        // ── SD server: a truncated inner command disconnects and returns its instance ──

        private void AssertMalformedCommandDisconnects(INetworkMessage msg)
        {
            const int peerId = 7;
            int seeded = SeedPool();
            var data = Bytes(_ser, msg);

            InvokeNoThrow(_svc, "HandleDataReceived", peerId, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "the sender must be disconnected");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, "Malformed command"), "a warning must name the cause");
            Assert.AreEqual(seeded, PooledStopCommands(), "the half-filled command must go back to the pool");
        }

        [Test]
        public void ClientInput_TruncatedCommand_DisconnectsAndReturnsTheCommand()
            => AssertMalformedCommandDisconnects(new ClientInputMessage { Tick = 1, PlayerId = 0, CommandData = TruncatedStopCommand(), CommandDataLength = 4 });

        [Test]
        public void ReliableCommandSubmit_TruncatedCommand_DisconnectsAndReturnsTheCommand()
            => AssertMalformedCommandDisconnects(new ReliableCommandSubmitMessage { PlayerId = 0, CommandData = TruncatedStopCommand(), CommandDataLength = 4 });

        [Test]
        public void ClientInputBundle_TruncatedEntry_DisconnectsAndReturnsTheCommand()
        {
            var b = new ClientInputBundleMessage { PlayerId = 0, Count = 1 };
            b.Entries[0] = new ClientInputBundleMessage.BundleEntry { Tick = 1, CommandData = TruncatedStopCommand(), CommandDataLength = 4 };
            AssertMalformedCommandDisconnects(b);
        }

        // ── SD server: a declared length past the end disconnects on the regular path ──

        [Test]
        public void ClientInput_DeclaredLengthPastEnd_Disconnects()
        {
            const int peerId = 8;
            var data = new byte[4096];
            for (int i = 0; i < data.Length; i++) data[i] = 0xAB;   // what a pooled buffer may still hold
            Array.Clear(data, 0, 13);
            data[0] = (byte)NetworkMessageType.ClientInput;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(9), 200);

            InvokeNoThrow(_svc, "HandleDataReceived", peerId, data, 13);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "the residue must not be read as a command");
        }
    }

    /// <summary>Peer-to-peer: the host's pre-auth gate, decode before relay, and the pool return on the early exits.</summary>
    [TestFixture]
    public class InboundLengthP2pTests
    {
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private LogCapture _log;
        private KlothoTestHarness _harness;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _log = new LogCapture();
            _harness = new KlothoTestHarness(_log);
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.AddGuest();
            _harness.StartPlaying();
            _log.Clear();
        }

        [TearDown]
        public void TearDown() => TestTransport.Reset();

        private static void InvokeNoThrow(object target, string method, params object[] args)
        {
            try { target.GetType().GetMethod(method, Priv).Invoke(target, args); }
            catch (TargetInvocationException e) { Assert.Fail($"{method} threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
        }

        private static int PooledStopCommands()
        {
            var pools = (Dictionary<int, Stack<ICommand>>)typeof(CommandPool)
                .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return pools.TryGetValue(StopCommand.TYPE_ID, out var s) ? s.Count : 0;
        }

        private static int SeedPool()
        {
            CommandPool.Return(CommandPool.Get<StopCommand>());
            return PooledStopCommands();
        }

        private static CommandMessage Msg(int tick, int playerId, byte[] data)
            => new CommandMessage { Tick = tick, PlayerId = playerId, SenderTick = tick, CommandData = data, CommandDataLength = data.Length };

        private static byte[] Truncated()
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, StopCommand.TYPE_ID);
            return b;
        }

        private static byte[] Valid(int playerId, int tick)
        {
            var c = new StopCommand { PlayerId = playerId, Tick = tick };
            var b = new byte[c.GetSerializedSize()];
            var w = new SpanWriter(b);
            c.Serialize(ref w);
            return b;
        }

        // ── pre-auth: only the first-message types are decoded ──

        [Test]
        public void HostPending_OtherFirstMessageType_IsRefusedWithoutDecoding()
        {
            var host = _harness.Host;
            var svc = host.NetworkService;
            ((HashSet<int>)typeof(KlothoNetworkService).GetField("_pendingPeers", Priv).GetValue(svc)).Add(99);
            var ser = (MessageSerializer)typeof(KlothoNetworkService).GetField("_messageSerializer", Priv).GetValue(svc);
            var data = new MessageSerializer().Serialize(new GameStartMessage());
            int before = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(svc, "HandleDataReceived", 99, data, data.Length);

            Assert.AreEqual(1, host.Transport.DisconnectPeerCallCount - before);
            var cache = (Dictionary<NetworkMessageType, INetworkMessage>)typeof(MessageSerializer).GetField("_messageCache", Priv).GetValue(ser);
            Assert.IsFalse(cache.ContainsKey(NetworkMessageType.GameStart), "GameStart must not be deserialized pre-auth");
        }

        // ── truncated command: the host neither relays nor throws, and disconnects; a guest only drops ──

        [Test]
        public void Host_TruncatedGuestCommand_NotRelayed_Disconnects()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int seeded = SeedPool();
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(40, guest.LocalPlayerId, Truncated()), guest.Transport.LocalPeerId);

            Assert.AreEqual(0, host.Transport.SendCallCount - sends, "a malformed command must not be relayed");
            Assert.AreEqual(1, host.Transport.DisconnectPeerCallCount - drops, "the authority disconnects the sender");
            Assert.AreEqual(seeded, PooledStopCommands(), "the half-filled command must go back to the pool");
        }

        [Test]
        public void Guest_TruncatedHostCommand_DroppedWithoutDisconnect()
        {
            var guest = _harness.Guests[0];
            int drops = guest.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(guest.NetworkService, "HandleCommandMessage", Msg(40, _harness.Host.LocalPlayerId, Truncated()), 0);

            Assert.AreEqual(0, guest.Transport.DisconnectPeerCallCount - drops, "a guest does not disconnect its host");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, "Malformed command"));
        }

        // ── the early exits after the new decode give the command back ──

        [Test]
        public void Host_SealedSlot_ValidCommand_ReturnsItToThePool()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            var buf = (InputBuffer)typeof(KlothoEngine).GetField("_inputBuffer", Priv).GetValue(host.Engine);
            buf.SealEmpty(30, guest.LocalPlayerId);
            int seeded = SeedPool();
            int dropCount = host.NetworkService.RelaySealDropCount, sends = host.Transport.SendCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(30, guest.LocalPlayerId, Valid(guest.LocalPlayerId, 30)), guest.Transport.LocalPeerId);

            Assert.AreEqual(1, host.NetworkService.RelaySealDropCount - dropCount);
            Assert.AreEqual(0, host.Transport.SendCallCount - sends);
            Assert.AreEqual(seeded, PooledStopCommands(), "a sealed-slot drop must not lose the decoded command");
        }

        [Test]
        public void Host_OwnCommandEcho_ReturnsItToThePool()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int seeded = SeedPool();

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(41, host.LocalPlayerId, Valid(host.LocalPlayerId, 41)), guest.Transport.LocalPeerId);

            Assert.AreEqual(seeded, PooledStopCommands(), "the own-command echo must not lose the decoded command");
        }
    }
}
