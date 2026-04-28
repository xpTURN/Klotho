using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Tests.Integration
{
    /// <summary>
    /// ServerDriven performance measurement tests (Section 7.3).
    /// Basic benchmarks under a TestTransport environment rather than actual network.
    /// </summary>
    [TestFixture]
    public class ServerDrivenBenchmarkTests
    {
        private ILogger _logger;
        private CommandFactory _commandFactory;
        private MessageSerializer _serializer;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SDBenchmark");
            _commandFactory = new CommandFactory();
            _serializer = new MessageSerializer();
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── #1 Bandwidth.VerifiedState ──────────────────────

        /// <summary>
        /// Measure VerifiedStateMessage serialization size.
        /// Byte count based on 2P EmptyCommand.
        /// </summary>
        [Test]
        public void Bandwidth_VerifiedStateMessageSize()
        {
            // Serialize 2P EmptyCommand
            var commands = new List<ICommand>
            {
                new EmptyCommand(0, 0),
                new EmptyCommand(1, 0)
            };

            int dataSize = _commandFactory.GetSerializedCommandsSize(commands);
            byte[] buf = new byte[dataSize];
            _commandFactory.SerializeCommandsTo(buf);

            var msg = new VerifiedStateMessage
            {
                Tick = 100,
                StateHash = 0x123456789ABCDEF0L,
                ConfirmedInputsData = buf,
                ConfirmedInputsDataLength = dataSize
            };

            byte[] serialized = _serializer.Serialize(msg);

            UnityEngine.Debug.Log(
                $"[Bandwidth] VerifiedStateMessage: {serialized.Length} bytes " +
                $"(header={1 + 4 + 8}B, inputData={dataSize}B, 2P EmptyCommand)");

            // Verify reasonable size (under 100 bytes)
            Assert.Less(serialized.Length, 100,
                "VerifiedStateMessage for 2P EmptyCommand should be under 100 bytes");
        }

        /// <summary>
        /// Estimate total VerifiedState bandwidth based on 100 ticks × 2P.
        /// </summary>
        [Test]
        public void Bandwidth_VerifiedState100Ticks2P()
        {
            var commands = new List<ICommand>
            {
                new EmptyCommand(0, 0),
                new EmptyCommand(1, 0)
            };

            int dataSize = _commandFactory.GetSerializedCommandsSize(commands);
            byte[] buf = new byte[dataSize];
            _commandFactory.SerializeCommandsTo(buf);

            var msg = new VerifiedStateMessage
            {
                Tick = 0,
                StateHash = 12345L,
                ConfirmedInputsData = buf,
                ConfirmedInputsDataLength = dataSize
            };

            byte[] serialized = _serializer.Serialize(msg);
            int totalBytes = serialized.Length * 100;
            float kbPerSecond = totalBytes / 1024f * (1000f / (100 * 50)); // 100 ticks @ 50ms = 5s

            UnityEngine.Debug.Log(
                $"[Bandwidth] 100ticks x 2P: {totalBytes} bytes total, ~{kbPerSecond:F1} KB/s @ 20Hz");

            Assert.Greater(totalBytes, 0);
        }

        // ── #3 Latency.InputToConfirm ───────────────────────

        /// <summary>
        /// Measure tick delay from input send to receiving server confirmation (TestTransport environment).
        /// </summary>
        [Test]
        public void Latency_InputToConfirmTickDelay()
        {
            var server = CreateServer();
            var client = AddClient(server);
            AddClient(server);
            StartPlaying(server);

            var clientService = (ServerDrivenClientService)client.NetworkService;

            int inputSendTick = 0;
            int confirmReceivedTick = -1;

            clientService.OnInputAckReceived += ackedTick =>
            {
                if (confirmReceivedTick < 0)
                    confirmReceivedTick = server.CurrentTick;
            };

            // Send input
            inputSendTick = server.CurrentTick;
            var cmd = new EmptyCommand(client.NetworkService.LocalPlayerId, 0);
            clientService.SendClientInput(0, cmd);

            // Pump until server tick + client reception
            for (int i = 0; i < 10; i++)
            {
                server.Transport.PollEvents();
                server.NetworkService.Update();
                server.Engine.Update(0.05f);
                client.NetworkService.Update();
                if (client.Phase == SessionPhase.Playing)
                    client.Engine.Update(0.05f);
            }

            int tickDelay = confirmReceivedTick >= 0 ? confirmReceivedTick - inputSendTick : -1;
            UnityEngine.Debug.Log(
                $"[Latency] Input->Confirm: {tickDelay} ticks " +
                $"(sendTick={inputSendTick}, confirmTick={confirmReceivedTick})");

            Assert.GreaterOrEqual(confirmReceivedTick, 0,
                "Should have received InputAck");
        }

        // ── #4 CPU.ServerTick ───────────────────────────────

        /// <summary>
        /// Measure CPU cost of server tick execution (TestSimulation, 2P/4P).
        /// </summary>
        [Test]
        public void CPU_ServerTick_2P()
        {
            var server = CreateServer();
            AddClient(server);
            AddClient(server);
            StartPlaying(server);

            MeasureServerTickCost(server, 1000, "2P");
        }

        [Test]
        public void CPU_ServerTick_4P()
        {
            var server = CreateServer(8);
            AddClient(server);
            AddClient(server);
            AddClient(server);
            AddClient(server);
            StartPlaying(server);

            MeasureServerTickCost(server, 1000, "4P");
        }

        private void MeasureServerTickCost(SDTestPeer server, int ticks, string label)
        {
            // Warm up
            for (int i = 0; i < 100; i++)
            {
                server.NetworkService.Update();
                server.Engine.Update(0.05f);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++)
            {
                server.NetworkService.Update();
                server.Engine.Update(0.05f);
            }
            sw.Stop();

            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
            UnityEngine.Debug.Log(
                $"[CPU] ServerTick {label}: {usPerTick:F1}μs/tick ({ticks} ticks in {sw.ElapsedMilliseconds}ms)");

            // 1 tick < 1ms (based on TestSimulation)
            Assert.Less(usPerTick, 1000, $"ServerTick should be under 1ms for {label}");
        }

        // ── #5 Memory.ServerSnapshot ────────────────────────

        /// <summary>
        /// Server snapshot memory: measure FullState size of TestSimulation.
        /// </summary>
        [Test]
        public void Memory_ServerSnapshotSize()
        {
            var sim = new TestSimulation();
            sim.Initialize();

            var (data, hash) = sim.SerializeFullStateWithHash();
            int snapshotBytes = data.Length;

            UnityEngine.Debug.Log(
                $"[Memory] TestSimulation snapshot: {snapshotBytes} bytes, hash=0x{hash:X16}");

            // TestSimulation is 8 bytes (long StateHash)
            Assert.AreEqual(8, snapshotBytes,
                "TestSimulation snapshot should be 8 bytes (long hash)");
        }

        // ── Helpers ────────────────────────────────────────

        private List<SDTestPeer> _benchClients = new List<SDTestPeer>();
        private SDTestPeer _benchServer;

        private SDTestPeer CreateServer(int maxPlayers = 4)
        {
            var peer = new SDTestPeer
            {
                Transport = new TestTransport(),
                Simulation = new TestSimulation(),
                IsServer = true
            };

            var serverService = new ServerNetworkService();
            serverService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.NetworkService = serverService;
            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    InputDelayTicks = 0,
                    HardToleranceMs = 200,
                },
                new SessionConfig());
            peer.Engine.Initialize(peer.Simulation, serverService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            serverService.SubscribeEngine(peer.Engine);
            serverService.CreateRoom("bench", maxPlayers);
            serverService.Listen("localhost", 7777, maxPlayers);

            _benchServer = peer;
            _benchClients.Clear();
            return peer;
        }

        private SDTestPeer AddClient(SDTestPeer server)
        {
            var peer = new SDTestPeer
            {
                Transport = new TestTransport(),
                Simulation = new TestSimulation(),
                IsServer = false
            };

            var clientService = new ServerDrivenClientService();
            clientService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.NetworkService = clientService;
            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    InputDelayTicks = 0,
                },
                new SessionConfig());
            peer.Engine.Initialize(peer.Simulation, clientService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            clientService.SubscribeEngine(peer.Engine);
            clientService.JoinRoom("bench");
            clientService.Connect("localhost", 7777);

            _benchClients.Add(peer);

            for (int i = 0; i < 12; i++)
            {
                server.Transport.PollEvents();
                server.NetworkService.Update();
                foreach (var c in _benchClients)
                    c.NetworkService.Update();
            }

            return peer;
        }

        private void StartPlaying(SDTestPeer server)
        {
            foreach (var c in _benchClients)
                c.NetworkService.SetReady(true);

            for (int i = 0; i < 12; i++)
            {
                server.Transport.PollEvents();
                server.NetworkService.Update();
                foreach (var c in _benchClients)
                    c.NetworkService.Update();
            }
        }
    }
}
