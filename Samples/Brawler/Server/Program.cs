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
// Verify:      dotnet run -- --verify <replay.rply> [--log=<level>]   (re-simulate a replay and compare it with the
//                                   client's claimed result; exit 0 = verified, 10 = claim mismatch,
//                                   20 = unverifiable, 30 = unreadable file — see BrawlerReplayVerifier)
// Config:      dotnet run -- --config-dir <dir> ...  (auto-discovered from CWD or bin directory if not specified)
// Flags:       --rtt-metrics  (enable RTT metrics for match identification)
//              --advertise <host>  (game-server address the lobby hands to clients; default is the
//                                   dev loopback constant, which only works when client and server
//                                   share a machine — set the reachable LAN/public address for remote clients)
bool isTest = args.Length > 0 && args[0] == "--test";
bool memReport = args.Length > 0 && args[0] == "--memreport";
bool multiRoom = args.Length > 0 && args[0] == "--multi";
bool verify = args.Length > 0 && args[0] == "--verify";
bool rttMetricsEnabled = Array.IndexOf(args, "--rtt-metrics") >= 0;

if (isTest)
{
    int failures = 0;
    failures += SafeRunSuite("MultiRoomTests", MultiRoomTests.RunAll);
    failures += SafeRunSuite("SingleRoomLifecycleTests", SingleRoomLifecycleTests.RunAll);
    failures += SafeRunSuite("NormalEndLifecycleTests", NormalEndLifecycleTests.RunAll);
    failures += SafeRunSuite("BrawlerMatchConfigTests", BrawlerMatchConfigTests.RunAll);
    failures += SafeRunSuite("BrawlerMatchResultTests", BrawlerMatchResultTests.RunAll);
    failures += SafeRunSuite("BrawlerReplayVerifierTests", BrawlerReplayVerifierTests.RunAll);
    failures += SafeRunSuite("ReplayJoinSeedTests", ReplayJoinSeedTests.RunAll);
    return failures;
}
else if (memReport)
    return RunMemReport(args);
else if (verify)
    return RunVerify(args);
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

