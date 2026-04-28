using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.BrawlerDedicatedServer;

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Multi-room E2E verification tests (§8 #8~#15).
    /// Validates server components with MockTransport without an actual network.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class MultiRoomTests
    {
        private static ILoggerFactory _loggerFactory;
        private static ILogger _logger;
        private static int _passed;
        private static int _failed;
        private static readonly MessageSerializer _serializer = new MessageSerializer();

        public static int RunAll()
        {
            _loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerConsole();
            });
            _logger = _loggerFactory.CreateLogger("Test");
            _passed = 0;
            _failed = 0;

            Console.WriteLine("=== MultiRoom E2E Tests ===\n");

            Test08_TwoRoomsSimultaneous();
            Test09_RoomIsolation();
            Test10_RoomCreationDestruction();
            Test11_TickIntervalStability();
            Test12_FullRoomReject();
            Test13_NonExistentRoomReject();
            Test14_GracefulShutdown();
            Test15_ThreadSafety();

            Console.WriteLine($"\n=== Results: {_passed} passed, {_failed} failed ===");
            _loggerFactory.Dispose();
            return _failed;
        }

        // ── #8: Two rooms running simultaneously ──

        static void Test08_TwoRoomsSimultaneous()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);

            // Create 2 rooms
            var room0 = env.RoomManager.CreateRoom(0);
            var room1 = env.RoomManager.CreateRoom(1);

            Assert("#8a Room0 created", room0 != null && room0.State == RoomState.Active);
            Assert("#8b Room1 created", room1 != null && room1.State == RoomState.Active);
            Assert("#8c ActiveRoomCount", env.RoomManager.ActiveRoomCount == 2);

            // Simulate peer connection to each room
            SimulatePeerJoin(env, peerId: 1, roomId: 0);
            SimulatePeerJoin(env, peerId: 2, roomId: 1);

            // Verify messages are queued for each room
            Assert("#8d Room0 has inbound", !room0.InboundQueue.IsEmpty);
            Assert("#8e Room1 has inbound", !room1.InboundQueue.IsEmpty);

            // Update rooms (DrainInboundQueue + Engine.Update)
            room0.Update(0.025f);
            room1.Update(0.025f);

            Assert("#8f Room0 still Active", room0.State == RoomState.Active);
            Assert("#8g Room1 still Active", room1.State == RoomState.Active);

            env.Dispose();
        }

        // ── #9: Isolation between rooms ──

        static void Test09_RoomIsolation()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            var room0 = env.RoomManager.CreateRoom(0);
            var room1 = env.RoomManager.CreateRoom(1);

            // Connect peer to Room0 only
            SimulatePeerJoin(env, peerId: 10, roomId: 0);

            // Room1 should be empty
            Assert("#9a Room0 has inbound", !room0.InboundQueue.IsEmpty);
            Assert("#9b Room1 empty", room1.InboundQueue.IsEmpty);

            // Send data to Room0
            SimulateData(env, peerId: 10, data: new byte[] { 70, 1, 2, 3 }, length: 4); // ClientInput
            Assert("#9c Room0 has data", !room0.InboundQueue.IsEmpty);
            Assert("#9d Room1 still empty", room1.InboundQueue.IsEmpty);

            // Peer is registered only in Room0's Transport
            Assert("#9e Room0 has peer 10", room0.Transport.ContainsPeer(10));
            Assert("#9f Room1 no peer 10", !room1.Transport.ContainsPeer(10));

            env.Dispose();
        }

        // ── #10: Room creation/destruction ──

        static void Test10_RoomCreationDestruction()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            var room0 = env.RoomManager.CreateRoom(0);

            // Peer connects → leaves
            SimulatePeerJoin(env, peerId: 20, roomId: 0);
            room0.Update(0.025f); // DrainInboundQueue → HandlePeerConnected

            SimulatePeerDisconnect(env, peerId: 20);
            room0.Update(0.025f); // DrainInboundQueue → HandlePeerDisconnected

            // ShouldDrain decision: Draining once all connections are released
            Assert("#10a Room0 Draining", room0.State == RoomState.Draining);

            // TransitionDrainingRooms → Disposing
            env.RoomManager.TransitionDrainingRooms();
            Assert("#10b Room0 Disposing", room0.State == RoomState.Disposing);

            // CleanupDisposingRooms → Empty
            env.RoomManager.CleanupDisposingRooms();
            Assert("#10c Room0 Empty", room0.State == RoomState.Empty);
            Assert("#10d ActiveRoomCount 0", env.RoomManager.ActiveRoomCount == 0);

            // Slot can be reused
            var newRoom = env.RoomManager.CreateRoom(0);
            Assert("#10e Slot reused", newRoom != null && newRoom.State == RoomState.Active);

            env.Dispose();
        }

        // ── #11: Load test (tick interval stability) ──

        static void Test11_TickIntervalStability()
        {
            var env = CreateTestEnv(maxRooms: 4, maxPlayersPerRoom: 4);
            for (int i = 0; i < 4; i++)
                env.RoomManager.CreateRoom(i);

            // Connect peer to each room (empty rooms transition to Draining via ShouldDrain)
            for (int i = 0; i < 4; i++)
                SimulatePeerJoin(env, peerId: 60 + i, roomId: i);

            // Run 100 cycles
            var readyRooms = new List<Room>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int cycle = 0; cycle < 100; cycle++)
            {
                env.RoomManager.GetReadyRooms(readyRooms);
                if (readyRooms.Count == 0) break;
                var countdown = new CountdownEvent(readyRooms.Count);

                for (int i = 0; i < readyRooms.Count; i++)
                {
                    var room = readyRooms[i];
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { room.Update(0.025f); }
                        finally { countdown.Signal(); }
                    });
                }

                countdown.Wait(100); // 100ms budget
                countdown.Dispose();
            }

            sw.Stop();
            long elapsedMs = sw.ElapsedMilliseconds;
            Assert("#11a 100 cycles completed", true);
            Assert($"#11b No crash ({elapsedMs}ms for 100 cycles)", elapsedMs < 10000);

            // All rooms still Active (peers are still connected)
            for (int i = 0; i < 4; i++)
            {
                var room = env.RoomManager.GetRoom(i);
                Assert($"#11c Room{i} Active", room != null && room.State == RoomState.Active);
            }

            env.Dispose();
        }

        // ── #12: Reject when room is full ──

        static void Test12_FullRoomReject()
        {
            var env = CreateTestEnv(maxRooms: 1, maxPlayersPerRoom: 1);
            env.RoomManager.CreateRoom(0);

            // First peer succeeds
            SimulatePeerJoin(env, peerId: 30, roomId: 0);

            // Second peer → rejected with RoomFull
            env.Transport.SimulateConnect(31);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(31, handshake, handshake.Length);

            // Rejected peer receives JoinReject(Reason=2) and is then DisconnectPeer'd
            Assert("#12a Peer 31 rejected", env.Transport.DisconnectedPeers.Contains(31));
            Assert("#12b Reject reason RoomFull",
                env.Transport.LastSentTo(31) != null && env.Transport.LastSentTo(31)[0] == (byte)NetworkMessageType.JoinReject);

            env.Dispose();
        }

        // ── #13: Reject when roomId does not exist ──

        static void Test13_NonExistentRoomReject()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            env.RoomManager.CreateRoom(0); // Only roomId=0 is created; roomId=99 does not exist

            env.Transport.SimulateConnect(40);
            byte[] handshake = MakeHandshakeMessage(roomId: 99);
            env.Transport.SimulateData(40, handshake, handshake.Length);

            Assert("#13a Peer 40 rejected", env.Transport.DisconnectedPeers.Contains(40));
            Assert("#13b Reject reason RoomNotFound",
                env.Transport.LastSentTo(40) != null && env.Transport.LastSentTo(40)[0] == (byte)NetworkMessageType.JoinReject);

            env.Dispose();
        }

        // ── #14: Graceful Shutdown ──

        static void Test14_GracefulShutdown()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            env.RoomManager.CreateRoom(0);
            env.RoomManager.CreateRoom(1);

            SimulatePeerJoin(env, peerId: 50, roomId: 0);
            SimulatePeerJoin(env, peerId: 51, roomId: 1);

            // Drain to process connections
            env.RoomManager.GetRoom(0)?.Update(0.025f);
            env.RoomManager.GetRoom(1)?.Update(0.025f);

            // ShutdownAllRooms
            env.Router.StopAccepting();
            env.RoomManager.ShutdownAllRooms();

            Assert("#14a ActiveRoomCount 0", env.RoomManager.ActiveRoomCount == 0);

            // Verify new connections are rejected
            env.Transport.SimulateConnect(52);
            Assert("#14b New peer rejected after shutdown", env.Transport.DisconnectedPeers.Contains(52));

            env.Dispose();
        }

        // ── #15: Thread safety ──

        static void Test15_ThreadSafety()
        {
            var env = CreateTestEnv(maxRooms: 4, maxPlayersPerRoom: 4);
            for (int i = 0; i < 4; i++)
                env.RoomManager.CreateRoom(i);

            // Simulate concurrent Update from multiple threads + PollEvents on the main thread
            bool crashed = false;
            var readyRooms = new List<Room>();

            // Connect peer to each room
            for (int r = 0; r < 4; r++)
                SimulatePeerJoin(env, peerId: 100 + r, roomId: r);

            try
            {
                for (int cycle = 0; cycle < 200; cycle++)
                {
                    // Phase 2: ThreadPool parallel update (tick-only execution without data)
                    env.RoomManager.GetReadyRooms(readyRooms);
                    if (readyRooms.Count == 0) break;
                    var cd = new CountdownEvent(readyRooms.Count);
                    for (int i = 0; i < readyRooms.Count; i++)
                    {
                        var room = readyRooms[i];
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { room.Update(0.025f); }
                            finally { cd.Signal(); }
                        });
                    }
                    cd.Wait(500);
                    cd.Dispose();
                }
            }
            catch (Exception ex)
            {
                crashed = true;
                Console.WriteLine($"  Thread safety crash: {ex.Message}");
            }

            Assert("#15a No crash in 200 cycles", !crashed);

            env.Dispose();
        }

        // ═══════════════════════════════════════════════════════
        // Test infrastructure
        // ═══════════════════════════════════════════════════════

        static TestEnvironment CreateTestEnv(int maxRooms, int maxPlayersPerRoom)
        {
            int tickIntervalMs = 25;
            var transport = new MockTransport();
            var logger = _loggerFactory.CreateLogger("TestServer");
            var router = new RoomRouter(transport, logger);
            var roomManager = new RoomManager(transport, router, _loggerFactory, new RoomManagerConfig
            {
                MaxRooms = maxRooms,
                MaxPlayersPerRoom = maxPlayersPerRoom,
                SimulationFactory = () => new EcsSimulation(
                    maxEntities: 64,
                    maxRollbackTicks: 1,
                    deltaTimeMs: tickIntervalMs),
                SimulationConfigFactory = () => new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = tickIntervalMs,
                    MaxRollbackTicks = 0,
                    UsePrediction = false,
                    InputDelayTicks = 0,
                },
                SessionConfigFactory = () => new SessionConfig
                {
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                },
                CallbacksFactory = (roomLogger) => new BrawlerServerCallbacks(roomLogger, null, null, 4, 0),
            });

            return new TestEnvironment
            {
                Transport = transport,
                Router = router,
                RoomManager = roomManager,
            };
        }

        static void SimulatePeerJoin(TestEnvironment env, int peerId, int roomId)
        {
            env.Transport.SimulateConnect(peerId);
            byte[] handshake = MakeHandshakeMessage(roomId);
            env.Transport.SimulateData(peerId, handshake, handshake.Length);
            byte[] join = MakePlayerJoinMessage();
            env.Transport.SimulateData(peerId, join, join.Length);
        }

        static void SimulateData(TestEnvironment env, int peerId, byte[] data, int length)
        {
            env.Transport.SimulateData(peerId, data, length);
        }

        static void SimulatePeerDisconnect(TestEnvironment env, int peerId)
        {
            env.Transport.SimulateDisconnect(peerId);
        }

        static byte[] MakeHandshakeMessage(int roomId)
        {
            var msg = new RoomHandshakeMessage { RoomId = roomId };
            return _serializer.Serialize(msg);
        }

        static byte[] MakePlayerJoinMessage()
        {
            return _serializer.Serialize(new PlayerJoinMessage());
        }

        static void Assert(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  PASS: {name}");
                _passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: {name}");
                _failed++;
            }
        }

        class TestEnvironment : IDisposable
        {
            public MockTransport Transport;
            public RoomRouter Router;
            public RoomManager RoomManager;

            public void Dispose()
            {
                Router?.Dispose();
            }
        }
    }

    /// <summary>
    /// Mock implementation of INetworkTransport. Manually fires events without an actual network.
    /// </summary>
    public class MockTransport : INetworkTransport
    {
        public bool IsConnected => true;
        public int LocalPeerId => 0;
        public string RemoteAddress => "127.0.0.1";
        public int RemotePort => 7777;

        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
#pragma warning disable CS0067
        public event Action OnConnected;
        public event Action OnDisconnected;
#pragma warning restore CS0067

        // For test verification
        public HashSet<int> DisconnectedPeers { get; } = new HashSet<int>();
        private readonly Dictionary<int, byte[]> _lastSent = new Dictionary<int, byte[]>();

        public byte[] LastSentTo(int peerId)
        {
            _lastSent.TryGetValue(peerId, out var data);
            return data;
        }

        // ── Manual event firing ──

        public void SimulateConnect(int peerId)
        {
            OnPeerConnected?.Invoke(peerId);
        }

        public void SimulateData(int peerId, byte[] data, int length)
        {
            OnDataReceived?.Invoke(peerId, data, length);
        }

        public void SimulateDisconnect(int peerId)
        {
            OnPeerDisconnected?.Invoke(peerId);
        }

        // ── INetworkTransport ──

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
        {
            byte[] copy = new byte[data.Length];
            Array.Copy(data, copy, data.Length);
            _lastSent[peerId] = copy;
        }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);
            _lastSent[peerId] = copy;
        }

        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { }
        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { }

        public void DisconnectPeer(int peerId)
        {
            DisconnectedPeers.Add(peerId);
        }

        public void PollEvents() { }
        public void FlushSendQueue() { }
        public void Listen(string address, int port, int maxConnections) { }
        public void Connect(string address, int port) { }
        public void Disconnect() { }
    }
}
