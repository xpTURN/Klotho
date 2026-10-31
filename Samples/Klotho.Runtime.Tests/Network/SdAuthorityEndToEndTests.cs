using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Server-driven, end to end: a server and two real clients over TestTransport, through the handshake into
    /// Playing. A client that sends a command kind its path does not carry is disconnected and the other client
    /// never sees the command; an honest reliable command still runs, and still goes through the entitlement gate.
    /// The fixture helpers follow the Brawler EditMode ServerDrivenIntegrationTests.
    /// </summary>
    [TestFixture]
    public class SdAuthorityEndToEndTests
    {
        private sealed class Peer
        {
            public TestTransport Transport;
            public IKlothoNetworkService NetworkService;
            public KlothoEngine Engine;
            public TestSimulation Simulation;
            public int LocalPlayerId => NetworkService.LocalPlayerId;
        }

        // Drops the probe reliable command, accepts everything else.
        private sealed class DropProbeGate : IReliableCommandEntitlementGate
        {
            public int ProbeChecks;

            public ReliableCommandVerdict Check(int playerId, byte[] entitlement, ICommand command)
            {
                if (!(command is InboundProbeReliableCommand))
                    return ReliableCommandVerdict.Accept();
                ProbeChecks++;
                return ReliableCommandVerdict.Drop();
            }
        }

        private const int Ghost = 77;

        private LogCapture _log;
        private CommandFactory _commandFactory;
        private Peer _server;
        private readonly List<Peer> _clients = new List<Peer>();

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _log = new LogCapture();
            _commandFactory = new CommandFactory();
            _clients.Clear();
            _server = null;
        }

        [TearDown]
        public void TearDown() => TestTransport.Reset();

        // ── fixture ──

        private void CreateServer(int maxPlayers = 4)
        {
            var peer = new Peer { Transport = new TestTransport(), Simulation = new TestSimulation() };
            var service = new ServerNetworkService();
            service.Initialize(peer.Transport, _commandFactory, _log);
            peer.NetworkService = service;
            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    UsePrediction = false,
                    InputDelayTicks = 1,
                    HardToleranceMs = 200,
                },
                new SessionConfig { CountdownDurationMs = 0 });
            peer.Engine.Initialize(peer.Simulation, service, _log);
            peer.Engine.SetCommandFactory(_commandFactory);
            service.SubscribeEngine(peer.Engine);
            service.CreateRoom("test", maxPlayers);
            service.Listen("localhost", 7777, maxPlayers);
            _server = peer;
        }

        private Peer AddClient()
        {
            var peer = new Peer { Transport = new TestTransport(), Simulation = new TestSimulation() };
            var service = new ServerDrivenClientService();
            service.Initialize(peer.Transport, _commandFactory, _log);
            peer.NetworkService = service;
            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    UsePrediction = true,
                    InputDelayTicks = 1,
                    HardToleranceMs = 0,
                },
                new SessionConfig { CountdownDurationMs = 0 });
            peer.Engine.Initialize(peer.Simulation, service, _log);
            peer.Engine.SetCommandFactory(_commandFactory);
            service.SubscribeEngine(peer.Engine);
            service.JoinRoom("test");
            service.Connect("localhost", 7777);
            _clients.Add(peer);
            PumpMessages();
            return peer;
        }

        private void PumpMessages(int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                foreach (var client in _clients)
                    if (client.Transport.IsConnected)
                        client.NetworkService.Update();
            }
        }

        private void StartPlaying()
        {
            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();
            Assert.AreEqual(SessionPhase.Playing, _server.NetworkService.Phase, "precondition: the server is Playing");
            foreach (var client in _clients)
                Assert.AreEqual(SessionPhase.Playing, client.NetworkService.Phase, "precondition: every client is Playing");
        }

        private void Tick(int count = 1, float deltaTime = 0.05f)
        {
            for (int n = 0; n < count; n++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                if (_server.NetworkService.Phase == SessionPhase.Playing)
                    _server.Engine.Update(deltaTime);
                foreach (var client in _clients)
                {
                    if (!client.Transport.IsConnected) continue;
                    client.NetworkService.Update();
                    if (client.NetworkService.Phase == SessionPhase.Playing)
                        client.Engine.Update(deltaTime);
                }
            }
        }

        private static List<int> Roster(KlothoEngine engine)
            => (List<int>)typeof(KlothoEngine).GetField("_activePlayerIds", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);

        // What a client receives in verified states: (tick, command type, issuing player, joined player for a join).
        private sealed class VerifiedLog
        {
            public readonly List<(int Tick, Type Type, int PlayerId, int Joined)> Entries = new List<(int, Type, int, int)>();
            public int LastTick = -1;

            public bool Contains(Type type, int playerId)
                => Entries.Exists(e => e.Type == type && e.PlayerId == playerId);

            public bool ContainsJoinOf(int joined)
                => Entries.Exists(e => e.Type == typeof(PlayerJoinCommand) && e.Joined == joined);
        }

        private static VerifiedLog Watch(Peer client)
        {
            var log = new VerifiedLog();
            ((ServerDrivenClientService)client.NetworkService).OnVerifiedStateReceived += (tick, commands, hash) =>
            {
                log.LastTick = Math.Max(log.LastTick, tick);
                for (int i = 0; i < commands.Count; i++)
                    log.Entries.Add((tick, commands[i].GetType(), commands[i].PlayerId,
                        commands[i] is PlayerJoinCommand join ? join.JoinedPlayerId : -1));
            };
            return log;
        }

        private (Peer sender, Peer other, VerifiedLog otherSees) Playing()
        {
            CreateServer();
            var sender = AddClient();
            var other = AddClient();
            StartPlaying();
            Tick(10);
            return (sender, other, Watch(other));
        }

        private void AssertSenderCutOff_OthersUnaffected(Peer sender, Peer other, VerifiedLog otherSees, int drops, int sentAtTick)
        {
            Assert.AreEqual(1, _server.Transport.DisconnectPeerCallCount - drops, "the server disconnects the sender");
            Assert.IsFalse(sender.Transport.IsConnected, "the sender is cut off");
            Assert.Greater(otherSees.LastTick, sentAtTick + 10, "the other client keeps receiving verified ticks");
            CollectionAssert.DoesNotContain(Roster(_server.Engine), Ghost, "no ghost in the server's roster");
            CollectionAssert.DoesNotContain(Roster(other.Engine), Ghost, "no ghost in the other client's roster");
        }

        // ── a client sends a command kind its path does not carry ──

        [Test]
        public void Client_PlayerJoinThroughItsInput_IsDisconnected_AndNoOtherNodeSeesIt()
        {
            var (sender, other, otherSees) = Playing();
            int drops = _server.Transport.DisconnectPeerCallCount, sentAt = _server.Engine.CurrentTick;

            sender.Engine.InputCommand(new PlayerJoinCommand { JoinedPlayerId = Ghost });
            Tick(40);

            AssertSenderCutOff_OthersUnaffected(sender, other, otherSees, drops, sentAt);
            Assert.IsFalse(otherSees.ContainsJoinOf(Ghost), "the forged join never reaches a verified state");
        }

        [Test]
        public void Client_PlayerJoinThroughReliableSubmit_IsDisconnected_AndNoOtherNodeSeesIt()
        {
            var (sender, other, otherSees) = Playing();
            int drops = _server.Transport.DisconnectPeerCallCount, sentAt = _server.Engine.CurrentTick;

            ((ServerDrivenClientService)sender.NetworkService).SendReliableCommand(
                new PlayerJoinCommand { PlayerId = sender.LocalPlayerId, JoinedPlayerId = Ghost });
            Tick(40);

            AssertSenderCutOff_OthersUnaffected(sender, other, otherSees, drops, sentAt);
            Assert.IsFalse(otherSees.ContainsJoinOf(Ghost), "the forged join never reaches a verified state");
        }

        // A reliable command in the per-tick input would skip the entitlement gate, which runs on ReliableCommandSubmit only.
        [Test]
        public void Client_ReliableCommandThroughItsInput_IsDisconnected_SoTheGateCannotBeSkipped()
        {
            var (sender, other, otherSees) = Playing();
            ((ServerNetworkService)_server.NetworkService).SetReliableCommandEntitlementGate(new DropProbeGate());
            int drops = _server.Transport.DisconnectPeerCallCount, sentAt = _server.Engine.CurrentTick;

            sender.Engine.InputCommand(new InboundProbeReliableCommand { SequenceNumber = 1 });
            Tick(40);

            AssertSenderCutOff_OthersUnaffected(sender, other, otherSees, drops, sentAt);
            Assert.IsFalse(otherSees.Contains(typeof(InboundProbeReliableCommand), sender.LocalPlayerId),
                "the gated command never runs");
        }

        // ── an honest reliable command ──

        [Test]
        public void Client_ReliableCommandThroughIssueOnce_RunsAndReachesTheOtherClient()
        {
            var (sender, other, otherSees) = Playing();
            int drops = _server.Transport.DisconnectPeerCallCount;

            sender.Engine.IssueOnce(() => new InboundProbeReliableCommand());
            Tick(40);

            Assert.AreEqual(0, _server.Transport.DisconnectPeerCallCount - drops, "an honest reliable command does not disconnect");
            Assert.IsTrue(sender.Transport.IsConnected);
            Assert.IsTrue(otherSees.Contains(typeof(InboundProbeReliableCommand), sender.LocalPlayerId),
                "the server ran it and the other client received it");
        }

        [Test]
        public void Client_ReliableCommandThroughIssueOnce_IsStillGated()
        {
            var (sender, other, otherSees) = Playing();
            var gate = new DropProbeGate();
            ((ServerNetworkService)_server.NetworkService).SetReliableCommandEntitlementGate(gate);
            int drops = _server.Transport.DisconnectPeerCallCount;

            sender.Engine.IssueOnce(() => new InboundProbeReliableCommand());
            Tick(40);

            Assert.GreaterOrEqual(gate.ProbeChecks, 1, "the gate is consulted on ReliableCommandSubmit");
            Assert.AreEqual(0, _server.Transport.DisconnectPeerCallCount - drops, "a gated command is dropped, not disconnected");
            Assert.IsFalse(otherSees.Contains(typeof(InboundProbeReliableCommand), sender.LocalPlayerId), "the dropped command never runs");
        }
    }
}