// --verify <replay.rply>: re-simulate a recorded match in THIS build and compare the result it derives
// with the result the client claimed. Not a room server — no transport, no lobby, no threads.
static int RunVerify(string[] argv)
{
    // Warning by default: a 5,000-tick playback at Information prints the game's whole per-tick chatter.
    // `--log=<level>` opens it for a diagnostic run (e.g. `--log=Information` to read the seeded loadout
    // masks). The split lives in the verifier (ParseArgs) so the rule "a `--` token is never a path" sits
    // next to the loop that would otherwise open one as a replay; Run then receives a purely positional list.
    var parsed = BrawlerReplayVerifier.ParseArgs(argv, KLogLevel.Warning);

    // stderr, and before the factory exists: these complain about the very option that sets the level, so
    // routing them through the logger could file them under the level the caller failed to select.
    for (int i = 0; i < parsed.Warnings.Length; i++)
        Console.Error.WriteLine(parsed.Warnings[i]);

    using var loggerFactory = CreateLoggerFactory(parsed.Level);
    var logger = loggerFactory.CreateLogger("Verify");
    return BrawlerReplayVerifier.Run(parsed.Files, logger);
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
    string replaySaveDir = ParseReplaySaveDir(args);
    var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

    // RTT metrics (match identification)
    ServerNetworkService.RttMetricsEnabled = rttMetricsEnabled;

    // Baked content — one loader shared with the multi-room and --verify modes so all three read the
    // same bytes (see BrawlerStageAssets for why that matters).
    var assets = BrawlerStageAssets.Load(logger);
    List<FPStaticCollider> CollidersFor(int stageId) => assets.CollidersFor(stageId);
    FPNavMesh NavMeshFor(int stageId) => assets.NavMeshFor(stageId);
    FPNavMeshRebakeSnapshot RebakeSnapshotFor(int stageId) => assets.RebakeSnapshotFor(stageId);
    var dataAssets = assets.DataAssets;
    var sharedRegistry = assets.Registry;

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
        // MaxPlayers rides along with botCount: bot ids are numbered past it, so it is a tick-0 state input
        // and a replay verifier rebuilding tick 0 has no sessionconfig.json to read it from.
        var matchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData
        {
            BotCount = botCount,
            MaxPlayers = maxPlayersPerRoom,
        });
        matchConfigSource = new StaticMatchConfigSource().Add(0, 1, matchConfigData);
    }

    // RoomRouter + RoomManager (MaxRooms=1, room is created lazily on first RoomHandshakeMessage). The stage
    // comes from the resolved MatchConfigContext (lobby push or the static table) and selects the baked
    // colliders/navmesh + stamps stageId — so the reported result's stage matches what actually ran.
    var router = new RoomRouter(transport, logger);
    var roomManagerConfig = new RoomManagerConfigBuilder((matchCtx, roomLogger) => new BrawlerServerCallbacks(roomLogger,
            CollidersFor(matchCtx.StageId),
            NavMeshFor(matchCtx.StageId),
            // Capacity from the room's own config when it carries one, so what built the world is what the
            // replay records. A lobby-issued config does not stamp it (0) -> this server's own value.
            MaxPlayersOf(matchCtx.MatchConfigData, maxPlayersPerRoom),
            BrawlerMatchConfig.Decode(matchCtx.MatchConfigData).BotCount,
            stageId: matchCtx.StageId,
            rebakeSnapshot: RebakeSnapshotFor(matchCtx.StageId),
            replaySaveDir: replaySaveDir,
            matchInstanceId: MatchInstanceIdOf(matchCtx.MatchConfigData)))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        // Per-match factory, not the shared instance: every room gets its own SessionConfig object, which
        // is where a per-match seed goes when the lobby starts issuing one (matchCtx carries the payload).
        // Cloning today changes no value — it removes the shared mutable object and opens the seam.
        .WithSessionConfig(matchCtx => sessionConfig.Clone())
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
    string replaySaveDir = ParseReplaySaveDir(args);
    var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

    // RTT metrics (match identification)
    ServerNetworkService.RttMetricsEnabled = rttMetricsEnabled;

    // Baked content — one loader shared with the multi-room and --verify modes so all three read the
    // same bytes (see BrawlerStageAssets for why that matters).
    var assets = BrawlerStageAssets.Load(logger);
    List<FPStaticCollider> CollidersFor(int stageId) => assets.CollidersFor(stageId);
    FPNavMesh NavMeshFor(int stageId) => assets.NavMeshFor(stageId);
    FPNavMeshRebakeSnapshot RebakeSnapshotFor(int stageId) => assets.RebakeSnapshotFor(stageId);
    var dataAssets = assets.DataAssets;
    var sharedRegistry = assets.Registry;

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
        var matchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData
        {
            BotCount = botCount,
            MaxPlayers = maxPlayersPerRoom,
        });
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
            NavMeshFor(matchCtx.StageId),
            MaxPlayersOf(matchCtx.MatchConfigData, maxPlayersPerRoom),
            BrawlerMatchConfig.Decode(matchCtx.MatchConfigData).BotCount,
            stageId: matchCtx.StageId,
            replaySaveDir: replaySaveDir,
            matchInstanceId: MatchInstanceIdOf(matchCtx.MatchConfigData),
            devAbortAfterMs: devAbortMs,
            rebakeSnapshot: RebakeSnapshotFor(matchCtx.StageId)))
        .WithRoomLimits(maxRooms, maxPlayersPerRoom, maxSpectatorsPerRoom)
        .WithSimulationConfig(simConfig)
        // Per-match factory, not the shared instance: every room gets its own SessionConfig object, which
        // is where a per-match seed goes when the lobby starts issuing one (matchCtx carries the payload).
        // Cloning today changes no value — it removes the shared mutable object and opens the seam.
        .WithSessionConfig(matchCtx => sessionConfig.Clone())
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

// --save-replays <dir>: write each room's replay when its recording stops. OFF by default — the engine
// records either way, so this flag decides whether that buffer becomes an artifact or is discarded. The
// server's file is the only SD recording with a tick-0 roster, so it is the only one --verify can REBUILD.
static string ParseReplaySaveDir(string[] args)
{
    int i = Array.IndexOf(args, "--save-replays");
    if (i < 0 || i + 1 >= args.Length) return null;
    string dir = args[i + 1];
    System.IO.Directory.CreateDirectory(dir);
    return dir;
}

// The lobby's key for this match, or null when nothing issued one (lobbyless). Names the saved replay and,
// more importantly, is what joins that file to the lobby's own record of the match.
static string MatchInstanceIdOf(byte[] matchConfigData)
{
    string id = BrawlerMatchConfig.Decode(matchConfigData).MatchInstanceId.ToString();
    return string.IsNullOrEmpty(id) ? null : id;
}

// Room capacity the world was built with. The room's own MatchConfigData carries it when the issuer stamped
// one (this server's static source does); a lobby-issued config does not, and 0 means "fall back to mine".
// It matters because bot ids are numbered past maxPlayers — a room that builds its world with a different
// capacity than the one recorded cannot have its tick 0 rebuilt by --verify.
static int MaxPlayersOf(byte[] matchConfigData, int fallback)
{
    int stamped = BrawlerMatchConfig.Decode(matchConfigData).MaxPlayers;
    return stamped > 0 ? stamped : fallback;
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
