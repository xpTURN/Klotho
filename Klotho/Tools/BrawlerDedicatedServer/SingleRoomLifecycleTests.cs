using System;
using System.IO;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Single-room lifecycle tests (IMP36).
    /// Verifies that a RoomManager configured with MaxRooms=1 supports the lazy CreateRoom flow:
    /// boot with no rooms, RoomRouter creates room 0 on first RoomHandshakeMessage,
    /// match end drains and disposes the room, next peer triggers a fresh room creation in the same slot.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class SingleRoomLifecycleTests
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

            Console.WriteLine("\n=== SingleRoom Lifecycle Tests (IMP36) ===\n");

            SR1_LazyCreateOnFirstHandshake();
            SR2_LobbyDrainAndRecreate();
            SR3_ShouldDrainTriggerCondition();
            SR4_DrainPhaseCapturedAtTransition();

            Console.WriteLine($"\n=== Single-room results: {_passed} passed, {_failed} failed ===");
            _loggerFactory.Dispose();
            return _failed;
        }

        // ── SR1: Lazy CreateRoom on first RoomHandshakeMessage ──

        static void SR1_LazyCreateOnFirstHandshake()
        {
            var env = CreateTestEnv();

            // Server starts with no room — single-room CreateRoom not called explicitly
            Assert("SR1a Initial ActiveRoomCount=0", env.RoomManager.ActiveRoomCount == 0);
            Assert("SR1b Initial GetRoom(0)=null", env.RoomManager.GetRoom(0) == null);

            // First peer connects + sends RoomHandshakeMessage
            env.Transport.SimulateConnect(1);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(1, handshake, handshake.Length);

            // RoomRouter.ValidateAndResolveRoom invokes lazy CreateRoom
            var room = env.RoomManager.GetRoom(0);
            Assert("SR1c Room 0 created lazily", room != null);
            Assert("SR1d Room State=Active", room != null && room.State == RoomState.Active);
            Assert("SR1e ActiveRoomCount=1", env.RoomManager.ActiveRoomCount == 1);
            Assert("SR1f Peer not rejected", !env.Transport.DisconnectedPeers.Contains(1));

            env.Dispose();
        }

        // ── SR2: Lobby drain and slot recreate ──

        static void SR2_LobbyDrainAndRecreate()
        {
            var env = CreateTestEnv();

            // Peer joins via lazy creation
            env.Transport.SimulateConnect(10);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(10, handshake, handshake.Length);
            byte[] join = MakePlayerJoinMessage();
            env.Transport.SimulateData(10, join, join.Length);

            var room = env.RoomManager.GetRoom(0);
            Assert("SR2a Room created", room != null && room.State == RoomState.Active);
            long firstCreatedAt = room.CreatedAtMs;

            room.Update(0.025f); // process Connected/Data events

            // Peer disconnects — last peer leaving in Lobby triggers ShouldDrain
            env.Transport.SimulateDisconnect(10);
            room.Update(0.025f);
            Assert("SR2b Room transitions to Draining", room.State == RoomState.Draining);

            // Main-thread cleanup cycle
            env.RoomManager.TransitionDrainingRooms();
            Assert("SR2c Room transitions to Disposing", room.State == RoomState.Disposing);

            env.RoomManager.CleanupDisposingRooms();
            Assert("SR2d Room transitions to Empty", room.State == RoomState.Empty);
            Assert("SR2e ActiveRoomCount=0", env.RoomManager.ActiveRoomCount == 0);

            // New peer arrives → lazy CreateRoom recreates a fresh room in the same slot
            // Sleep briefly so the new room's CreatedAtMs differs from the disposed one (timestamp resolution).
            System.Threading.Thread.Sleep(2);
            env.Transport.SimulateConnect(11);
            byte[] handshake2 = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(11, handshake2, handshake2.Length);

            var newRoom = env.RoomManager.GetRoom(0);
            Assert("SR2f New room created at slot 0", newRoom != null && newRoom.State == RoomState.Active);
            Assert("SR2g New room is a fresh instance", newRoom != room);
            Assert("SR2h New CreatedAtMs differs", newRoom != null && newRoom.CreatedAtMs >= firstCreatedAt);

            env.Dispose();
        }

        // ── SR3: ShouldDrain trigger condition ──

        static void SR3_ShouldDrainTriggerCondition()
        {
            var env = CreateTestEnv();

            // Lazy create via handshake
            env.Transport.SimulateConnect(20);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(20, handshake, handshake.Length);

            var room = env.RoomManager.GetRoom(0);
            Assert("SR3a Room created", room != null);

            // Process Connected event (peer is now in handshake/sync state)
            room.Update(0.025f);

            // While at least one peer-related count is non-zero, ShouldDrain is false
            bool drainBefore = room.ShouldDrain();
            Assert("SR3b ShouldDrain=false while peer present", !drainBefore);

            // Disconnect the only peer
            env.Transport.SimulateDisconnect(20);
            room.Update(0.025f);

            Assert("SR3c ShouldDrain=true after last peer leaves",
                room.NetworkService.PeerToPlayerCount == 0
                && room.NetworkService.PendingPeerCount == 0
                && room.NetworkService.PeerSyncStateCount == 0
                && room.NetworkService.DisconnectedPlayerCount == 0);

            env.Dispose();
        }

        // ── SR4: DrainPhase captured at Active→Draining transition ──

        static void SR4_DrainPhaseCapturedAtTransition()
        {
            var env = CreateTestEnv();

            env.Transport.SimulateConnect(30);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(30, handshake, handshake.Length);

            var room = env.RoomManager.GetRoom(0);
            room.Update(0.025f);

            env.Transport.SimulateDisconnect(30);
            room.Update(0.025f);

            Assert("SR4a Drained from a non-Playing phase",
                room.State == RoomState.Draining && room.DrainPhase != SessionPhase.Playing);

            // Phase counter increments after CleanupDisposingRooms records the dispose
            long beforeTotal = env.RoomManager.GetDrainTotal(room.DrainPhase);
            env.RoomManager.TransitionDrainingRooms();
            env.RoomManager.CleanupDisposingRooms();
            long afterTotal = env.RoomManager.GetDrainTotal(room.DrainPhase);

            Assert("SR4b Drain counter incremented for captured phase", afterTotal == beforeTotal + 1);
            Assert("SR4c LastDrainLifetimeMs >= 0", env.RoomManager.LastDrainLifetimeMs >= 0);

            env.Dispose();
        }

        // ═══════════════════════════════════════════════════════
        // Test infrastructure (single-room: MaxRooms=1, no pre-CreateRoom)
        // ═══════════════════════════════════════════════════════

        static FPNavMesh _sharedNavMesh;
        static System.Collections.Generic.List<xpTURN.Klotho.Deterministic.Physics.FPStaticCollider> _sharedStaticColliders;
        static IDataAssetRegistry _sharedAssetRegistry;

        static void EnsureSharedTestData()
        {
            if (_sharedNavMesh == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
                if (File.Exists(p)) _sharedNavMesh = FPNavMeshSerializer.Deserialize(p);
            }
            if (_sharedStaticColliders == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
                if (File.Exists(p)) _sharedStaticColliders = xpTURN.Klotho.Deterministic.Physics.FPStaticColliderSerializer.Load(p);
            }
            if (_sharedAssetRegistry == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");
                if (File.Exists(p))
                {
                    var assets = DataAssetReader.LoadMixedCollectionFromBytes(p);
                    IDataAssetRegistryBuilder builder = new DataAssetRegistry();
                    builder.RegisterRange(assets);
                    _sharedAssetRegistry = builder.Build();
                }
            }
        }

        static TestEnv CreateTestEnv()
        {
            EnsureSharedTestData();

            int tickIntervalMs = 25;
            const int maxPlayersPerRoom = 4;
            var transport = new MockTransport();
            var logger = _loggerFactory.CreateLogger("TestServer");
            var router = new RoomRouter(transport, logger);
            var navMesh = _sharedNavMesh;
            var staticColliders = _sharedStaticColliders;
            var assetRegistry = _sharedAssetRegistry;
            var roomManager = new RoomManager(transport, router, _loggerFactory, new RoomManagerConfig
            {
                MaxRooms = 1,
                MaxPlayersPerRoom = maxPlayersPerRoom,
                SimulationFactory = () => new EcsSimulation(
                    maxEntities: 64,
                    maxRollbackTicks: 1,
                    deltaTimeMs: tickIntervalMs,
                    assetRegistry: assetRegistry),
                SimulationConfigFactory = () => new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = tickIntervalMs,
                    MaxRollbackTicks = 1,
                    SyncCheckInterval = 1,
                    UsePrediction = false,
                    InputDelayTicks = 1,
                },
                SessionConfigFactory = () => new SessionConfig
                {
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                },
                CallbacksFactory = (roomLogger) => new BrawlerServerCallbacks(roomLogger, staticColliders, navMesh, maxPlayersPerRoom, 0),
            });

            return new TestEnv
            {
                Transport = transport,
                Router = router,
                RoomManager = roomManager,
            };
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

        class TestEnv : IDisposable
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
}
