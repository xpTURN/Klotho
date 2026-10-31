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
    // A reliable command that goes on the wire, for the tests of which command kinds each path carries.
    [KlothoSerializable(9405)]
    public partial class InboundProbeReliableCommand : CommandBase, IReliableCommand
    {
        [KlothoOrder(0)] public int SequenceNumber { get; set; }
        public int OrderKey => 0;
    }

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
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, nameof(ArgumentOutOfRangeException)), "the warning must name what the decode threw");
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

        // ── SD server: a payload that yields no command disconnects — too short, or an unknown type ──

        private static byte[] UnknownTypeCommand()
        {
            var b = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(b, int.MaxValue);
            return b;
        }

        private static ClientInputBundleMessage OneEntryBundle(byte[] data)
        {
            var b = new ClientInputBundleMessage { PlayerId = 0, Count = 1 };
            b.Entries[0] = new ClientInputBundleMessage.BundleEntry { Tick = 1, CommandData = data, CommandDataLength = data.Length };
            return b;
        }

        private void AssertNoCommandDisconnects(INetworkMessage msg, string cause)
        {
            const int peerId = 7;
            var data = Bytes(_ser, msg);
            int sends = _transport.SendCallCount;

            InvokeNoThrow(_svc, "HandleDataReceived", peerId, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "the sender must be disconnected");
            Assert.AreEqual(0, _transport.SendCallCount - sends, "no ACK for a payload that yields no command");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, cause), $"the warning must name the cause ({cause})");
        }

        [Test]
        public void ClientInput_ShortPayload_Disconnects()
            => AssertNoCommandDisconnects(new ClientInputMessage { Tick = 1, PlayerId = 0, CommandData = new byte[2], CommandDataLength = 2 }, "length 2");

        [Test]
        public void ReliableCommandSubmit_ShortPayload_Disconnects()
            => AssertNoCommandDisconnects(new ReliableCommandSubmitMessage { PlayerId = 0, CommandData = new byte[2], CommandDataLength = 2 }, "length 2");

        [Test]
        public void ClientInputBundle_ShortEntry_Disconnects()
            => AssertNoCommandDisconnects(OneEntryBundle(new byte[2]), "length 2");

        [Test]
        public void ClientInput_UnknownCommandType_Disconnects()
            => AssertNoCommandDisconnects(new ClientInputMessage { Tick = 1, PlayerId = 0, CommandData = UnknownTypeCommand(), CommandDataLength = 12 }, "unknown command type 2147483647");

        [Test]
        public void ReliableCommandSubmit_UnknownCommandType_Disconnects()
            => AssertNoCommandDisconnects(new ReliableCommandSubmitMessage { PlayerId = 0, CommandData = UnknownTypeCommand(), CommandDataLength = 12 }, "unknown command type 2147483647");

        [Test]
        public void ClientInputBundle_UnknownCommandTypeEntry_Disconnects()
            => AssertNoCommandDisconnects(OneEntryBundle(UnknownTypeCommand()), "unknown command type 2147483647");

        // ── SD server: a command kind the path does not carry disconnects ──

        private static byte[] Encode(CommandBase c)
        {
            var b = new byte[c.GetSerializedSize()];
            var w = new SpanWriter(b);
            c.Serialize(ref w);
            return b;
        }

        private static byte[] PlayerJoin() => Encode(new PlayerJoinCommand { PlayerId = 0, Tick = 1, JoinedPlayerId = 77 });
        private static byte[] Reliable() => Encode(new InboundProbeReliableCommand { PlayerId = 0, Tick = 1, SequenceNumber = 1 });

        private static ClientInputMessage Input(byte[] data)
            => new ClientInputMessage { Tick = 1, PlayerId = 0, CommandData = data, CommandDataLength = data.Length };

        private static ReliableCommandSubmitMessage Submit(byte[] data)
            => new ReliableCommandSubmitMessage { PlayerId = 0, CommandData = data, CommandDataLength = data.Length };

        private static int PooledOf(int typeId)
        {
            var pools = (Dictionary<int, Stack<ICommand>>)typeof(CommandPool)
                .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return pools.TryGetValue(typeId, out var s) ? s.Count : 0;
        }

        private void AssertKindDisconnects<T>(INetworkMessage msg) where T : CommandBase, new()
        {
            CommandPool.Return(CommandPool.Get<T>());
            int typeId = new T().CommandTypeId;
            int seeded = PooledOf(typeId);

            AssertNoCommandDisconnects(msg, $"{typeof(T).Name} — disconnect");
            Assert.AreEqual(seeded, PooledOf(typeId), "the refused command must go back to the pool");
        }

        [Test]
        public void ClientInput_PlayerJoinCommand_Disconnects()
            => AssertKindDisconnects<PlayerJoinCommand>(Input(PlayerJoin()));

        [Test]
        public void ClientInputBundle_PlayerJoinCommandEntry_Disconnects()
            => AssertKindDisconnects<PlayerJoinCommand>(OneEntryBundle(PlayerJoin()));

        [Test]
        public void ReliableCommandSubmit_PlayerJoinCommand_Disconnects()
            => AssertKindDisconnects<PlayerJoinCommand>(Submit(PlayerJoin()));

        // A reliable command belongs on ReliableCommandSubmit, where the entitlement gate and the sequence dedup apply.
        [Test]
        public void ClientInput_ReliableCommand_Disconnects()
            => AssertKindDisconnects<InboundProbeReliableCommand>(Input(Reliable()));

        [Test]
        public void ClientInputBundle_ReliableCommandEntry_Disconnects()
            => AssertKindDisconnects<InboundProbeReliableCommand>(OneEntryBundle(Reliable()));

        // Control: the reliable path still takes a reliable command (refused here only for the unbound peer, without a disconnect).
        [Test]
        public void ReliableCommandSubmit_ReliableCommand_IsNotRefusedForItsKind()
        {
            var data = Bytes(_ser, Submit(Reliable()));

            InvokeNoThrow(_svc, "HandleDataReceived", 7, data, data.Length);

            Assert.AreEqual(0, _transport.DisconnectPeerCallCount);
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

    /// <summary>Peer-to-peer: the host's pre-auth gate, decode before relay, the pool return on the early exits, and the host taking a command only from the player it names.</summary>
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
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, nameof(ArgumentOutOfRangeException)), "the warning must name what the decode threw");
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
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, nameof(ArgumentOutOfRangeException)), "the warning must name what the decode threw");
        }

        // ── a payload that yields no command: the host disconnects without relaying; a guest only drops ──

        private static byte[] UnknownType()
        {
            var b = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(b, int.MaxValue);
            return b;
        }

        private void AssertHostDisconnectsWithoutRelay(byte[] data, string cause)
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(42, guest.LocalPlayerId, data), guest.Transport.LocalPeerId);

            Assert.AreEqual(0, host.Transport.SendCallCount - sends, "a payload without a command must not be relayed");
            Assert.AreEqual(1, host.Transport.DisconnectPeerCallCount - drops, "the authority disconnects the sender");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, cause), $"the warning must name the cause ({cause})");
        }

        [Test]
        public void Host_ShortGuestCommand_NotRelayed_Disconnects()
            => AssertHostDisconnectsWithoutRelay(new byte[2], "length=2");

        [Test]
        public void Host_UnknownGuestCommandType_NotRelayed_Disconnects()
            => AssertHostDisconnectsWithoutRelay(UnknownType(), "unknown command type 2147483647");

        private void AssertGuestDropsWithoutDisconnect(byte[] data)
        {
            var guest = _harness.Guests[0];
            int drops = guest.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(guest.NetworkService, "HandleCommandMessage", Msg(42, _harness.Host.LocalPlayerId, data), 0);

            Assert.AreEqual(0, guest.Transport.DisconnectPeerCallCount - drops, "a guest does not disconnect its host");
        }

        [Test]
        public void Guest_ShortHostCommand_DroppedWithoutDisconnect()
            => AssertGuestDropsWithoutDisconnect(new byte[2]);

        [Test]
        public void Guest_UnknownHostCommandType_DroppedWithoutDisconnect()
            => AssertGuestDropsWithoutDisconnect(UnknownType());

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
        public void Guest_OwnCommandEcho_ReturnsItToThePool()
        {
            var guest = _harness.Guests[0];
            int seeded = SeedPool();

            InvokeNoThrow(guest.NetworkService, "HandleCommandMessage", Msg(41, guest.LocalPlayerId, Valid(guest.LocalPlayerId, 41)), 0);

            Assert.AreEqual(seeded, PooledStopCommands(), "the own-command echo must not lose the decoded command");
        }

        // ── the host takes a command only from the player it names ──

        // A guest sends only its own player's commands, with the header copied from the command. Anything
        // else is forged: not relayed, not stored, back to the pool, and the sender is disconnected.
        private void AssertHostRejects(CommandMessage msg, int fromPeerId, int tick, int playerId)
        {
            var host = _harness.Host;
            Assert.IsFalse(host.Engine.HasCommand(tick, playerId), "precondition: the slot is empty");
            int seeded = SeedPool();
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", msg, fromPeerId);

            Assert.AreEqual(0, host.Transport.SendCallCount - sends, "a forged command must not be relayed");
            Assert.IsFalse(host.Engine.HasCommand(tick, playerId), "a forged command must not be stored");
            Assert.AreEqual(1, host.Transport.DisconnectPeerCallCount - drops, "the host disconnects the sender");
            Assert.AreEqual(seeded, PooledStopCommands(), "a rejected command must go back to the pool");
        }

        [Test]
        public void Host_PayloadNamingAnotherPlayer_NotRelayedNorStored_Disconnects()
        {
            var guest = _harness.Guests[0];
            int victim = _harness.Guests[1].LocalPlayerId;
            AssertHostRejects(Msg(30, guest.LocalPlayerId, Valid(victim, 30)), guest.Transport.LocalPeerId, 30, victim);
        }

        [Test]
        public void Host_HeaderNamingTheHost_NotRelayed_Disconnects()
        {
            var guest = _harness.Guests[0];
            int host = _harness.Host.LocalPlayerId;
            AssertHostRejects(Msg(31, host, Valid(host, 31)), guest.Transport.LocalPeerId, 31, host);
        }

        [Test]
        public void Host_HeaderNamingAnotherGuest_NotRelayedNorStored_Disconnects()
        {
            var guest = _harness.Guests[0];
            int victim = _harness.Guests[1].LocalPlayerId;
            AssertHostRejects(Msg(32, victim, Valid(victim, 32)), guest.Transport.LocalPeerId, 32, victim);
        }

        [Test]
        public void Host_PayloadTickOffItsHeader_NotRelayed_Disconnects()
        {
            var guest = _harness.Guests[0];
            var buf = (InputBuffer)typeof(KlothoEngine).GetField("_inputBuffer", Priv).GetValue(_harness.Host.Engine);
            buf.SealEmpty(30, guest.LocalPlayerId);
            // The header names an unsealed slot so the seal guard lets it through; the payload targets the sealed one.
            AssertHostRejects(Msg(31, guest.LocalPlayerId, Valid(guest.LocalPlayerId, 30)), guest.Transport.LocalPeerId, 31, guest.LocalPlayerId);
        }

        [Test]
        public void Host_CommandFromUnboundPeer_NotRelayedNorStored_Disconnects()
        {
            int player = _harness.Guests[0].LocalPlayerId;
            AssertHostRejects(Msg(33, player, Valid(player, 33)), 99, 33, player);
        }

        [Test]
        public void Host_CommandFromThePlayerItNames_RelayedAndStored()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            Assert.IsFalse(host.Engine.HasCommand(34, guest.LocalPlayerId), "precondition: the slot is empty");
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(34, guest.LocalPlayerId, Valid(guest.LocalPlayerId, 34)), guest.Transport.LocalPeerId);

            Assert.AreEqual(1, host.Transport.SendCallCount - sends, "relayed to the other guest");
            Assert.IsTrue(host.Engine.HasCommand(34, guest.LocalPlayerId));
            Assert.AreEqual(0, host.Transport.DisconnectPeerCallCount - drops);
        }

        // ── a framework system command (PlayerJoin) comes only from the authority ──

        private static byte[] Encode(CommandBase c)
        {
            var b = new byte[c.GetSerializedSize()];
            var w = new SpanWriter(b);
            c.Serialize(ref w);
            return b;
        }

        private static List<ICommand> SystemLane(KlothoEngine engine, int tick)
        {
            var buf = (InputBuffer)typeof(KlothoEngine).GetField("_inputBuffer", Priv).GetValue(engine);
            var lanes = (Dictionary<int, List<ICommand>>)typeof(InputBuffer).GetField("_systemCommands", Priv).GetValue(buf);
            return lanes.TryGetValue(tick, out var list) ? list : new List<ICommand>();
        }

        private static List<int> Roster(KlothoEngine engine)
            => (List<int>)typeof(KlothoEngine).GetField("_activePlayerIds", Priv).GetValue(engine);

        private static int PooledOf(int typeId)
        {
            var pools = (Dictionary<int, Stack<ICommand>>)typeof(CommandPool)
                .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return pools.TryGetValue(typeId, out var s) ? s.Count : 0;
        }

        [Test]
        public void Host_PlayerJoinFromGuest_NotRelayedNorStored_Disconnects()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            CommandPool.Return(CommandPool.Get<PlayerJoinCommand>());
            var join = new PlayerJoinCommand { PlayerId = guest.LocalPlayerId, Tick = 35, JoinedPlayerId = 77 };
            int seeded = PooledOf(join.CommandTypeId);
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(35, guest.LocalPlayerId, Encode(join)), guest.Transport.LocalPeerId);

            Assert.AreEqual(0, host.Transport.SendCallCount - sends, "an authority-only command from a guest must not be relayed");
            Assert.AreEqual(0, SystemLane(host.Engine, 35).Count, "nor stored");
            Assert.AreEqual(1, host.Transport.DisconnectPeerCallCount - drops, "the host disconnects the sender");
            Assert.AreEqual(seeded, PooledOf(join.CommandTypeId), "the refused command must go back to the pool");
        }

        // The forgery sent through the guest's own SendCommand: a ghost in the roster would leave every node's
        // verified chain waiting for input that never comes.
        [Test]
        public void Guest_PlayerJoinCommand_DoesNotStallTheChain()
        {
            var host = _harness.Host;
            var forger = _harness.Guests[0];
            var other = _harness.Guests[1];
            _harness.AdvanceAllToTick(20);
            var join = CommandPool.Get<PlayerJoinCommand>();
            join.PlayerId = forger.LocalPlayerId;
            join.Tick = forger.CurrentTick + forger.Engine.InputDelay;
            join.JoinedPlayerId = 77;
            join.Entitlement = null;
            int joinTick = join.Tick;

            forger.NetworkService.SendCommand(join);
            _harness.AdvanceAllToTick(host.CurrentTick + 40);

            Assert.Greater(host.Engine.LastVerifiedTick, joinTick + 10, "the host's verified chain keeps moving");
            Assert.Greater(other.Engine.LastVerifiedTick, joinTick + 10, "and the other guest's");
            CollectionAssert.DoesNotContain(Roster(host.Engine), 77);
            CollectionAssert.DoesNotContain(Roster(other.Engine), 77);
        }

        // Control: a guest's reliable command is a system-lane command it does send, on the slot path.
        [Test]
        public void Host_ReliableCommandFromGuest_RelayedAndStored()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            var rel = new InboundProbeReliableCommand { PlayerId = guest.LocalPlayerId, Tick = 36, SequenceNumber = 1 };
            int sends = host.Transport.SendCallCount, drops = host.Transport.DisconnectPeerCallCount;

            InvokeNoThrow(host.NetworkService, "HandleCommandMessage", Msg(36, guest.LocalPlayerId, Encode(rel)), guest.Transport.LocalPeerId);

            Assert.AreEqual(1, host.Transport.SendCallCount - sends, "relayed to the other guest");
            Assert.AreEqual(1, SystemLane(host.Engine, 36).Count, "stored on the system lane");
            Assert.AreEqual(0, host.Transport.DisconnectPeerCallCount - drops);
        }
    }
}
