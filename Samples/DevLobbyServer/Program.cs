using System;
using System.Threading;

using xpTURN.Klotho.Logging;              // KLoggerFactory, KLogLevel, IKLogger
using xpTURN.Klotho.Network;              // DeliveryMethod
using xpTURN.Klotho.LiteNetLib;           // LiteNetLibTransport
using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.ECS;                  // FixedString64 (match-config identity budget)
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, SdDevIdentity, LobbyWire, RedeemResult
using Brawler;                            // BrawlerMatchConfig / BrawlerMatchConfigData (MatchConfigData codec)
using xpTURN.Samples.DevLobby;            // DevMatchStagePolicy (+ DevFailingMatchResultSink under DEBUG)

// Dev lobby stand-in — a plain net8.0 console process, NOT a game server. Holds the Ed25519 private key
// (issuance) and the redeem authority (nonce consume / idempotency / match↔server binding) via
// DevLobbyCore — the same core the in-proc test fake wraps, so the redeem logic under test matches what
// runs here. Speaks the LiteNetLib lobby protocol (LobbyWire). DEV ONLY — never ship the private seed in
// a release client.
//
// CLI: dotnet run -- [port] [logLevel]   (default 9999 / Information)

int port = args.Length > 0 ? int.Parse(args[0]) : 9999;
var logLevel = args.Length > 1 ? Enum.Parse<KLogLevel>(args[1]) : KLogLevel.Information;

using var loggerFactory = KLoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(logLevel);
    builder.AddConsole();
    builder.AddRollingFile(options =>
    {
        options.FilePrefix = "DevLobby";
        options.RollingSizeKB = 1024 * 1024;
        options.FlushMode = KFlushMode.AsyncEvent;
    });
});
var logger = loggerFactory.CreateLogger("DevLobby");

var backend = Ed25519Backends.Default;
Func<long> now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
// Verify with the real public key (BC). The core holds the private seed (via the issuer) + the
// match→server assignment + the consumed-nonce ledger.
var lobby = SdDevLobby.CreateDevLobbyCore(backend, SdDevIdentity.PublicKey, now, logger);

// DEV (DEBUG builds only): --dev-sink-fail <N> → the first N matchResult submits throw, so the lobby withholds
// the ack and the dedi re-sends on its ~3s timer until the (N+1)th accepts (at-least-once retry, sink isolation).
// Pass AFTER the positional args, e.g.: dotnet run -- 9999 Information --dev-sink-fail 3
#if DEBUG
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--dev-sink-fail" && int.TryParse(args[i + 1], out int failCount) && failCount > 0)
    {
        lobby.SetMatchResultSink(new DevFailingMatchResultSink(failCount, new ReferenceLoggingSink(logger), logger));
        logger.KWarning($"[DevLobby][DEV] --dev-sink-fail={failCount}: first {failCount} matchResult submit(s) will throw (no ack → dedi retries ~3s apart)");
        break;
    }
#endif

var transport = new LiteNetLibTransport(logger, null, "xpTURN.DevLobby");
if (!transport.Listen("0.0.0.0", port, maxConnections: 64))
{
    logger.KError($"[DevLobby] failed to bind port {port} — exiting.");
    Environment.Exit(1);
}

// Anti-overflow sanity bound for roomReport decode (not a capacity policy — see use site below).
const int RoomReportRoomCap = 1024;
// Anti-overflow sanity bound for matchResult roster decode — see decoder OOB guard.
const int MatchResultRosterCap = 1024;

// Two-phase issue coordinator — reserves a room, pushes its match config to the dedi (ReservePush),
// and defers the IssueResponse until the dedi confirms (ReserveAck).
const long AckTimeoutMs = 2000; // ReserveAck wait before rollback → IssueFull
var coordinator = new DevLobbyReserveCoordinator(
    lobby, now, () => Guid.NewGuid().ToString("N"),
    send: (peerId, wire) => transport.Send(peerId, wire, DeliveryMethod.ReliableOrdered),
    configPolicy: MatchStagePolicy, // matchId → stage: the lobby owns each match's config (dev policy below)
    ticketValidityMs: SdDevIdentity.TicketValidityMs, ackTimeoutMs: AckTimeoutMs, logger: logger);

// Dev stage policy. The stage selection itself lives in DevMatchStagePolicy — pure, and therefore unit-tested;
// only the Brawler-coupled payload assembly stays here.
static (int stageId, byte[] payload) MatchStagePolicy(DevLobbyReserveCoordinator.MatchConfigRequest req)
{
    // The stage comes from the RENDEZVOUS matchId, never the instance id — the instance id ends in a hex
    // token digit and would flip the stage for half of all matches (see MatchConfigRequest).
    int stage = DevMatchStagePolicy.StageFor(req.MatchId);
    // Dev policy: botCount = stage (a stage-2 match gets 2 bots), so the lobby exercises the MatchConfigData
    // channel too, not just stageId. MatchConfigData is produced via Brawler's own codec (BrawlerMatchConfig
    // .Encode) — the dedi/client read it with the same codec, so there is no hand-rolled wire layout to keep
    // in sync. (This couples the dev lobby to the Brawler sample's config type; see the csproj Compile note.)
    int botCount = stage;

    // The two inputs a replay verifier cannot obtain on its own. The capacity is tick-0 state (bot ids are
    // numbered past it) and the instance id is the only key that joins an uploaded replay to this match.
    // Both ride the payload because that is what reaches every peer and lands in every peer's replay file.
    //
    // An id that does not fit is REFUSED, not truncated: FixedString64 cuts silently at 62 bytes, and two
    // ids cut to the same value would merge two different matches in the verification record.
    var instanceId = FixedString64.FromString(req.InstanceId ?? string.Empty);
    if (instanceId.ToString() != (req.InstanceId ?? string.Empty))
        throw new InvalidOperationException(
            $"Match instance id does not fit the match config ({req.InstanceId}) — 62 UTF-8 bytes is the budget.");

    byte[] payload = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData
    {
        BotCount = botCount,
        MaxPlayers = req.Capacity,
        MatchInstanceId = instanceId,
    });
    return (stage, payload);
}

