using System;
using System.Threading;

using xpTURN.Klotho.Logging;              // KLoggerFactory, KLogLevel, IKLogger
using xpTURN.Klotho.Network;              // DeliveryMethod
using xpTURN.Klotho.LiteNetLib;           // LiteNetLibTransport
using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, SdDevIdentity, LobbyWire, RedeemResult

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

var transport = new LiteNetLibTransport(logger, null, "xpTURN.DevLobby");
if (!transport.Listen("0.0.0.0", port, maxConnections: 64))
{
    logger.KError($"[DevLobby] failed to bind port {port} — exiting.");
    Environment.Exit(1);
}

// Anti-overflow sanity bound for roomReport decode (not a capacity policy — see use site below).
const int RoomReportRoomCap = 1024;

transport.OnDataReceived += (peerId, data, len) =>
{
    byte kind = LobbyWire.PeekKind(data, len);
    if (kind == LobbyWire.IssueRequest && LobbyWire.TryDecodeIssueRequest(data, len, out var ir))
    {
        // Dev policy: account == authToken (no real auth). The nonce is generated (it is the reservation
        // key) BEFORE assignment; the ticket is minted with that nonce only on a successful assign.
        long t = now();
        long expiresAt = t + SdDevIdentity.TicketValidityMs;
        string nonce = Guid.NewGuid().ToString("N");
        var assign = lobby.TryAssign(ir.MatchId, nonce, expiresAt);
        if (!assign.Ok)
        {
            transport.Send(peerId, LobbyWire.EncodeIssueResponse(ir.RequestId, LobbyWire.IssueFull,
                string.Empty, string.Empty, 0, -1, LobbyWire.ModeSd), DeliveryMethod.ReliableOrdered);
            logger.KInformation($"[DevLobby] issue FULL account={ir.AuthToken} match={ir.MatchId}");
        }
        else
        {
            var ticket = new LobbyTicket(ir.AuthToken, ir.DisplayName, ir.MatchId, t, expiresAt, nonce);
            string wire = lobby.Issue(ticket);
            transport.Send(peerId, LobbyWire.EncodeIssueResponse(ir.RequestId, LobbyWire.IssueOk,
                wire, assign.Host, assign.Port, assign.RoomId, LobbyWire.ModeSd), DeliveryMethod.ReliableOrdered);
            logger.KInformation($"[DevLobby] issue account={ir.AuthToken} match={ir.MatchId} → {assign.Host}:{assign.Port} room={assign.RoomId}");
        }
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
};

// Server (dedi) disconnect → mark unavailable (stop new assignment); reclaim only after grace (Sweep).
// client/redeem-only peers are unmapped → no-op. (peer↔server mapping is built by serverRegister; logs in core.)
transport.OnPeerDisconnected += disconnectedPeerId =>
{
    lobby.HandleServerDisconnect(disconnectedPeerId);
};

logger.KInformation($"[DevLobby] listening on {port} — match '{SdDevIdentity.DevMatchId}' → server '{SdDevIdentity.DevServerId}'");

bool running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };
while (running)
{
    transport.PollEvents();
    lobby.Sweep(now()); // reclaim expired reservations / consumed nonces, restore drained Empty rooms
    Thread.Sleep(15);
}
transport.Disconnect();
logger.KInformation($"[DevLobby] stopped.");
