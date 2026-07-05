using System;
using System.IO;
using System.Threading;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.BrawlerDedicatedServer;
using xpTURN.Klotho.BrawlerDedicatedServer.Tests;
using Brawler;                            // BrawlerPlayerConfigEntitlementGuard (game ECS, namespace Brawler)
using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend
using xpTURN.Klotho.Samples.Identity.Sd;  // SdDevIdentity, LiteNetLibLobbyRedeemClient, SdRoomReporter

const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

// Force-load the split Klotho/game assemblies and run JIT warmups before any factory
// is constructed (see KlothoServerBootstrap for why this is required).
KlothoServerBootstrap.Initialize("Brawler");

// ── CLI parsing ──
// Single room: dotnet run -- <port> <botCount> [logLevel]
// Multi-room:  dotnet run -- --multi <port> <maxRooms> <botCount> [logLevel]
// Test:        dotnet run -- --test
// Config:      dotnet run -- --config-dir <dir> ...  (auto-discovered from CWD or bin directory if not specified)
// Flags:       --rtt-metrics  (enable RTT metrics for match identification)
//              --advertise <host>  (game-server address the lobby hands to clients; default is the
//                                   dev loopback constant, which only works when client and server
//                                   share a machine — set the reachable LAN/public address for remote clients)
bool isTest = args.Length > 0 && args[0] == "--test";
bool multiRoom = args.Length > 0 && args[0] == "--multi";
bool rttMetricsEnabled = Array.IndexOf(args, "--rtt-metrics") >= 0;

if (isTest)
{
    int failures = 0;
    failures += SafeRunSuite("MultiRoomTests", MultiRoomTests.RunAll);
    failures += SafeRunSuite("SingleRoomLifecycleTests", SingleRoomLifecycleTests.RunAll);
    failures += SafeRunSuite("NormalEndLifecycleTests", NormalEndLifecycleTests.RunAll);
    return failures;
}
else if (multiRoom)
    RunMultiRoom(args, rttMetricsEnabled);
else
    RunSingleRoom(args, rttMetricsEnabled);
return 0;

