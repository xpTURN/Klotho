using System;
using System.Collections.Generic;

using xpTURN.Klotho.Logging;          // IKLogger (optional report-channel observability)
using xpTURN.Klotho.Samples.Identity; // p2pref: LobbyTicket, LobbyTicketCodec, DevLobbyTicketIssuer, IEd25519Backend

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Transport-agnostic dev-lobby decision logic — the issue + redeem authority, shared by the in-proc
    /// test fake (<see cref="InProcLobbyRedeemClient"/>) AND the external DevLobbyServer process. Extracting
    /// it here means the redeem logic (signature, expiry, match↔server binding, idempotent nonce consume)
    /// is implemented and unit-tested ONCE; the LiteNetLib server is only a thin transport wrapper, so the
    /// two paths cannot diverge.
    /// <para>
    /// This object stands in for the lobby: it holds the Ed25519 private key (via the issuer) and the
    /// authoritative match→server assignment + consumed-nonce ledger. The private key never leaves it
    /// (process boundary in the sample). Thread-safe for the issue / redeem / assign paths (a server-global
    /// validator drives concurrent redeems; all mutate under the one lock). <see cref="HandleMatchResult"/> is
    /// the deliberate exception: it runs the result sink OUTSIDE the lock (so a slow sink never blocks the lock),
    /// which makes its de-dup a check-then-act — call it from a SINGLE thread only (the reference's single poll
    /// loop; see the note at the sink call). A multi-threaded host must serialize result delivery.
    /// </para>
    /// <para>
    /// Two match keys live here on purpose, and conflating them is the bug this naming exists to prevent.
    /// <c>matchId</c> is the RENDEZVOUS key: the clients share it so they land in the same room, and it therefore
    /// repeats across matches. <c>matchInstanceId</c> is minted once per room binding and keys the match RESULT,
    /// so it must be unique per match. Assignment and redeem take the former; <see cref="HandleMatchResult"/>
    /// takes the latter.
    /// </para>
    /// </summary>
    public sealed class DevLobbyCore
    {
        private readonly DevLobbyTicketIssuer _issuer;   // signs (private key) — issue side
        private readonly IEd25519Backend _backend;       // verify (public key) — redeem 1st-pass
        private readonly byte[] _publicKey;
        private readonly Func<long> _nowUnixMs;
        private readonly long _idempotencyWindowMs;
        private readonly long _backupTimeoutMs;   // P1 heartbeat backup timeout (hang detection)
        private readonly long _reclaimGraceMs;    // P1 grace after unavailable before reclaiming a server's rooms
        private readonly IKLogger _logger;        // optional — P1 report-channel observability (null in tests)
        private readonly LobbyRoomRegistry _registry; // room availability + match/reservation ledgers
        private readonly Func<string> _instanceTokenFactory; // per-match-instance token (see MakeInstanceId)

        private readonly object _lock = new object();
        // nonce -> consumed outcome; idempotency window returns the cached result, beyond it = replay reject.
        private readonly Dictionary<string, Consumed> _consumed = new Dictionary<string, Consumed>(StringComparer.Ordinal);
        // Processed match-instance keys for result de-dup. Process-lifetime only — a durable set /
        // idempotent backend is a real-lobby concern; the reference set is cleared on restart.
        // Grows by one entry (~130B) per accepted result and is never evicted: accepted as a dev-only
        // cost. A TTL would have to outlive the dedi's 120s give-up window, and it would silently
        // break a future journal replay, which re-sends arbitrarily old — already acked — records.
        private readonly HashSet<string> _processedMatchResults = new HashSet<string>(StringComparer.Ordinal);
        private IMatchResultSink _matchResultSink; // default = ReferenceLoggingSink (set in ctor); never null

        // Issued match-instance → assigned serverId, retained ACROSS the room's reclaim (results are
        // reclaim-independent, so the slot's InstanceId is already null by result time) — this is how
        // HandleMatchResult verifies a result's instance was actually assigned to the serverId reporting it
        // (F5 provenance). Pruned by TTL for instances whose result never arrives (rollback / dedi crash).
        private readonly Dictionary<string, IssuedInstance> _issuedInstances = new Dictionary<string, IssuedInstance>(StringComparer.Ordinal);
        private const long IssuedInstanceTtlMs = 30 * 60 * 1000; // 30 min — safely > dedi give-up (120s) + match length

        private readonly struct Consumed
        {
            public readonly long AtMs;       // when first consumed (idempotency-window anchor)
            public readonly long ExpiresAt;   // ticket expiry (record kept until then; after that expiry-check covers it)
            public readonly RedeemResult Result;
            public Consumed(long atMs, long expiresAt, RedeemResult result) { AtMs = atMs; ExpiresAt = expiresAt; Result = result; }
        }

        private readonly struct IssuedInstance
        {
            public readonly string ServerId;  // the server this match instance was assigned to
            public readonly long IssuedMs;    // mint time (TTL anchor)
            public IssuedInstance(string serverId, long issuedMs) { ServerId = serverId; IssuedMs = issuedMs; }
        }

        /// <param name="issuer">Signs tickets (holds the private key) — the lobby's issuance side.</param>
        /// <param name="backend">Ed25519 verify (redeem 1st-pass over wire bytes).</param>
        /// <param name="publicKey">Lobby public key (verify).</param>
        /// <param name="nowUnixMs">Lobby clock (expiry / nonce-window). Injectable for deterministic tests.</param>
        /// <param name="idempotencyWindowMs">Window in which a repeat redeem of the same nonce returns the
        /// cached result — this is how a player who passed validation but disconnected before the slot was
        /// reserved recovers on re-join. Must be ≥ realistic rejoin latency (the core's validation timeout
        /// plus reconnect time).</param>
        /// <param name="registry">Room availability registry (seeded with the dedicated server(s)); holds the
        /// match→(server,room) and nonce→reservation ledgers. The lobby is the authority for room assignment.</param>
        /// <param name="roomReportBackupTimeoutMs">P1: a server with no report for this long (and that has
        /// reported at least once) is marked unavailable — hang backstop behind transport disconnect. Injectable
        /// for tests; defaults to the dev constant.</param>
        /// <param name="serverReclaimGraceMs">P1: grace after a server goes unavailable before its rooms are
        /// reclaimed (≫ reconnect, ≪ reservation TTL). Injectable for tests; defaults to the dev constant.</param>
        /// <param name="instanceTokenFactory">Mints the per-match-instance token appended to the rendezvous matchId
        /// (see <see cref="MakeInstanceId"/>). Defaults to a full GUID — do NOT truncate it: every dev instance
        /// shares the same matchId prefix, so the token IS the collision space, and a birthday collision
        /// resurrects the very bug the instance id exists to fix (a real second match discarded as a duplicate
        /// result). Injectable so tests can assert deterministic ids.</param>
        public DevLobbyCore(DevLobbyTicketIssuer issuer, IEd25519Backend backend, byte[] publicKey,
                            Func<long> nowUnixMs, long idempotencyWindowMs,
                            LobbyRoomRegistry registry,
                            long roomReportBackupTimeoutMs = SdDevIdentity.RoomReportBackupTimeoutMs,
                            long serverReclaimGraceMs = SdDevIdentity.ServerReclaimGraceMs,
                            IKLogger logger = null,
                            Func<string> instanceTokenFactory = null)
        {
            _instanceTokenFactory = instanceTokenFactory ?? (() => Guid.NewGuid().ToString("N"));
            _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
            _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
            _idempotencyWindowMs = idempotencyWindowMs;
            _backupTimeoutMs = roomReportBackupTimeoutMs;
            _reclaimGraceMs = serverReclaimGraceMs;
            _logger = logger; // optional; report-channel events log here when present
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _matchResultSink = new ReferenceLoggingSink(logger); // default; SetMatchResultSink overrides (null resets)
        }

        // ── Issue (lobby → client). Caller supplies the time/nonce fields (dev/test determinism). ──
        public string Issue(in LobbyTicket ticket) => _issuer.Issue(ticket);

        /// <summary>Mints this match instance's unique key: <c>{matchId}#{token}</c>. The rendezvous matchId is
        /// carried as a prefix so the key stays self-contained — by the time a result reaches the lobby the room
        /// may already be reclaimed, leaving nothing to reverse-map the instance id against. Recover the
        /// rendezvous key with <c>LastIndexOf('#')</c>; no '#' means the whole string is the rendezvous key
        /// (a production lobby whose matchId is already unique appends no token).</summary>
        private string MakeInstanceId(string matchId) => matchId + "#" + _instanceTokenFactory();

        /// <summary>Capacity-aware room assignment for a match. Reserves a slot for the ticket's
        /// <paramref name="nonce"/> and returns where the client should connect. Atomic under the single lock.
        /// <para>step 1: reuse the match's existing room if it has free capacity (one room serves one match);
        /// step 2: else reserve a fresh Empty room (the dedicated server materializes it on the first
        /// handshake); else: <see cref="AssignResult.Full"/> (transient — all rooms occupied).</para></summary>
        /// <param name="matchId">The match id (equals the ticket's SessionId).</param>
        /// <param name="nonce">The ticket nonce — the reservation key used to reclaim the slot when the ticket expires.</param>
        /// <param name="ticketExpiresAt">The ticket's expiry; the reservation is reclaimed once this passes.</param>
        public AssignResult TryAssign(string matchId, string nonce, long ticketExpiresAt)
        {
            lock (_lock)
            {
                SweepReservations(_nowUnixMs()); // reclaim ghost reservations / restore Empty rooms first

                // step 1 — reuse the match's existing room if it has capacity (skip if its server is
                // unavailable — falls through to step 2 for multi-server failover; single-server dev → Full).
                if (_registry.MatchLedger.TryGetValue(matchId, out var loc)
                    && _registry.Servers.TryGetValue(loc.ServerId, out var srv0)
                    && srv0.Available
                    && loc.RoomId >= 0 && loc.RoomId < srv0.Rooms.Length)
                {
                    var slot0 = srv0.Rooms[loc.RoomId];
                    if (slot0.EffectiveFree > 0)
                    {
                        slot0.Reserved++;
                        _registry.ReservationLedger[nonce] = new LobbyRoomRegistry.Reservation(matchId, loc.ServerId, loc.RoomId, ticketExpiresAt);
                        // step-1: reuse. AckPending mirrors the room's current state — false = committed (step-1a,
                        // respond immediately), true = still awaiting the room's first ReserveAck (step-1b, join as waiter).
                        // The instance id is the room's, not a new one: this is the SAME match instance joining.
                        return AssignResult.Assigned(srv0, loc.RoomId, slot0.InstanceId, freshReserve: false, ackPending: slot0.AckPending);
                    }
                    return AssignResult.Full; // same match, room full → cannot spill (one room serves one match)
                }

                // step 2 — reserve a fresh Empty room (the loop handles multiple servers; the dev sample has one).
                foreach (var srv in _registry.Servers.Values)
                {
                    if (!srv.Available) continue; // never assign to a down/hung server
                    for (int roomId = 0; roomId < srv.Rooms.Length; roomId++)
                    {
                        var slot = srv.Rooms[roomId];
                        if (slot.State != LobbyRoomRegistry.RoomState.Empty) continue;
                        slot.State = LobbyRoomRegistry.RoomState.Reserved;
                        slot.SessionId = matchId;
                        slot.InstanceId = MakeInstanceId(matchId); // Empty→Reserved happens exactly once per match instance
                        _issuedInstances[slot.InstanceId] = new IssuedInstance(srv.ServerId, _nowUnixMs()); // provenance record (F5), outlives reclaim
                        slot.Reserved = 1;
                        slot.Occupied = 0;
                        slot.AckPending = true; // step-2: tentative until the dedi's ReserveAck confirms it
                        _registry.MatchLedger[matchId] = (srv.ServerId, roomId);
                        _registry.ReservationLedger[nonce] = new LobbyRoomRegistry.Reservation(matchId, srv.ServerId, roomId, ticketExpiresAt);
                        return AssignResult.Assigned(srv, roomId, slot.InstanceId, freshReserve: true, ackPending: true);
                    }
                }
                return AssignResult.Full; // all rooms occupied
            }
        }

        // ── two-phase reservation commit/rollback (paired with the deferred issue coordinator) ──

        /// <summary>Commits a tentative reservation: clears the room's AckPending (ReserveAck ok). After this,
        /// same-match joins (step-1a) respond immediately. No-op if the room is gone. Returns true if cleared.</summary>
        public bool CommitReservation(string serverId, int roomId)
        {
            lock (_lock)
            {
                if (_registry.Servers.TryGetValue(serverId, out var srv)
                    && roomId >= 0 && roomId < srv.Rooms.Length)
                {
                    srv.Rooms[roomId].AckPending = false;
                    return true;
                }
                return false;
            }
        }

        /// <summary>Active (Reserved-but-not-materialized) room reservations for a server — the set the dedi
        /// must re-learn on reconnect (re-push). One entry per room: (roomId, matchId, instanceId, latest ticket
        /// expiry). Both keys are returned: the re-push carries the instance id, while the stage policy is still
        /// evaluated against the rendezvous matchId.</summary>
        public List<(int roomId, string matchId, string instanceId, long expiresAt, int capacity)> ActiveRoomReservations(string serverId)
        {
            var result = new List<(int, string, string, long, int)>();
            lock (_lock)
            {
                if (!_registry.Servers.TryGetValue(serverId, out var srv)) return result;
                for (int roomId = 0; roomId < srv.Rooms.Length; roomId++)
                {
                    var slot = srv.Rooms[roomId];
                    if (slot.State != LobbyRoomRegistry.RoomState.Reserved || slot.SessionId == null) continue;
                    long exp = 0;
                    foreach (var kv in _registry.ReservationLedger)
                        if (string.Equals(kv.Value.ServerId, serverId, StringComparison.Ordinal)
                            && kv.Value.RoomId == roomId && kv.Value.ExpiresAt > exp)
                            exp = kv.Value.ExpiresAt;
                    result.Add((roomId, slot.SessionId, slot.InstanceId, exp, srv.MaxPlayersPerRoom));
                }
            }
            return result;
        }

        /// <summary>Rolls back a single tentative reservation by nonce (ReserveAck nak / timeout / client dc):
        /// removes the ledger entry, decrements Reserved, and restores the room to Empty when it holds no
        /// reservations/occupants left (clearing MatchLedger + AckPending). Mirrors SweepReservations' per-entry
        /// reclaim. No-op if the nonce is unknown (already committed/redeemed/swept).</summary>
        public void ReleaseReservation(string nonce)
        {
            lock (_lock)
            {
                if (!_registry.ReservationLedger.TryGetValue(nonce, out var resv)) return;
                _registry.ReservationLedger.Remove(nonce);
                if (!_registry.Servers.TryGetValue(resv.ServerId, out var srv)
                    || resv.RoomId < 0 || resv.RoomId >= srv.Rooms.Length) return;
                var slot = srv.Rooms[resv.RoomId];
                if (slot.Reserved > 0) slot.Reserved--;
                if (slot.State == LobbyRoomRegistry.RoomState.Reserved && slot.Reserved == 0 && slot.Occupied == 0)
                {
                    if (slot.SessionId != null) _registry.MatchLedger.Remove(slot.SessionId);
                    slot.SessionId = null;
                    slot.InstanceId = null;
                    slot.AckPending = false;
                    slot.State = LobbyRoomRegistry.RoomState.Empty;
                }
            }
        }

        /// <summary>Periodic sweep (call from the lobby loop): reclaim expired reservations + consumed-nonce
        /// records, and restore drained Empty rooms. Atomic under the single lock.</summary>
        public void Sweep(long now)
        {
            lock (_lock)
            {
                EvictExpired(now);       // _consumed
                SweepReservations(now);  // reservationLedger + Empty restore
                SweepServers(now);       // P1: heartbeat backup timeout + reconnect-grace reclaim
                PruneIssuedInstances(now); // F5: drop provenance records whose result never arrived (rollback/crash)
            }
        }

        // P1: mark hung servers unavailable (heartbeat backup) and reclaim long-unavailable servers'
        // rooms after the reconnect grace. Must be called under _lock.
        //   - backup timeout requires LastReportMs > 0 (a server that has NEVER reported — the bootstrap seed
        //     before the dedi connects, or a P0 test that jumps the clock — is exempt; P0-regression guard).
        //   - the `Available` guard stops re-stamping UnavailableSinceMs on an already-down server.
        // F5: evict provenance records older than the TTL — instances whose result never came (reserve rolled
        // back, dedi crashed before reporting). The TTL must outlive the dedi's give-up + match length so a
        // legitimate late result is never rejected as "unissued". Must be called under _lock.
        private void PruneIssuedInstances(long now)
        {
            if (_issuedInstances.Count == 0) return;
            List<string> stale = null;
            foreach (var kv in _issuedInstances)
                if (now - kv.Value.IssuedMs > IssuedInstanceTtlMs) (stale ??= new List<string>()).Add(kv.Key);
            if (stale != null)
                foreach (string k in stale) _issuedInstances.Remove(k);
        }

        private void SweepServers(long now)
        {
            foreach (var srv in _registry.Servers.Values)
            {
                if (srv.Available && srv.LastReportMs > 0 && now - srv.LastReportMs > _backupTimeoutMs)
                {
                    srv.Available = false;
                    srv.UnavailableSinceMs = now;
                    _logger?.KWarning($"[DevLobby] server '{srv.ServerId}' unavailable — heartbeat timeout ({now - srv.LastReportMs}ms, hang)");
                }
                else if (!srv.Available && srv.UnavailableSinceMs > 0
                         && now - srv.UnavailableSinceMs > _reclaimGraceMs)
                {
                    ReclaimServer(srv);
                    srv.UnavailableSinceMs = 0; // reclaim done — don't repeat every sweep (stays !Available until re-register)
                    _logger?.KInformation($"[DevLobby] server '{srv.ServerId}' rooms reclaimed (reconnect grace expired)");
                }
            }
        }

        /// <summary>Result of <see cref="TryAssign"/>. <c>Ok=false</c> → transient Full (client may retry).</summary>
        public readonly struct AssignResult
        {
            public readonly bool Ok;
            public readonly string ServerId;
            public readonly int RoomId;
            public readonly string Host;
            public readonly int Port;
            public readonly int ServerPeerId;  // dedi transport peerId (target for ReservePush); -1 = none
            public readonly bool FreshReserve;  // true = step-2 (new Empty room reserved) → needs ReservePush;
                                                // false = step-1 (reused an existing room for this match)
            public readonly bool AckPending;    // the assigned room is reserved-but-unconfirmed (awaiting ReserveAck)
            public readonly string InstanceId;  // the assigned room's match-instance key; consumed on the
                                                // FreshReserve path (the only one that pushes to the dedi)
            public readonly int Capacity;       // the assigned server's MaxPlayersPerRoom. Rides along because
                                                // the match config the lobby issues has to carry it: bot ids are
                                                // numbered past the capacity, so it is tick-0 state and a replay
                                                // verifier rebuilding tick 0 has no SessionConfig to read it from.
            private AssignResult(bool ok, string serverId, int roomId, string host, int port,
                                 int serverPeerId, bool freshReserve, bool ackPending, string instanceId, int capacity)
            { Ok = ok; ServerId = serverId; RoomId = roomId; Host = host; Port = port;
              ServerPeerId = serverPeerId; FreshReserve = freshReserve; AckPending = ackPending; InstanceId = instanceId; Capacity = capacity; }
            public static readonly AssignResult Full = new AssignResult(false, null, -1, string.Empty, 0, -1, false, false, null, 0);
            public static AssignResult Assigned(LobbyRoomRegistry.ServerEntry srv, int roomId, string instanceId, bool freshReserve, bool ackPending)
                => new AssignResult(true, srv.ServerId, roomId, srv.Host, srv.Port, srv.PeerId, freshReserve, ackPending, instanceId, srv.MaxPlayersPerRoom);
        }

        // ── Redeem (game server → lobby). Authoritative: nonce consume, expiry, match↔server binding. ──
        // The validator already ran a local 1st-pass; the lobby re-checks everything (it is the authority).
        public RedeemResult Redeem(string ticketWire, string sessionId, string serverId, int roomId)
        {
            long now = _nowUnixMs();

            // 1. split + verify-over-wire + parse (signed-but-malformed → invalid).
            if (!LobbyTicketCodec.TrySplitWire(ticketWire, out string payloadSeg, out string sigSeg))
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            byte[] payload, signature;
            try
            {
                payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
                signature = LobbyTicketCodec.Base64UrlDecode(sigSeg);
            }
            catch
            {
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);
            }
            if (signature.Length != SdWireCodes.Ed25519SignatureLength)
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            bool verified;
            try { verified = _backend.Verify(_publicKey, payload, signature); }
            catch { return RedeemResult.Reject(SdWireCodes.IdentityInvalid); }
            if (!verified)
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            LobbyTicket p;
            try { p = LobbyTicketCodec.ParsePayload(payload); }
            catch { return RedeemResult.Reject(SdWireCodes.IdentityInvalid); }

            // 2. expiry — lobby clock is authority (the validator's local check uses a lenient skew margin).
            if (p.ExpiresAt <= now)
                return RedeemResult.Reject(SdWireCodes.IdentityExpired);

            // 3+4. match binding + nonce consume + occupied transition — ONE critical section so the
            //    consumed-nonce ↔ reservation ↔ occupied transition stays atomic.
            //    Nonce zones for a previously-consumed nonce while the ticket is still valid:
            //      within window  → idempotent recovery (return cached accept; no occupied++);
            //      beyond window   → replay reject (9).
            //    The record is kept until the ticket EXPIRES (then step 2 rejects any re-presentation).
            lock (_lock)
            {
                EvictExpired(now);
                SweepReservations(now);

                // 3. match binding — the lobby's assignment ledger is authoritative (matchId == ticket.SessionId).
                //    Reject if this match is not assigned to the redeeming (server, room) (cross-match / wrong room /
                //    unassigned). P2 room cross-check is FAIL-CLOSED: ledger RoomId is always >= 0 (TryAssign), so a
                //    sentinel roomId = -1 (a non-RoomManager SD that never called SetRoomId) mismatches and is rejected.
                if (!_registry.MatchLedger.TryGetValue(p.SessionId, out var bound)
                    || !string.Equals(bound.ServerId, serverId, StringComparison.Ordinal)
                    || bound.RoomId != roomId)
                    return RedeemResult.Reject(SdWireCodes.IdentitySessionMismatch);

                if (_consumed.TryGetValue(p.Nonce, out Consumed prior))
                {
                    if (now - prior.AtMs <= _idempotencyWindowMs)
                        return prior.Result;                                  // recovery (no double occupied++)
                    return RedeemResult.Reject(SdWireCodes.IdentityRejected);  // replay
                }

                // 4. first consume — accept + reservation→occupied transition (reserved--/occupied++).
                // Demo: attach the account's authoritative entitlement (owned set) in the SAME redeem snapshot
                // as the account/displayName, so the account and its entitlement are confirmed together.
                // Deterministic per account so replay/reconnect recovers the same blob (the idempotency cache
                // below returns this exact result).
                var result = RedeemResult.Accept(p.Account, p.DisplayName, DemoEntitlement.ForAccount(p.Account));
                _consumed[p.Nonce] = new Consumed(now, p.ExpiresAt, result);
                if (_registry.ReservationLedger.TryGetValue(p.Nonce, out var resv)
                    && _registry.Servers.TryGetValue(resv.ServerId, out var srv)
                    && resv.RoomId >= 0 && resv.RoomId < srv.Rooms.Length)
                {
                    var slot = srv.Rooms[resv.RoomId];
                    if (slot.Reserved > 0) slot.Reserved--;
                    slot.Occupied++;
                    slot.State = LobbyRoomRegistry.RoomState.Active;
                    _registry.ReservationLedger.Remove(p.Nonce);
                }
                return result;
            }
        }

        // ── P1: dedi → lobby report handlers (serverRegister / roomReport / disconnect). All under _lock. ──

        /// <summary>serverRegister: upsert the server entry (reconnect-restore preserves rooms; a
        /// capacity change reinits + evicts that server's ledgers, R-G), refresh endpoint/availability, and
        /// (re)bind the peer→server reverse index. Time = internal clock.</summary>
        public void HandleServerRegister(string serverId, string host, int port,
                                         int maxRooms, int maxPlayersPerRoom, int peerId)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                _registry.Servers.TryGetValue(serverId, out var existing);
                // drop any previous peer→server mapping for this server (reconnect churn / capacity reinit).
                if (existing != null && existing.PeerId >= 0)
                    _registry.ServerByPeer.Remove(existing.PeerId);

                LobbyRoomRegistry.ServerEntry srv;
                string mode;
                if (existing == null)
                {
                    _registry.AddServer(serverId, host, port, maxRooms, maxPlayersPerRoom);
                    srv = _registry.Servers[serverId];
                    mode = "new";
                }
                else if (existing.MaxRooms != maxRooms || existing.MaxPlayersPerRoom != maxPlayersPerRoom)
                {
                    // capacity changed (R-G): evict the old server's ledgers, then re-create with fresh Empty
                    // rooms (data loss is consistent — capacity change ⇒ dedi restart ⇒ rooms gone).
                    ReclaimServer(existing);
                    _registry.AddServer(serverId, host, port, maxRooms, maxPlayersPerRoom);
                    srv = _registry.Servers[serverId];
                    mode = "reinit(capacity-change)";
                }
                else
                {
                    // reconnect restore: refresh endpoint, PRESERVE rooms/reservations/occupancy.
                    srv = existing;
                    srv.Host = host; srv.Port = port;
                    mode = srv.Available ? "refresh" : "reconnect-restore";
                }

                srv.PeerId = peerId;
                _registry.ServerByPeer[peerId] = serverId;
                srv.Available = true;
                srv.UnavailableSinceMs = 0;
                srv.LastReportMs = now;
                _logger?.KInformation($"[DevLobby] serverRegister server='{serverId}' {host}:{port} cap={maxRooms}x{maxPlayersPerRoom} peer={peerId} ({mode})");
            }
        }

        /// <summary>roomReport: reconcile occupied/state to the dedi's report; reclaim ended rooms.
        /// Single-exception rule — adopt the reported state, except a Reserved&amp;occupied==0 room reported
        /// Empty is kept (lazy not-yet-materialized). Unregistered serverId / out-of-range roomId are ignored.</summary>
        public void HandleRoomReport(string serverId, RoomStateReport[] rooms, int roomCount)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                if (!_registry.Servers.TryGetValue(serverId, out var srv)) return; // unregistered → ignore
                srv.Available = true;
                srv.UnavailableSinceMs = 0;
                srv.LastReportMs = now;

                for (int i = 0; i < roomCount; i++)
                {
                    int roomId = rooms[i].RoomId;
                    if (roomId < 0 || roomId >= srv.Rooms.Length) continue; // bounds-check (malformed/shrink)
                    var slot = srv.Rooms[roomId];
                    slot.LastReportMs = now;

                    bool reportEmpty = rooms[i].State == LobbyWire.RoomStateEmpty
                                    || rooms[i].State == LobbyWire.RoomStateDisposing;
                    if (reportEmpty)
                    {
                        // lazy not-yet-materialized (Reserved, nobody redeemed) → keep (no report / Empty does not mean absent).
                        if (slot.State == LobbyRoomRegistry.RoomState.Reserved && slot.Occupied == 0)
                            continue;
                        // a room that was actually in use reports Empty → match ended → reclaim.
                        if (slot.State != LobbyRoomRegistry.RoomState.Empty)
                        {
                            string ended = slot.SessionId;
                            ReclaimRoom(srv, roomId);
                            _logger?.KInformation($"[DevLobby] roomReport reclaim server='{serverId}' room={roomId} match='{ended}' (ended → slot freed)");
                        }
                        continue;
                    }

                    // report Active/Draining → adopt (occupied = dedi authority; SessionId unchanged: a
                    // lobby-Empty slot stays unbound — P1 roomId-gap surface, P2 closes).
                    var prevState = slot.State;
                    slot.Occupied = rooms[i].PlayerCount;
                    slot.State = rooms[i].State == LobbyWire.RoomStateDraining
                        ? LobbyRoomRegistry.RoomState.Draining
                        : LobbyRoomRegistry.RoomState.Active;
                    if (slot.State != prevState) // log only transitions (steady heartbeat stays quiet)
                        _logger?.KInformation($"[DevLobby] roomReport server='{serverId}' room={roomId} {prevState}→{slot.State} occupied={slot.Occupied}");
                }
            }
        }

        /// <summary>Sets the sink that receives accepted match results. null → resets to the built-in
        /// <see cref="ReferenceLoggingSink"/>. Injected by the DevLobbyServer process (or a test) before the poll
        /// loop starts. Never leaves the field null (HandleMatchResult calls it unconditionally).</summary>
        public void SetMatchResultSink(IMatchResultSink sink) => _matchResultSink = sink ?? new ReferenceLoggingSink(_logger);

        /// <summary>matchResult (dedi → lobby): a verified match result or abort notification. Idempotent
        /// by match instance (process-lifetime de-dup). Forwards to the sink and, ONLY on a clean sink return
        /// (responsibility handoff), marks the instance processed and returns true so the caller acks. A sink
        /// throw is isolated → returns false → NO ack → the dedi retries. A duplicate returns true (idempotent
        /// ack). Independent of roomReport reclaim: the message is self-contained by its match-instance key.</summary>
        public bool HandleMatchResult(int peerId, string serverId, string matchInstanceId, int roomId, int stageId,
                                      byte terminationKind, MatchResultRosterEntry[] roster, byte[] payload)
        {
            if (string.IsNullOrEmpty(matchInstanceId)) return false; // unkeyable → withhold ack (defensive)

            lock (_lock)
            {
                // P1 provenance: the sender peer must be the registered dedi for the serverId it claims — else any
                // client on the shared report channel could forge a result "as" another server (ReserveAck applies
                // this same peer check). Reject WITHOUT marking so the genuine dedi's result still gets through.
                if (!_registry.ServerByPeer.TryGetValue(peerId, out string boundServer) || boundServer != serverId)
                {
                    _logger?.KWarning($"[DevLobby] matchResult REJECT peer={peerId} claims server='{serverId}' (not its registered server) instance='{matchInstanceId}'");
                    return false; // do NOT ack, do NOT mark
                }

                // Dedup BEFORE P2: a retry of an already-processed result must still get an idempotent ack even
                // after its issue record was dropped on first handoff (else the dedi retries to give-up). A
                // not-yet-processed forgery falls through to the P2 binding check below.
                if (_processedMatchResults.Contains(matchInstanceId))
                {
                    _logger?.KInformation($"[DevLobby] matchResult dup instance='{matchInstanceId}' → ack (idempotent)");
                    return true; // already handled → idempotent ack (dedi stops retrying)
                }

                // P2 provenance: the instance must have been issued to THIS serverId. Closes the cross-server
                // forgery P1 alone misses (attacker registers its own serverId, replays a victim's instance id).
                if (!_issuedInstances.TryGetValue(matchInstanceId, out IssuedInstance issued) || issued.ServerId != serverId)
                {
                    _logger?.KWarning($"[DevLobby] matchResult REJECT instance='{matchInstanceId}' not issued to server='{serverId}' (peer={peerId})");
                    return false; // do NOT ack, do NOT mark
                }
            }

            // Submit OUTSIDE the lock (must be fast/non-blocking; a real backend offloads heavy work). A throw is
            // isolated so the single-threaded poll loop is never taken down by the game backend.
            try
            {
                // _matchResultSink is never null (ReferenceLoggingSink default; SetMatchResultSink null-resets).
                _matchResultSink.Submit(serverId, matchInstanceId, roomId, stageId, terminationKind, roster, payload);
            }
            catch (Exception e)
            {
                _logger?.KWarning($"[DevLobby] matchResult sink threw instance='{matchInstanceId}' → NO ack (dedi retry): {e.Message}");
                return false; // withhold ack — do NOT mark processed
            }

            lock (_lock)
            {
                _processedMatchResults.Add(matchInstanceId); // mark ONLY after a clean handoff
                _issuedInstances.Remove(matchInstanceId);    // provenance consumed — retries now short-circuit at the dedup above
            }
            _logger?.KInformation($"[DevLobby] matchResult accepted instance='{matchInstanceId}' server='{serverId}' room={roomId} kind={terminationKind} roster={roster?.Length ?? 0}");
            return true;
        }

        /// <summary>Transport disconnect: mark the server unavailable (stop new assignment) but do NOT
        /// reclaim — that waits for the reconnect grace (Sweep). Stale/superseded peerId (reconnect churn) is
        /// ignored via the reverse-index + PeerId guard.</summary>
        public void HandleServerDisconnect(int peerId)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                if (!_registry.ServerByPeer.TryGetValue(peerId, out var serverId)) return; // not a server peer
                if (!_registry.Servers.TryGetValue(serverId, out var srv) || srv.PeerId != peerId) return; // superseded
                srv.Available = false;
                srv.UnavailableSinceMs = now;
                _registry.ServerByPeer.Remove(peerId);
                _logger?.KWarning($"[DevLobby] server '{serverId}' unavailable — transport disconnect (peer={peerId}); new assignment stopped, reclaim after grace");
                srv.PeerId = -1; // peerId may be reused by a future (different) connection
            }
        }

        // Reclaim one room slot: evict its match binding + this room's reservationLedger entries, zero counts,
        // → Empty. Must be called under _lock.
        private void ReclaimRoom(LobbyRoomRegistry.ServerEntry srv, int roomId)
        {
            var slot = srv.Rooms[roomId];
            if (slot.SessionId != null) _registry.MatchLedger.Remove(slot.SessionId);
            List<string> drop = null;
            foreach (var kv in _registry.ReservationLedger)
                if (kv.Value.RoomId == roomId
                    && string.Equals(kv.Value.ServerId, srv.ServerId, StringComparison.Ordinal))
                    (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null)
                for (int i = 0; i < drop.Count; i++) _registry.ReservationLedger.Remove(drop[i]);
            slot.SessionId = null;
            slot.InstanceId = null;
            slot.Reserved = 0;
            slot.Occupied = 0;
            slot.State = LobbyRoomRegistry.RoomState.Empty;
        }

        // Reclaim every room of a server (grace reclaim / capacity reinit). Must be called under _lock.
        private void ReclaimServer(LobbyRoomRegistry.ServerEntry srv)
        {
            for (int roomId = 0; roomId < srv.Rooms.Length; roomId++)
                ReclaimRoom(srv, roomId);
        }

        // Reclaim reservations whose ticket has expired (now > ExpiresAt) — reserved--; then restore any
        // room that drained to a pure reservation with nothing left (Reserved & reserved==0 & occupied==0)
        // back to Empty and evict its match binding. This is purely a lobby-ledger event (no server report
        // needed). Must be called under _lock.
        private void SweepReservations(long now)
        {
            if (_registry.ReservationLedger.Count > 0)
            {
                List<string> stale = null;
                foreach (var kv in _registry.ReservationLedger)
                    if (now > kv.Value.ExpiresAt) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        var resv = _registry.ReservationLedger[stale[i]];
                        _registry.ReservationLedger.Remove(stale[i]);
                        if (_registry.Servers.TryGetValue(resv.ServerId, out var srv)
                            && resv.RoomId >= 0 && resv.RoomId < srv.Rooms.Length)
                        {
                            var slot = srv.Rooms[resv.RoomId];
                            if (slot.Reserved > 0) slot.Reserved--;
                        }
                    }
                }
            }
            // Empty restore: a reserved-but-never-redeemed room with no reservations left.
            foreach (var srv in _registry.Servers.Values)
            {
                foreach (var slot in srv.Rooms)
                {
                    if (slot.State == LobbyRoomRegistry.RoomState.Reserved
                        && slot.Reserved == 0 && slot.Occupied == 0)
                    {
                        if (slot.SessionId != null) _registry.MatchLedger.Remove(slot.SessionId);
                        slot.SessionId = null;
                        slot.InstanceId = null;
                        slot.State = LobbyRoomRegistry.RoomState.Empty;
                    }
                }
            }
        }

        // Drop consumed records for tickets that have expired (now > ExpiresAt); the expiry check then
        // guards any re-presentation, so the ledger stays bounded. Called under _lock.
        private void EvictExpired(long now)
        {
            if (_consumed.Count == 0) return;
            List<string> stale = null;
            foreach (var kv in _consumed)
            {
                if (now > kv.Value.ExpiresAt)
                    (stale ??= new List<string>()).Add(kv.Key);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) _consumed.Remove(stale[i]);
        }
    }
}
