using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;
using Utf8StringInterpolation;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.BrawlerDedicatedServer;
using xpTURN.Klotho.BrawlerDedicatedServer.Tests;

const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

// ── CLI parsing ──
// Single room: dotnet run -- <port> <botCount> [logLevel]
// Multi-room:  dotnet run -- --multi <port> <maxRooms> <botCount> [logLevel]
// Test:        dotnet run -- --test
// Config:      dotnet run -- --config-dir <dir> ...  (auto-discovered from CWD or bin directory if not specified)
bool isTest = args.Length > 0 && args[0] == "--test";
bool multiRoom = args.Length > 0 && args[0] == "--multi";

if (isTest)
    return MultiRoomTests.RunAll();
else if (multiRoom)
    RunMultiRoom(args);
else
    RunSingleRoom(args);
return 0;

// ═══════════════════════════════════════════════════════════
// Single room (Phase 1)
// ═══════════════════════════════════════════════════════════
static void RunSingleRoom(string[] args)
{
    int port = args.Length > 0 ? int.Parse(args[0]) : 7777;
    int botCount = args.Length > 1 ? int.Parse(args[1]) : 0;

    var staticColliderPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
    var navMeshPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
    var assetPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");

    var logLevel = args.Length > 2 ? Enum.Parse<LogLevel>(args[2]) : LogLevel.Warning;
    using var loggerFactory = CreateLoggerFactory(logLevel);
    var logger = loggerFactory.CreateLogger("Server");

    // Load config
    var simConfig = SimulationConfigLoader.Load(args, logger);
    var sessionConfig = SessionConfigLoader.Load(args, logger);
    int tickIntervalMs = simConfig.TickIntervalMs;
    var maxPlayers = sessionConfig.MaxPlayers;
    var maxSpectators = sessionConfig.MaxSpectators;

    // Pre-load data
    var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
    var navMesh = FPNavMeshSerializer.Deserialize(navMeshPath);
    var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(dataAssets);
    var assetRegistry = registryBuilder.Build();

    var transport = new LiteNetLibTransport(logger, connectionKey: KLOTHO_CONNECTION_KEY);

    var sim = new EcsSimulation(
        maxEntities: 256,
        maxRollbackTicks: 1,
        deltaTimeMs: tickIntervalMs,
        assetRegistry: assetRegistry);

    var callbacks = new BrawlerServerCallbacks(logger,
                                                staticColliders,
                                                navMesh,
                                                maxPlayers,
                                                botCount);

    callbacks.RegisterSystems(sim);
    sim.LockAssetRegistry();

    var commandFactory = new CommandFactory();
    var networkService = new ServerNetworkService();
    networkService.Initialize(transport, commandFactory, logger);

    var engine = new KlothoEngine(simConfig, sessionConfig);
    engine.Initialize(sim, networkService, logger, callbacks);
    networkService.SubscribeEngine(engine);

    networkService.CreateRoom("default", maxPlayers);
    networkService.MaxSpectatorsPerRoom = maxSpectators;
    if (!networkService.Listen("0.0.0.0", port, maxPlayers + maxSpectators))
    {
        logger.ZLogError($"[BrawlerDedicatedServer] Failed to bind port {port} — exiting.");
        Environment.Exit(1);
    }

    logger.ZLogInformation($"[BrawlerDedicatedServer] Server listening on port {port}, maxPlayers={maxPlayers}, maxSpectators={maxSpectators}, botCount={botCount}, tickInterval={tickIntervalMs}ms");

    var loop = new DedicatedServerLoop(engine, transport, tickIntervalMs, logger);
    loop.Run();

    networkService.LeaveRoom();
    transport.Disconnect();

    logger.ZLogInformation($"[BrawlerDedicatedServer] Server stopped.");
}