static int SafeRunSuite(string name, Func<int> run)
{
    try { return run(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{name}] crashed: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

// ═══════════════════════════════════════════════════════════
// Single room — RoomManager-based (MaxRooms=1, lazy CreateRoom via RoomRouter)
// ═══════════════════════════════════════════════════════════
static void RunSingleRoom(string[] args, bool rttMetricsEnabled)
{
    int port = args.Length > 0 ? int.Parse(args[0]) : 7777;
    int botCount = args.Length > 1 ? int.Parse(args[1]) : 0;
    const int maxRooms = 1;

    var staticColliderPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
    var navMeshPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
    var assetPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");

    var logLevel = args.Length > 2 ? Enum.Parse<KLogLevel>(args[2]) : KLogLevel.Warning;
    using var loggerFactory = CreateLoggerFactory(logLevel);
    var logger = loggerFactory.CreateLogger("Server");
    
#if DEBUG || DEVELOPMENT_BUILD
    CommandPool.SetDiagnosticLogger(logger);
    EventPool.SetDiagnosticLogger(logger);
#endif

    // Load config
    var simConfig = SimulationConfigLoader.Load(args, logger);
    var sessionConfig = SessionConfigLoader.Load(args, logger);
#if KLOTHO_FAULT_INJECTION
    xpTURN.Klotho.Diagnostics.FaultInjectionLoader.TryLoadAndApply(
        ConfigPathResolver.Resolve(xpTURN.Klotho.Diagnostics.FaultInjectionLoader.DefaultFileName, args), logger);
#endif
    int tickIntervalMs = simConfig.TickIntervalMs;
    var maxPlayersPerRoom = sessionConfig.MaxPlayers;
    var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

    // RTT metrics (match identification)
    ServerNetworkService.RttMetricsEnabled = rttMetricsEnabled;

    // Pre-load data
    var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
    var navMeshBytes = File.ReadAllBytes(navMeshPath);
    var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(dataAssets);
    var sharedRegistry = registryBuilder.Build();

    // Single Transport
    var transport = new LiteNetLibTransport(logger, connectionKey: KLOTHO_CONNECTION_KEY);
    if (!transport.Listen("0.0.0.0", port, maxRooms * (maxPlayersPerRoom + maxSpectatorsPerRoom)))
    {
        logger.KError($"[BrawlerDedicatedServer] Failed to bind port {port} — exiting.");
        Environment.Exit(1);
    }

    // RoomRouter + RoomManager (MaxRooms=1, room is created lazily on first RoomHandshakeMessage)
    var router = new RoomRouter(transport, logger);
    var roomManagerConfig = new RoomManagerConfigBuilder((roomLogger) => new BrawlerServerCallbacks(roomLogger,
            staticColliders,
            FPNavMeshSerializer.Deserialize(navMeshBytes),
            maxPlayersPerRoom,
            botCount))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        .WithSessionConfig(sessionConfig)
        .WithDerivedSimulation(sharedRegistry)
        .Build();
    // Entitlement guard — server-side cross-check of each client's BrawlerPlayerConfig against
    // the account's owned set, clamping unowned picks. Inert until a lobby/validator populates the per-player
    // entitlement (no lobby wired here → entitlement null → every selection passes, opt-in off behaviour).
    roomManagerConfig.PlayerConfigEntitlementGuard = new BrawlerPlayerConfigEntitlementGuard();
    // In-match reliable-command gate — server-side cross-check of each client's UseConsumableCommand
    // against the account's owned set, dropping an unowned use before it reaches a tick. Inert until a
    // lobby/validator populates the per-player entitlement (no lobby → entitlement null → every command
    // accepted, opt-in off behaviour).
    roomManagerConfig.ReliableCommandEntitlementGate = new BrawlerReliableCommandEntitlementGate();
    // Dev lobby identity validator (SD): enabled at RUNTIME by the --lobby host:port flag (no compile
    // define). Absent → no validator (lobby off; clients join ticketless). Run DevLobbyServer first. The
    // redeem response also carries the account entitlement, which flows into the entitlement guard above.
    var (lobbyEnabled, lobbyHost, lobbyPort) = ParseLobbyEndpoint(args);
    LiteNetLibLobbyRedeemClient redeemClient = null;
    if (lobbyEnabled)
    {
        redeemClient = new LiteNetLibLobbyRedeemClient(logger, lobbyHost, lobbyPort);
        roomManagerConfig.IdentityValidator = SdDevIdentity.CreateValidator(
            Ed25519Backends.Default, SdDevIdentity.PublicKey, redeemClient,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        logger.KInformation($"[BrawlerDedicatedServer] identity validator active — dev lobby {lobbyHost}:{lobbyPort}, serverId={SdDevIdentity.DevServerId}");
    }
    var roomManager = new RoomManager(transport, router, loggerFactory, roomManagerConfig);

    logger.KInformation(
        $"[BrawlerDedicatedServer] Server listening on port {port}, maxPlayers={maxPlayersPerRoom}, maxSpectators={maxSpectatorsPerRoom}, botCount={botCount}, tickInterval={tickIntervalMs}ms");

    // Dedi → lobby room reporting (P1): advertise capacity (serverRegister) + push room occupancy (roomReport).
    SdRoomReporter roomReporter = null;
    if (lobbyEnabled)
    {
        string advertiseHost = ParseAdvertiseHost(args);
        roomReporter = new SdRoomReporter(roomManager, logger, lobbyHost, lobbyPort,
            SdDevIdentity.DevServerId, advertiseHost, port,
            maxRooms, maxPlayersPerRoom, SdDevIdentity.RoomReportIntervalMs);
        roomReporter.Start();
        logger.KInformation($"[BrawlerDedicatedServer] room reporter active — advertising {advertiseHost}:{port} {maxRooms}x{maxPlayersPerRoom} to lobby {lobbyHost}:{lobbyPort}");
    }

    // Main loop (includes Graceful Shutdown)
    var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
    loop.Run();

    roomReporter?.Dispose();
    redeemClient?.Dispose();
    logger.KInformation($"[BrawlerDedicatedServer] Server stopped.");
}

// ═══════════════════════════════════════════════════════════
// Multi-room
// ═══════════════════════════════════════════════════════════
static void RunMultiRoom(string[] args, bool rttMetricsEnabled)
{
    // dotnet run -- --multi <port> <maxRooms> <botCount> [logLevel]
    int port = args.Length > 1 ? int.Parse(args[1]) : 7777;
    int maxRooms = args.Length > 2 ? int.Parse(args[2]) : 4;
    int botCount = args.Length > 3 ? int.Parse(args[3]) : 0;

    var staticColliderPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
    var navMeshPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
    var assetPath = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");

    var logLevel = args.Length > 4 ? Enum.Parse<KLogLevel>(args[4]) : KLogLevel.Warning;
    using var loggerFactory = CreateLoggerFactory(logLevel);
    var logger = loggerFactory.CreateLogger("Server");
#if DEBUG || DEVELOPMENT_BUILD
    CommandPool.SetDiagnosticLogger(logger);
    EventPool.SetDiagnosticLogger(logger);
#endif

    // Load config
    var simConfig = SimulationConfigLoader.Load(args, logger);
    var sessionConfig = SessionConfigLoader.Load(args, logger);
#if KLOTHO_FAULT_INJECTION
    xpTURN.Klotho.Diagnostics.FaultInjectionLoader.TryLoadAndApply(
        ConfigPathResolver.Resolve(xpTURN.Klotho.Diagnostics.FaultInjectionLoader.DefaultFileName, args), logger);
#endif
    int tickIntervalMs = simConfig.TickIntervalMs;
    var maxPlayersPerRoom = sessionConfig.MaxPlayers;
    var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

    // RTT metrics (match identification)
    ServerNetworkService.RttMetricsEnabled = rttMetricsEnabled;

    // Pre-load data — shared across rooms (read-only)
    var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
    var navMeshBytes = File.ReadAllBytes(navMeshPath);
    var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(dataAssets);
    var sharedRegistry = registryBuilder.Build();

    // Guarantee ThreadPool minimum threads
    int minWorker = Math.Max(Environment.ProcessorCount, maxRooms + 2);
    ThreadPool.SetMinThreads(minWorker, Environment.ProcessorCount);

    // Single Transport (one port)
    var transport = new LiteNetLibTransport(logger, connectionKey: KLOTHO_CONNECTION_KEY);
    if (!transport.Listen("0.0.0.0", port, maxRooms * (maxPlayersPerRoom + maxSpectatorsPerRoom)))
    {
        logger.KError($"[BrawlerDedicatedServer] Failed to bind port {port} — exiting.");
        Environment.Exit(1);
    }

    // RoomRouter + RoomManager
    var router = new RoomRouter(transport, logger);
    var roomManagerConfig = new RoomManagerConfigBuilder((roomLogger) => new BrawlerServerCallbacks(roomLogger,
            staticColliders,
            FPNavMeshSerializer.Deserialize(navMeshBytes),
            maxPlayersPerRoom,
            botCount))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        .WithSessionConfig(sessionConfig)
        .WithDerivedSimulation(sharedRegistry)
        .Build();
    // Entitlement guard — server-side cross-check of each client's BrawlerPlayerConfig against
    // the account's owned set, clamping unowned picks. Inert until a lobby/validator populates the per-player
    // entitlement (no lobby wired here → entitlement null → every selection passes, opt-in off behaviour).
    roomManagerConfig.PlayerConfigEntitlementGuard = new BrawlerPlayerConfigEntitlementGuard();
    // In-match reliable-command gate — server-side cross-check of each client's UseConsumableCommand
    // against the account's owned set, dropping an unowned use before it reaches a tick. Inert until a
    // lobby/validator populates the per-player entitlement (no lobby → entitlement null → every command
    // accepted, opt-in off behaviour).
    roomManagerConfig.ReliableCommandEntitlementGate = new BrawlerReliableCommandEntitlementGate();
    // Dev lobby identity validator (SD): enabled at RUNTIME by the --lobby host:port flag (no compile
    // define). Absent → no validator (lobby off; clients join ticketless). Run DevLobbyServer first. The
    // redeem response also carries the account entitlement, which flows into the entitlement guard above.
    var (lobbyEnabled, lobbyHost, lobbyPort) = ParseLobbyEndpoint(args);
    LiteNetLibLobbyRedeemClient redeemClient = null;
    if (lobbyEnabled)
    {
        redeemClient = new LiteNetLibLobbyRedeemClient(logger, lobbyHost, lobbyPort);
        roomManagerConfig.IdentityValidator = SdDevIdentity.CreateValidator(
            Ed25519Backends.Default, SdDevIdentity.PublicKey, redeemClient,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        logger.KInformation($"[BrawlerDedicatedServer] identity validator active — dev lobby {lobbyHost}:{lobbyPort}, serverId={SdDevIdentity.DevServerId}");
    }
    var roomManager = new RoomManager(transport, router, loggerFactory, roomManagerConfig);

    logger.KInformation(
        $"[BrawlerDedicatedServer] Server listening on port {port}, maxRooms={maxRooms}, maxPlayersPerRoom={maxPlayersPerRoom}, botCount={botCount}, tickInterval={tickIntervalMs}ms");

    // Dedi → lobby room reporting (P1): advertise capacity (serverRegister) + push room occupancy (roomReport).
    SdRoomReporter roomReporter = null;
    if (lobbyEnabled)
    {
        string advertiseHost = ParseAdvertiseHost(args);
        roomReporter = new SdRoomReporter(roomManager, logger, lobbyHost, lobbyPort,
            SdDevIdentity.DevServerId, advertiseHost, port,
            maxRooms, maxPlayersPerRoom, SdDevIdentity.RoomReportIntervalMs);
        roomReporter.Start();
        logger.KInformation($"[BrawlerDedicatedServer] room reporter active — advertising {advertiseHost}:{port} {maxRooms}x{maxPlayersPerRoom} to lobby {lobbyHost}:{lobbyPort}");
    }

    // Main loop (includes Graceful Shutdown)
    var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
    loop.Run();

    roomReporter?.Dispose();
    redeemClient?.Dispose();
    logger.KInformation($"[BrawlerDedicatedServer] Server stopped.");
}

// Dev lobby endpoint: --lobby host:port (default host localhost, port 9999). Presence of the flag ENABLES
// the SD lobby at runtime (no compile define). A named flag (not a positional arg) avoids colliding with the
// positional port/botCount/maxRooms args, which differ between single- and multi-room modes.
static (bool enabled, string host, int port) ParseLobbyEndpoint(string[] args)
{
    int i = Array.IndexOf(args, "--lobby");
    if (i < 0) return (false, "localhost", 9999);
    string host = "localhost";
    int port = 9999;
    if (i + 1 < args.Length)
    {
        var parts = args[i + 1].Split(':');
        if (parts.Length > 0 && parts[0].Length > 0) host = parts[0];
        if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
    }
    return (true, host, port);
}

// Game-server address advertised to the lobby (--advertise <host>). The lobby hands this to
// clients verbatim as the join endpoint, so it must be reachable FROM THE CLIENTS — the dev
// default (SdDevIdentity.DedicatedServerHost = loopback) only works when client and server
// share a machine. The lobby stores the self-reported value as-is (it does not substitute the
// registration connection's source address), so remote-client setups must pass this flag.
static string ParseAdvertiseHost(string[] args)
{
    int i = Array.IndexOf(args, "--advertise");
    if (i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
        return args[i + 1];
    return SdDevIdentity.DedicatedServerHost;
}

// ═══════════════════════════════════════════════════════════
// Common logger factory
// ═══════════════════════════════════════════════════════════
static IKLoggerFactory CreateLoggerFactory(KLogLevel logLevel)
{
    return KLoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(logLevel);
        builder.SetTimestampFormat("HH:mm:ss.fff"); // date dropped; hour kept. Applies to both console and file.
        builder.AddConsole();
        builder.AddRollingFile(options =>
        {
            options.FilePrefix = "Server";
            options.RollingSizeKB = 1024 * 1024;
            options.FlushMode = KFlushMode.AsyncEvent;
        });
    });
}
