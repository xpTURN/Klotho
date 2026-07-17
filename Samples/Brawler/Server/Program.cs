using System;
using System.Collections.Generic;
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
bool memReport = args.Length > 0 && args[0] == "--memreport";
bool multiRoom = args.Length > 0 && args[0] == "--multi";
bool rttMetricsEnabled = Array.IndexOf(args, "--rtt-metrics") >= 0;

if (isTest)
{
    int failures = 0;
    failures += SafeRunSuite("MultiRoomTests", MultiRoomTests.RunAll);
    failures += SafeRunSuite("SingleRoomLifecycleTests", SingleRoomLifecycleTests.RunAll);
    failures += SafeRunSuite("NormalEndLifecycleTests", NormalEndLifecycleTests.RunAll);
    failures += SafeRunSuite("BrawlerMatchConfigTests", BrawlerMatchConfigTests.RunAll);
    failures += SafeRunSuite("BrawlerMatchResultTests", BrawlerMatchResultTests.RunAll);
    return failures;
}
else if (memReport)
    return RunMemReport(args);
else if (multiRoom)
    RunMultiRoom(args, rttMetricsEnabled);
else
    RunSingleRoom(args, rttMetricsEnabled);
return 0;

// --memreport: dump the full registered component set + per-frame reservation with NO maxCount caps,
// without running a live match. Sizes the prune denylist — any registered type here that no registered
// system touches is a prune candidate. live/peak read 0. Add --pruned to apply Brawler's denylist
// (BrawlerPrunedComponents) and see the pruned layout (before/after comparison).
static int RunMemReport(string[] argv)
{
    using var loggerFactory = CreateLoggerFactory(KLogLevel.Information);
    var logger = loggerFactory.CreateLogger("MemReport");
    var simConfig = SimulationConfigLoader.Load(argv, logger);
    int[] pruned = Array.IndexOf(argv, "--pruned") >= 0 ? BrawlerPrunedComponents.ResolveTypeIds() : null;
    ComponentStorageRegistry.EnsureLayoutComputed(simConfig.MaxEntities, null, pruned);
    var frame = new Frame(simConfig.MaxEntities, logger);
    int ringHeaps = simConfig.MaxRollbackTicks + 1;
    var report = xpTURN.Klotho.ECS.Diagnostics.ComponentMemoryAnalyzer.Capture(frame, ringHeaps);
    Console.WriteLine(report.ToText());
    return 0;
}

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
    // Reservation-pruning denylist — always applied. Fail-safe: a denylist only ever prunes the
    // types it explicitly lists — currently the single MovementComponent (the one registered type no
    // Brawler system touches); every other peak=0 component is scanned by a registered engine system and
    // so stays reserved. Add to that list to prune more; no gate needed.
    simConfig.SetRuntimePrunedComponentTypeIds(BrawlerPrunedComponents.ResolveTypeIds());
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

    // Pre-load data — per-stage baked geometry (stage 1 = Stage01.*, stage 2 = Stage02.*; unmapped/0 → stage 1),
    // so a lobby-selected stage is actually simulated, not just reported. Mirrors RunMultiRoom.
    var stageColliders = new Dictionary<int, List<FPStaticCollider>>
    {
        [1] = FPStaticColliderSerializer.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.StaticColliders.bytes")),
        [2] = FPStaticColliderSerializer.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Stage02.StaticColliders.bytes")),
    };
    var stageNavBytes = new Dictionary<int, byte[]>
    {
        [1] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.NavMeshData.bytes")),
        [2] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Stage02.NavMeshData.bytes")),
    };
    List<FPStaticCollider> CollidersFor(int stageId) => stageColliders.TryGetValue(stageId, out var c) ? c : stageColliders[1];
    byte[] NavBytesFor(int stageId) => stageNavBytes.TryGetValue(stageId, out var b) ? b : stageNavBytes[1];

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

    // Match config source (built BEFORE the room manager config so it can be wired into it). --lobby: the lobby
    // pushes each room's stage (ReservePush) into a LobbyMatchConfigSource — the SAME instance is wired into
    // WithMatchConfigSource (so the pushed stage is actually simulated + the reservation materializes) AND handed
    // to the reporter as its result-key source. Lobbyless: a static room 0 → stage 1 table carrying the CLI
    // botCount as opaque MatchConfigData. (In --lobby mode the lobby is the config authority; CLI botCount is ignored.)
    var (lobbyEnabled, lobbyHost, lobbyPort) = ParseLobbyEndpoint(args);
    LobbyMatchConfigSource lobbyReservations = null;
    IMatchConfigSource matchConfigSource;
    if (lobbyEnabled)
    {
        lobbyReservations = new LobbyMatchConfigSource(maxRooms,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), logger);
        matchConfigSource = lobbyReservations;
    }
    else
    {
        var matchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = botCount });
        matchConfigSource = new StaticMatchConfigSource().Add(0, 1, matchConfigData);
    }

    // RoomRouter + RoomManager (MaxRooms=1, room is created lazily on first RoomHandshakeMessage). The stage
    // comes from the resolved MatchConfigContext (lobby push or the static table) and selects the baked
    // colliders/navmesh + stamps stageId — so the reported result's stage matches what actually ran.
    var router = new RoomRouter(transport, logger);
    var roomManagerConfig = new RoomManagerConfigBuilder((matchCtx, roomLogger) => new BrawlerServerCallbacks(roomLogger,
            CollidersFor(matchCtx.StageId),
            FPNavMeshSerializer.Deserialize(NavBytesFor(matchCtx.StageId)),
            maxPlayersPerRoom,
            BrawlerMatchConfig.Decode(matchCtx.MatchConfigData).BotCount,
            stageId: matchCtx.StageId))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        .WithSessionConfig(sessionConfig)
        .WithDerivedSimulation(sharedRegistry)
        .WithMatchConfigSource(matchConfigSource)
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
            maxRooms, maxPlayersPerRoom, SdDevIdentity.RoomReportIntervalMs,
            reservations: lobbyReservations);
        roomReporter.Start();
        // Subscribe the reporter to each room's engine/network events (result capture + identity ledger) and its
        // drain hook. Set after the reporter exists; read lazily at room creation, before any room is made (loop
        // not started). AttachRoom also wires room.OnDraining (capturing the room's ledger), so abandoned-match
        // notification and the reservation drain-release fire per room — no separate OnRoomDraining config hook.
        roomManagerConfig.OnRoomCreated = roomReporter.AttachRoom;
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
    // Reservation-pruning denylist — always applied. Fail-safe: a denylist only ever prunes the
    // types it explicitly lists — currently the single MovementComponent (the one registered type no
    // Brawler system touches); every other peak=0 component is scanned by a registered engine system and
    // so stays reserved. Add to that list to prune more; no gate needed.
    simConfig.SetRuntimePrunedComponentTypeIds(BrawlerPrunedComponents.ResolveTypeIds());
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

    // Pre-load data — shared across rooms (read-only). Per-stage baked geometry: stage 1 = Stage01.*,
    // stage 2 = Stage02.* (an unmapped/0 stageId falls back to stage 1 = the default stage).
    var stageColliders = new Dictionary<int, List<FPStaticCollider>>
    {
        [1] = FPStaticColliderSerializer.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.StaticColliders.bytes")),
        [2] = FPStaticColliderSerializer.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Stage02.StaticColliders.bytes")),
    };
    var stageNavBytes = new Dictionary<int, byte[]>
    {
        [1] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.NavMeshData.bytes")),
        [2] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Stage02.NavMeshData.bytes")),
    };
    List<FPStaticCollider> CollidersFor(int stageId) => stageColliders.TryGetValue(stageId, out var c) ? c : stageColliders[1];
    byte[] NavBytesFor(int stageId) => stageNavBytes.TryGetValue(stageId, out var b) ? b : stageNavBytes[1];

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

    // Match config source. With --lobby: the lobby pushes each room's stage (ReservePush) into a
    // LobbyMatchConfigSource (populated as clients are assigned). Lobbyless: a static room→stage table
    // (room r → stage 1+(r%2), alternating Stage01/Stage02). Both refuse unmapped/unreserved rooms →
    // CreateRoomAt returns null → RoomNotFound.
    var (lobbyEnabled, lobbyHost, lobbyPort) = ParseLobbyEndpoint(args);
    LobbyMatchConfigSource lobbyReservations = null;
    IMatchConfigSource matchConfigSource;
    if (lobbyEnabled)
    {
        lobbyReservations = new LobbyMatchConfigSource(maxRooms,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), logger);
        matchConfigSource = lobbyReservations;
    }
    else
    {
        // Lobbyless: the CLI botCount is this authority's per-match dynamic config — serialized into each
        // room's opaque MatchConfigData so it propagates like stageId (rather than injected straight into
        // the callback). (In --lobby mode the lobby is the authority for match config; CLI botCount is ignored.)
        var matchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = botCount });
        var staticSource = new StaticMatchConfigSource();
        for (int r = 0; r < maxRooms; r++) staticSource.Add(r, 1 + (r % 2), matchConfigData);
        matchConfigSource = staticSource;
    }

    // DEV (DEBUG builds only): --dev-abort-after-ms <N> → each room StateDivergence-aborts N ms into Playing,
    // to exercise the server→lobby abort-notification path without a real diverging client.
    long devAbortMs = 0;
