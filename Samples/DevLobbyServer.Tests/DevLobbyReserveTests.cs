using System.Collections.Generic;

using Xunit;

using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, DevLobbyReserveCoordinator, LobbyMatchConfigSource, LobbyWire, ...

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// Lobby reserve-push channel: ReservePush/ReserveAck wire round-trip, the lobby-side two-phase issue
    /// coordinator (step-1a/1b/2, commit/rollback/timeout/client-dc), and the dedi-side lobby match config
    /// source (apply/resolve + self-heal Nak). Deterministic via an injected clock + nonce + captured send.
    /// </summary>
    public sealed class DevLobbyReserveTests
    {
        // ── ReservePush / ReserveAck wire round-trip ─────────────────────────
        [Fact]
        public void ReservePush_RoundTrip_WithPayload()
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var buf = LobbyWire.EncodeReservePush(42, 3, "matchX", 2, payload, 123456789L);
            Assert.Equal(LobbyWire.ReservePush, LobbyWire.PeekKind(buf, buf.Length));
            Assert.True(LobbyWire.TryDecodeReservePush(buf, buf.Length, out var m));
            Assert.Equal(42, m.RequestId);
            Assert.Equal(3, m.RoomId);
            Assert.Equal("matchX", m.MatchInstanceId);
            Assert.Equal(2, m.StageId);
            Assert.Equal(payload, m.MatchConfigData);
            Assert.Equal(123456789L, m.ReservationExpiresAt);
        }

        [Fact]
        public void ReservePush_RoundTrip_NullPayload()
        {
            var buf = LobbyWire.EncodeReservePush(1, 0, "m", 1, null, 5L);
            Assert.True(LobbyWire.TryDecodeReservePush(buf, buf.Length, out var m));
            Assert.Null(m.MatchConfigData); // null → length 0 → decodes back to null
            Assert.Equal(1, m.StageId);
        }

        [Fact]
        public void ReserveAck_RoundTrip()
        {
            var ok = LobbyWire.EncodeReserveAck(7, true, LobbyWire.ReserveNakNone);
            Assert.True(LobbyWire.TryDecodeReserveAck(ok, ok.Length, out var a));
            Assert.Equal(7, a.RequestId); Assert.True(a.Ok); Assert.Equal(LobbyWire.ReserveNakNone, a.NakReason);

            var nak = LobbyWire.EncodeReserveAck(8, false, LobbyWire.ReserveNakMatchConflict);
            Assert.True(LobbyWire.TryDecodeReserveAck(nak, nak.Length, out var b));
            Assert.False(b.Ok); Assert.Equal(LobbyWire.ReserveNakMatchConflict, b.NakReason);
        }

        [Fact]
        public void PeekKind_UnknownKind_NoCrash() // an old decoder ignores an unknown message kind gracefully (no throw)
        {
            var buf = new byte[] { 99, 0, 0, 0, 0 };
            Assert.Equal((byte)99, LobbyWire.PeekKind(buf, buf.Length));
            Assert.False(LobbyWire.TryDecodeReservePush(buf, buf.Length, out _));
            Assert.False(LobbyWire.TryDecodeIssueRequest(buf, buf.Length, out _));
        }

        // ── coordinator harness ──────────────────────────────────────────────
        private const string Server = "srv";
        private const int MaxRooms = 2, Capacity = 2, ServerPeer = 1;
        private const long Validity = 60_000, AckTimeout = 2_000;
        private long _now = 1_000_000;
        private int _nonceSeq;
        private readonly BcEd25519Backend _backend = new BcEd25519Backend();
        private readonly List<(int peer, byte[] wire)> _sent = new List<(int, byte[])>();
        private LobbyRoomRegistry _reg;

        private int _tokenSeq;

        private DevLobbyCore NewCore()
        {
            _reg = new LobbyRoomRegistry();
            _reg.AddServer(Server, "127.0.0.1", 7777, MaxRooms, Capacity);
            var core = new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                        () => _now, 30_000, _reg,
                                        instanceTokenFactory: () => "t" + (++_tokenSeq)); // deterministic instance ids
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, ServerPeer); // set PeerId
            return core;
        }

        private DevLobbyReserveCoordinator NewCoord(DevLobbyCore core)
            => new DevLobbyReserveCoordinator(core, () => _now, () => "n" + (++_nonceSeq),
                   (peer, wire) => _sent.Add((peer, wire)),
                   req => (1 + (req.RoomId % 2), (byte[])null), Validity, AckTimeout);

        private LobbyRoomRegistry.RoomSlot Slot(int roomId) => _reg.Servers[Server].Rooms[roomId];

        // Finds the single ReservePush in _sent (asserts exactly one since the last clear) and returns it.
        private LobbyWire.ReservePushMsg LastPush()
        {
            LobbyWire.ReservePushMsg push = default; int found = 0;
            foreach (var (_, wire) in _sent)
                if (LobbyWire.PeekKind(wire, wire.Length) == LobbyWire.ReservePush
                    && LobbyWire.TryDecodeReservePush(wire, wire.Length, out var m)) { push = m; found++; }
            Assert.Equal(1, found);
            return push;
        }

        private (int count, LobbyWire.IssueResp last) IssueResponses()
        {
            int n = 0; LobbyWire.IssueResp last = default;
            foreach (var (_, wire) in _sent)
                if (LobbyWire.PeekKind(wire, wire.Length) == LobbyWire.IssueResponse
                    && LobbyWire.TryDecodeIssueResponse(wire, wire.Length, out var m)) { last = m; n++; }
            return (n, last);
        }

        // ── two-phase issue: reserve → ack → commit / rollback ───────────────
        [Fact]
        public void Issue_FreshRoom_SendsReservePush_DefersResponse() // step-2
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(clientRequestId: 100, clientPeerId: 10, "acc", "Name", "A");

            var push = LastPush();
            Assert.Equal(0, push.RoomId);
            Assert.Equal("A#t1", push.MatchInstanceId);         // the wire slot that used to carry matchId
            Assert.Equal(1, push.StageId);              // room 0 → stage 1+(0%2)=1
            Assert.Equal(ServerPeer, _sent[0].peer);    // pushed to the assigned server's peer
            Assert.Equal(0, IssueResponses().count);    // deferred — no IssueResponse yet
            Assert.True(Slot(0).AckPending);            // tentative
        }

        [Fact]
        public void ReserveAck_Ok_Commits_AndIssues() // step-2 commit
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            var push = LastPush();
            _sent.Clear();

            coord.HandleReserveAck(ServerPeer, push.RequestId, ok: true, nakReason: 0);

            var (count, resp) = IssueResponses();
            Assert.Equal(1, count);
            Assert.Equal(LobbyWire.IssueOk, resp.Status);
            Assert.Equal(0, resp.RoomId);
            Assert.False(string.IsNullOrEmpty(resp.TicketWire)); // minted on commit
            Assert.False(Slot(0).AckPending);                    // committed
        }

        [Fact]
        public void ReserveAck_Nak_RollsBack_Full() // step-2 nak
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            var push = LastPush();
            _sent.Clear();

            coord.HandleReserveAck(ServerPeer, push.RequestId, ok: false, nakReason: LobbyWire.ReserveNakMatchConflict);

            var (count, resp) = IssueResponses();
            Assert.Equal(1, count);
            Assert.Equal(LobbyWire.IssueFull, resp.Status);
            Assert.True(string.IsNullOrEmpty(resp.TicketWire)); // NOT minted on rollback
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, Slot(0).State); // reservation released
            Assert.Equal(0, Slot(0).Reserved);
        }

        [Fact]
        public void ReserveAck_WrongPeer_Ignored_PendingIntact() // forged ack from a non-dedi peer
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            var push = LastPush();
            _sent.Clear();

            // Forged ack from a peer that is NOT the reservation's target dedi → ignored, pending untouched.
            coord.HandleReserveAck(ServerPeer + 99, push.RequestId, ok: false, nakReason: LobbyWire.ReserveNakMatchConflict);
            Assert.Equal(0, IssueResponses().count);        // neither committed nor rolled back
            Assert.True(Slot(0).AckPending);                // still tentative
            Assert.Equal(LobbyRoomRegistry.RoomState.Reserved, Slot(0).State);

            // The real dedi's ack (correct peer) still resolves it.
            coord.HandleReserveAck(ServerPeer, push.RequestId, ok: true, nakReason: 0);
            var (count, resp) = IssueResponses();
            Assert.Equal(1, count);
            Assert.Equal(LobbyWire.IssueOk, resp.Status);
            Assert.False(Slot(0).AckPending);
        }

        [Fact]
        public void Timeout_RollsBack_Full() // step-2 timeout
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            _sent.Clear();

            _now += AckTimeout + 1;
            coord.SweepTimeouts(_now);

            var (count, resp) = IssueResponses();
            Assert.Equal(1, count);
            Assert.Equal(LobbyWire.IssueFull, resp.Status);
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, Slot(0).State);
        }

        [Fact]
        public void SameMatch_CommittedReuse_RespondsImmediately_NoNewPush() // step-1a
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            coord.HandleReserveAck(ServerPeer, LastPush().RequestId, ok: true, nakReason: 0); // commit player 1
            _sent.Clear();

            coord.HandleIssue(101, 11, "acc2", "Name2", "A"); // 2nd player, same match, committed room

            var (count, resp) = IssueResponses();
            Assert.Equal(1, count);                        // immediate response
            Assert.Equal(LobbyWire.IssueOk, resp.Status);
            foreach (var (_, wire) in _sent)               // and NO new ReservePush
                Assert.NotEqual(LobbyWire.ReservePush, LobbyWire.PeekKind(wire, wire.Length));
        }

        [Fact]
        public void SameMatch_PendingJoin_Waiter_CommitIssuesBoth() // step-1b
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");   // player 1 → pending (push sent)
            var push = LastPush();
            coord.HandleIssue(101, 11, "acc2", "Name2", "A"); // player 2 joins pending as waiter
            _sent.Clear();

            Assert.Equal(0, IssueResponses().count);          // both deferred pre-ack

            coord.HandleReserveAck(ServerPeer, push.RequestId, ok: true, nakReason: 0);

            var (count, _) = IssueResponses();
            Assert.Equal(2, count);                            // both issued on commit
            Assert.Equal(2, Slot(0).Reserved);
        }

        // ── client disconnect while a reservation is pending ─────────────────
        [Fact]
        public void ClientDisconnect_WhilePending_RollsBack()
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            _sent.Clear();

            coord.HandleClientDisconnect(clientPeerId: 10);

            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, Slot(0).State); // reservation rolled back
            Assert.Equal(0, Slot(0).Reserved);
            // A late ack now finds no pending → no-op (no throw, no stray issue).
            coord.HandleReserveAck(ServerPeer, 1, ok: true, nakReason: 0);
            Assert.Equal(0, IssueResponses().count);
        }

        // ── reconnect re-push (fire-and-forget) ──────────────────────────────
        [Fact]
        public void RePush_ReReservesActiveRooms_FireAndForget()
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", "A");
            coord.HandleReserveAck(ServerPeer, LastPush().RequestId, ok: true, nakReason: 0); // committed, room 0 Reserved
            _sent.Clear();

            coord.RePushReservations(Server, ServerPeer); // dedi reconnect

            var push = LastPush();          // exactly one re-push for room 0
            Assert.Equal(0, push.RoomId);
            Assert.Equal("A#t1", push.MatchInstanceId); // the SAME instance id as the original push (else the dedi NAKs us)
            Assert.Equal(1, push.StageId);
            // Fire-and-forget: its ack lands on no pending → no-op (no stray IssueResponse).
            coord.HandleReserveAck(ServerPeer, push.RequestId, ok: true, nakReason: 0);
            Assert.Equal(0, IssueResponses().count);
        }

        // ── dedi-side LobbyMatchConfigSource ─────────────────────────────────
        [Fact]
        public void MatchConfigSource_ApplyThenResolve_RoundTrip()
        {
            var src = new LobbyMatchConfigSource(MaxRooms, () => _now);
            var (ok, nak) = src.ApplyReservePush(1, "A", stageId: 2, matchConfigData: new byte[] { 9 }, expiresAt: _now + 10_000);
            Assert.True(ok); Assert.Equal(LobbyWire.ReserveNakNone, nak);

            Assert.True(src.TryResolve(1, out var cfg));
            Assert.Equal(1, cfg.RoomId);
            Assert.Equal(2, cfg.StageId);
            Assert.Equal(new byte[] { 9 }, cfg.MatchConfigData);
        }

        [Fact]
        public void MatchConfigSource_OutOfRange_Nak()
        {
            var src = new LobbyMatchConfigSource(MaxRooms, () => _now);
            var (ok, nak) = src.ApplyReservePush(MaxRooms, "A", 1, null, _now + 10_000);
            Assert.False(ok); Assert.Equal(LobbyWire.ReserveNakRoomRange, nak);
            Assert.False(src.TryResolve(MaxRooms, out _));
        }

        [Fact]
        public void MatchConfigSource_MaterializedConflict_Nak_ButUnmaterialized_Overwrites() // self-heal
        {
            var src = new LobbyMatchConfigSource(MaxRooms, () => _now);

            // Unmaterialized reservation for match A, then a push for match B on the same room → overwrite (self-heal).
            src.ApplyReservePush(0, "A", 1, null, _now + 10_000);
            var (ok1, _) = src.ApplyReservePush(0, "B", 2, null, _now + 10_000);
            Assert.True(ok1);
            Assert.True(src.TryResolve(0, out var cfg)); // now materialized for B
            Assert.Equal(2, cfg.StageId);

            // A push for a DIFFERENT match on the materialized room → Nak.
            var (ok2, nak2) = src.ApplyReservePush(0, "C", 1, null, _now + 10_000);
            Assert.False(ok2); Assert.Equal(LobbyWire.ReserveNakMatchConflict, nak2);

            // Same match re-push (reconnect) on the materialized room → allowed.
            var (ok3, _) = src.ApplyReservePush(0, "B", 2, null, _now + 10_000);
            Assert.True(ok3);
        }

        [Fact]
        public void MatchConfigSource_Expired_Dropped()
        {
            var src = new LobbyMatchConfigSource(MaxRooms, () => _now);
            src.ApplyReservePush(0, "A", 1, null, _now + 1_000);
            _now += 2_000;
            Assert.False(src.TryResolve(0, out _)); // expired → refused (RoomNotFound)
        }

        [Fact]
        public void MatchConfigSource_Release_ClearsMaterialized_AllowsReusedRoomNewMatch() // room-ended → reuse unblock
        {
            var src = new LobbyMatchConfigSource(MaxRooms, () => _now);

            // Match A on room 0 goes live (materialized).
            src.ApplyReservePush(0, "A", 1, null, _now + 300_000);
            Assert.True(src.TryResolve(0, out _));

            // Without release, the materialized entry lingers and a DIFFERENT match on the reused room 0 is
            // refused until the reservation TTL expires — a stale entry blocking a legitimately reused room.
            var (blocked, nak) = src.ApplyReservePush(0, "B", 2, null, _now + 300_000);
            Assert.False(blocked); Assert.Equal(LobbyWire.ReserveNakMatchConflict, nak);

            // Room 0 ends → SdRoomReporter observes the live→gone transition and calls Release(0).
            src.Release(0);

            // The reused room now accepts the new match (no lingering materialized entry, no TTL wait).
            var (ok, _) = src.ApplyReservePush(0, "B", 2, null, _now + 300_000);
            Assert.True(ok);
            Assert.True(src.TryResolve(0, out var cfg));
            Assert.Equal(2, cfg.StageId);
        }
    }
}
