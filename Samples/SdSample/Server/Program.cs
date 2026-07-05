using System;
using System.IO;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Samples.SdSample.Server;
#if KLOTHO_DEV_LOBBY
using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend
using xpTURN.Klotho.Samples.Identity.Sd;  // SdDevIdentity, LiteNetLibLobbyRedeemClient
#endif

// Force-load the split Klotho/game assemblies and run JIT warmups before any factory
// is constructed (see KlothoServerBootstrap for why this is required).
KlothoServerBootstrap.Initialize("SdSample", "xpTURN.Samples");

// CLI: dotnet run -- [port] [logLevel] [lobbyHost] [lobbyPort] [advertiseHost]
//      (default 7777 / Information / localhost / 9999 / SdDevIdentity.DedicatedServerHost)
// advertiseHost: game-server address the lobby hands to clients verbatim — must be reachable
// FROM THE CLIENTS. The dev loopback default only works when client and server share a machine.
int port = args.Length > 0 ? int.Parse(args[0]) : 7777;
var logLevel = args.Length > 1 ? Enum.Parse<KLogLevel>(args[1]) : KLogLevel.Information;
#if KLOTHO_DEV_LOBBY
string lobbyHost = args.Length > 2 ? args[2] : "localhost";
int lobbyPort = args.Length > 3 ? int.Parse(args[3]) : 9999;
string advertiseHost = args.Length > 4 && !string.IsNullOrWhiteSpace(args[4])
    ? args[4] : SdDevIdentity.DedicatedServerHost;
#endif
const int maxRooms = 2;   // multi-room; MUST match the lobby's SdDevIdentity.MaxRooms

using var loggerFactory = KLoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(logLevel);
    builder.AddConsole();
    builder.AddRollingFile(options =>
    {
        options.FilePrefix = "SdServer";
        options.RollingSizeKB = 1024 * 1024;
        options.FlushMode = KFlushMode.AsyncEvent;
    });
});
var logger = loggerFactory.CreateLogger("SdServer");

// Server-authoritative config (simulationconfig.json / sessionconfig.json).
var simConfig = SimulationConfigLoader.Load(args, logger);
var sessionConfig = SessionConfigLoader.Load(args, logger);
int tickIntervalMs = simConfig.TickIntervalMs;
int maxPlayers = sessionConfig.MaxPlayers;

// DataAsset (.bytes) baked by the Unity project, copied next to the executable under Data/.
var assetPath = Path.Combine(AppContext.BaseDirectory, "Data", "SdAssets.bytes");
var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);
IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
registryBuilder.RegisterRange(dataAssets);
var sharedRegistry = registryBuilder.Build();

var transport = new LiteNetLibTransport(logger, connectionKey: "xpTURN.SdSample");
if (!transport.Listen("0.0.0.0", port, maxRooms * maxPlayers))
{
    logger.KError($"[SdServer] Failed to bind port {port} — exiting.");
    Environment.Exit(1);
}

// RoomRouter consumes the RoomHandshakeMessage and routes peers to the room; RoomManager
// wires EcsSimulation / ServerNetworkService / KlothoEngine / CommandFactory per room internally.
var router = new RoomRouter(transport, logger);
var roomManagerConfig = new RoomManagerConfigBuilder((roomLogger) => new SdServerCallbacks(roomLogger, maxPlayers))
    .WithRoomLimits(maxRooms, maxPlayers, maxSpectatorsPerRoom: 0)
    .WithSimulationConfig(simConfig)
    .WithSessionConfig(sessionConfig)
    .WithDerivedSimulation(sharedRegistry)
    .Build();

// Identity validator — set on the config after Build() (the IdentityValidator property is the supported
// hook, so the core stays untouched). Gated on KLOTHO_DEV_LOBBY (must match the client): only the dev
// lobby endpoint and keys are dev-only; a production server wires its validator unconditionally. The
// redeem client owns a LiteNetLib connection to the external dev lobby (run DevLobbyServer first);
// production swaps in a real ILobbyRedeemClient.
#if KLOTHO_DEV_LOBBY
LiteNetLibLobbyRedeemClient redeemClient = new LiteNetLibLobbyRedeemClient(logger, lobbyHost, lobbyPort);
roomManagerConfig.IdentityValidator = SdDevIdentity.CreateValidator(
    Ed25519Backends.Default, SdDevIdentity.PublicKey, redeemClient,
    () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
logger.KInformation($"[SdServer] identity validator active — dev lobby {lobbyHost}:{lobbyPort}, serverId={SdDevIdentity.DevServerId}");
#endif

var roomManager = new RoomManager(transport, router, loggerFactory, roomManagerConfig);

logger.KInformation($"[SdServer] listening on port {port}, maxPlayers={maxPlayers}, tickInterval={tickIntervalMs}ms");

// Dedi → lobby room reporting (P1): advertise capacity (serverRegister) + push room occupancy (roomReport).
// dev-only, gated like the redeem client; advertises this dedi's actual config (D4 capacity authority).
#if KLOTHO_DEV_LOBBY
var roomReporter = new SdRoomReporter(roomManager, logger, lobbyHost, lobbyPort,
    SdDevIdentity.DevServerId, advertiseHost, port,
    maxRooms, maxPlayers, SdDevIdentity.RoomReportIntervalMs);
roomReporter.Start();
logger.KInformation($"[SdServer] room reporter active — advertising {advertiseHost}:{port} {maxRooms}x{maxPlayers} to lobby {lobbyHost}:{lobbyPort}");
#endif

var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
loop.Run();

#if KLOTHO_DEV_LOBBY
roomReporter.Dispose();
redeemClient.Dispose();
#endif
logger.KInformation($"[SdServer] Server stopped.");