#if DEBUG
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == "--dev-abort-after-ms" && long.TryParse(args[i + 1], out var ms) && ms > 0)
            devAbortMs = ms;
    if (devAbortMs > 0)
        logger.KWarning($"[BrawlerDedicatedServer][DEV] --dev-abort-after-ms={devAbortMs}: room will StateDivergence-abort {devAbortMs}ms into Playing");
#endif

    // RoomRouter + RoomManager — context callbacks factory: each room's stage (from the resolved
    // MatchConfigContext) selects the baked colliders/navmesh, its MatchConfigData carries the dynamic
    // knobs (botCount), and CreateRoomAt stamps both onto the room's SimulationConfig so they propagate.
    var router = new RoomRouter(transport, logger);
    var roomManagerConfig = new RoomManagerConfigBuilder((matchCtx, roomLogger) => new BrawlerServerCallbacks(roomLogger,
            CollidersFor(matchCtx.StageId),
            FPNavMeshSerializer.Deserialize(NavBytesFor(matchCtx.StageId)),
            maxPlayersPerRoom,
            BrawlerMatchConfig.Decode(matchCtx.MatchConfigData).BotCount,
            stageId: matchCtx.StageId,
            devAbortAfterMs: devAbortMs))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        .WithSessionConfig(sessionConfig)
        .WithDerivedSimulation(sharedRegistry)
        .WithMatchConfigSource(matchConfigSource)
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
    // The reporter's report client also receives ReservePush → populates lobbyReservations → replies ReserveAck.
    SdRoomReporter roomReporter = null;
    if (lobbyEnabled)
    {
        string advertiseHost = ParseAdvertiseHost(args);
        roomReporter = new SdRoomReporter(roomManager, logger, lobbyHost, lobbyPort,
            SdDevIdentity.DevServerId, advertiseHost, port,
            maxRooms, maxPlayersPerRoom, SdDevIdentity.RoomReportIntervalMs,
            reservations: lobbyReservations);
        roomReporter.Start();
        // Subscribe the reporter to each room's engine/network events (result capture + identity ledger) and its
        // drain hook. Set after the reporter exists; read lazily at room creation, before any room is made (loop
        // not started). AttachRoom also wires room.OnDraining (capturing the room's ledger), so abandoned-match
        // notification and the reservation drain-release fire per room — no separate OnRoomDraining config hook.
        roomManagerConfig.OnRoomCreated = roomReporter.AttachRoom;
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