// ═══════════════════════════════════════════════════════════
// Multi-room (Phase 2)
// ═══════════════════════════════════════════════════════════
static void RunMultiRoom(string[] args)
{
    // dotnet run -- --multi <port> <maxRooms> <botCount> [logLevel]
    int port = args.Length > 1 ? int.Parse(args[1]) : 7777;
    int maxRooms = args.Length > 2 ? int.Parse(args[2]) : 4;
    int botCount = args.Length > 3 ? int.Parse(args[3]) : 0;

    var staticColliderPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
    var navMeshPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
    var assetPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");

    var logLevel = args.Length > 4 ? Enum.Parse<LogLevel>(args[4]) : LogLevel.Warning;
    using var loggerFactory = CreateLoggerFactory(logLevel);
    var logger = loggerFactory.CreateLogger("Server");

    // Load config
    var simConfig = SimulationConfigLoader.Load(args, logger);
    var sessionConfig = SessionConfigLoader.Load(args, logger);
    int tickIntervalMs = simConfig.TickIntervalMs;
    var maxPlayersPerRoom = sessionConfig.MaxPlayers;
    var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

    // Pre-load data — shared across rooms (read-only)
    var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
    var navMeshBytes = File.ReadAllBytes(navMeshPath);
    var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(dataAssets);
    var sharedRegistry = registryBuilder.Build();

    // Guarantee ThreadPool minimum threads (§2.3)
    int minWorker = Math.Max(Environment.ProcessorCount, maxRooms + 2);
    ThreadPool.SetMinThreads(minWorker, Environment.ProcessorCount);

    // Single Transport (one port)
    var transport = new LiteNetLibTransport(logger, connectionKey: KLOTHO_CONNECTION_KEY);
    if (!transport.Listen("0.0.0.0", port, maxRooms * (maxPlayersPerRoom + maxSpectatorsPerRoom)))
    {
        logger.ZLogError($"[BrawlerDedicatedServer] Failed to bind port {port} — exiting.");
        Environment.Exit(1);
    }

    // RoomRouter + RoomManager
    var router = new RoomRouter(transport, logger);
    var roomManager = new RoomManager(transport, router, loggerFactory, new RoomManagerConfig
    {
        MaxRooms = maxRooms,
        MaxPlayersPerRoom = maxPlayersPerRoom,
        MaxSpectatorsPerRoom = maxSpectatorsPerRoom,
        SimulationFactory = () => new EcsSimulation(
            maxEntities: simConfig.MaxEntities,
            maxRollbackTicks: 1,
            deltaTimeMs: tickIntervalMs,
            assetRegistry: sharedRegistry),
        SimulationConfigFactory = () => simConfig,
        SessionConfigFactory = () => sessionConfig,
        CallbacksFactory = (roomLogger) => new BrawlerServerCallbacks(roomLogger,
            staticColliders,
            FPNavMeshSerializer.Deserialize(navMeshBytes),
            maxPlayersPerRoom,
            botCount),
    });

    logger.ZLogInformation(
        $"[BrawlerDedicatedServer] Server listening on port {port}, maxRooms={maxRooms}, maxPlayersPerRoom={maxPlayersPerRoom}, botCount={botCount}, tickInterval={tickIntervalMs}ms");

    // Main loop (includes Graceful Shutdown)
    var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
    loop.Run();

    logger.ZLogInformation($"[BrawlerDedicatedServer] Server stopped.");
}

// ═══════════════════════════════════════════════════════════
// Common logger factory
// ═══════════════════════════════════════════════════════════
static ILoggerFactory CreateLoggerFactory(LogLevel logLevel)
{
    return LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(logLevel);
        builder.AddZLoggerConsole();

        builder.AddZLoggerRollingFile(options =>
        {
            options.FilePathSelector = (dt, index) => $"Logs/Server_{dt:yyyy-MM-dd-HH-mm-ss}_{index:000}.log";
            options.RollingInterval = RollingInterval.Day;
            options.RollingSizeKB = 1024 * 1024;
            options.UsePlainTextFormatter(formatter =>
            {
                formatter.SetPrefixFormatter($"{0}|{1:short}|", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel));
                formatter.SetExceptionFormatter((writer, ex) => Utf8String.Format(writer, $"{ex.Message}\n{ex.StackTrace}"));
            });
        });
    });
}