transport.OnDataReceived += (peerId, data, len) =>
{
    byte kind = LobbyWire.PeekKind(data, len);
    if (kind == LobbyWire.IssueRequest && LobbyWire.TryDecodeIssueRequest(data, len, out var ir))
    {
        // Dev policy: account == authToken (no real auth). The coordinator reserves a room, pushes its match
        // config to the dedi, and defers the response until ReserveAck (two-phase); the ticket is minted only
        // on commit (a rolled-back reserve never issues one → no orphan-nonce redeem).
        coordinator.HandleIssue(ir.RequestId, peerId, ir.AuthToken, ir.DisplayName, ir.MatchId);
    }
    else if (kind == LobbyWire.ReserveAck && LobbyWire.TryDecodeReserveAck(data, len, out var ack))
    {
        // dedi → lobby: confirm/refuse a ReservePush → commit + issue, or rollback + Full (all participants).
        // peerId is verified against the pending's target dedi so a client can't forge acks for another's reserve.
        coordinator.HandleReserveAck(peerId, ack.RequestId, ack.Ok, ack.NakReason);
    }
    else if (kind == LobbyWire.RedeemRequest && LobbyWire.TryDecodeRedeemRequest(data, len, out var rr))
    {
        RedeemResult result = lobby.Redeem(rr.TicketWire, rr.SessionId, rr.ServerId, rr.RoomId);
        transport.Send(peerId, LobbyWire.EncodeRedeemResponse(rr.RequestId, result), DeliveryMethod.ReliableOrdered);
        logger.KInformation($"[DevLobby] redeem server={rr.ServerId} room={rr.RoomId} ok={result.Ok} code={result.RejectWireCode}");
    }
    else if (kind == LobbyWire.ServerRegister && LobbyWire.TryDecodeServerRegister(data, len, out var sr))
    {
        // dedi → lobby: capacity/endpoint advertise + reconnect-restore (one-way, no reply). Logs in core.
        lobby.HandleServerRegister(sr.ServerId, sr.Host, sr.Port, sr.MaxRooms, sr.MaxPlayersPerRoom, peerId);
        coordinator.RePushReservations(sr.ServerId, peerId); // reconnect-restore: re-push active reservations (fire-and-forget)
    }
    // Decode cap is an anti-overflow sanity bound (not a capacity policy) — generous so a dedi reporting many
    // rooms (e.g. Brawler's multi-room) isn't silently dropped; the authoritative per-server capacity is
    // (re)set from serverRegister. (Was SdDevIdentity.MaxRooms=2, which dropped larger reports → no heartbeat
    // → backup-timeout "hang" → server marked unavailable → issue FULL.)
    else if (kind == LobbyWire.RoomReport && LobbyWire.TryDecodeRoomReport(data, len, RoomReportRoomCap, out var rp))
    {
        // dedi → lobby: room occupancy/state reconcile (one-way, no reply). Core logs state changes only.
        lobby.HandleRoomReport(rp.ServerId, rp.Rooms, rp.RoomCount);
    }
    else if (kind == LobbyWire.MatchResult && LobbyWire.TryDecodeMatchResult(data, len, MatchResultRosterCap, out var mres))
    {
        // dedi → lobby: verified match result / abort notification. Idempotent by match instance; ack
        // ONLY after the lobby takes responsibility (a sink throw withholds the ack → dedi retries).
        bool ackOk = lobby.HandleMatchResult(peerId, mres.ServerId, mres.MatchInstanceId, mres.RoomId, mres.StageId,
                                             mres.TerminationKind, mres.Roster, mres.Payload);
        if (ackOk)
            transport.Send(peerId, LobbyWire.EncodeMatchResultAck(mres.RequestId, true), DeliveryMethod.ReliableOrdered);
        // else: withhold ack → dedi retries via its resend timer
    }
};

// Server (dedi) disconnect → mark unavailable (stop new assignment); reclaim only after grace (Sweep).
// client/redeem-only peers are unmapped → no-op. (peer↔server mapping is built by serverRegister; logs in core.)
transport.OnPeerDisconnected += disconnectedPeerId =>
{
    lobby.HandleServerDisconnect(disconnectedPeerId);
    coordinator.HandleClientDisconnect(disconnectedPeerId); // roll back any deferred issue for this client
};

logger.KInformation($"[DevLobby] listening on {port} — match '{SdDevIdentity.DevMatchId}' → server '{SdDevIdentity.DevServerId}'");

bool running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };
while (running)
{
    transport.PollEvents();
    long tick = now();
    lobby.Sweep(tick);              // reclaim expired reservations / consumed nonces, restore drained Empty rooms
    coordinator.SweepTimeouts(tick); // roll back deferred issues whose ReserveAck never arrived → IssueFull
    Thread.Sleep(15);
}
transport.Disconnect();
logger.KInformation($"[DevLobby] stopped.");
