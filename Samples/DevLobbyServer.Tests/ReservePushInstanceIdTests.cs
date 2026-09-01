using System.Collections.Generic;

using Xunit;

using xpTURN.Klotho.Network;             // RoomStateReport
using xpTURN.Klotho.Samples.Identity;    // BcEd25519Backend
using xpTURN.Klotho.Samples.Identity.Sd; // DevLobbyCore, DevLobbyReserveCoordinator, LobbyRoomRegistry, LobbyWire

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The coordinator pushes the INSTANCE id in the wire's matchId slot, while
    /// the stage policy keeps receiving the RENDEZVOUS matchId.
    /// <para>
    /// The split matters because <c>MatchStagePolicy</c> derives the stage from the id's trailing ASCII digit.
    /// An instance id ends in a hex token character, so leaking it into the policy flips stage 1 → 2 for eight of
    /// sixteen possible endings: wrong half the time, and silent. The recording policy below asserts the argument
    /// directly, which catches the leak deterministically rather than probabilistically.
    /// </para>
    /// </summary>
    public class ReservePushInstanceIdTests
    {
        const string Server = "srv", Match = "brawl-1";
        const int MaxRooms = 2, Capacity = 2, ServerPeer = 1;
        const long Validity = 60_000, AckTimeout = 2_000;

        long _now = 1_000_000;
        int _nonceSeq, _tokenSeq;
        readonly BcEd25519Backend _backend = new BcEd25519Backend();
        readonly List<(int peer, byte[] wire)> _sent = new List<(int, byte[])>();
        readonly List<string> _policySawMatchIds = new List<string>(); // every id handed to _configPolicy
        readonly List<string> _policySawInstanceIds = new List<string>(); // and the instance id beside it
        int _policySawCapacity = -1;                                      // the assigned server's capacity
        LobbyRoomRegistry _reg;

        DevLobbyCore NewCore()
        {
            _reg = new LobbyRoomRegistry();
            _reg.AddServer(Server, "127.0.0.1", 7777, MaxRooms, Capacity);
            var core = new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                        () => _now, 30_000, _reg,
                                        instanceTokenFactory: () => "t" + (++_tokenSeq));
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, ServerPeer);
            return core;
        }

        // Recording stage policy — captures what the coordinator passes in (the instance-id leak guard).
        DevLobbyReserveCoordinator NewCoord(DevLobbyCore core)
            => new DevLobbyReserveCoordinator(core, () => _now, () => "n" + (++_nonceSeq),
                   (peer, wire) => _sent.Add((peer, wire)),
                   req => { _policySawMatchIds.Add(req.MatchId); _policySawInstanceIds.Add(req.InstanceId); _policySawCapacity = req.Capacity; return (7, (byte[])null); },
                   Validity, AckTimeout);

        LobbyWire.ReservePushMsg LastPush()
        {
            LobbyWire.ReservePushMsg push = default; int found = 0;
            foreach (var (_, wire) in _sent)
                if (LobbyWire.PeekKind(wire, wire.Length) == LobbyWire.ReservePush
                    && LobbyWire.TryDecodeReservePush(wire, wire.Length, out var m)) { push = m; found++; }
            Assert.Equal(1, found);
            return push;
        }

        // ── the wire carries the instance id ────────────────────────────────────
        [Fact]
        public void FreshReserve_PushesInstanceId_NotRendezvousMatchId()
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", Match);

            Assert.Equal(Match + "#t1", LastPush().MatchInstanceId);
            Assert.Equal(Match + "#t1", _reg.Servers[Server].Rooms[0].InstanceId); // wire agrees with the slot
        }

        [Fact]
        public void RePush_CarriesTheSameInstanceId() // a different id would NAK against our own materialized entry
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", Match);
            coord.HandleReserveAck(ServerPeer, LastPush().RequestId, ok: true, nakReason: 0);
            _sent.Clear();

            coord.RePushReservations(Server, ServerPeer);

            Assert.Equal(Match + "#t1", LastPush().MatchInstanceId);
        }

        [Fact]
        public void SecondMatchOnTheSameLobby_PushesADifferentInstanceId() // the bug: both used to be "brawl-1"
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", Match);
            string first = LastPush().MatchInstanceId;
            coord.HandleReserveAck(ServerPeer, LastPush().RequestId, ok: true, nakReason: 0);
            _sent.Clear();

            core.HandleRoomReport(Server, new[] { new RoomStateReport
                { RoomId = 0, State = LobbyWire.RoomStateActive, PlayerCount = 1 } }, 1);
            core.HandleRoomReport(Server, new[] { new RoomStateReport
                { RoomId = 0, State = LobbyWire.RoomStateEmpty, PlayerCount = 0 } }, 1); // room ended → reclaimed
            coord.HandleIssue(101, 11, "acc2", "Name2", Match); // same rendezvous key, new match

            Assert.NotEqual(first, LastPush().MatchInstanceId);
        }

        // ── the stage policy never sees an instance id ──────────────────────────
        [Fact]
        public void StagePolicy_ReceivesRendezvousMatchId_OnFreshReserve()
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", Match);

            Assert.Equal(new[] { Match }, _policySawMatchIds);
            Assert.Equal(7, LastPush().StageId); // ...and its output still reaches the wire
        }

        [Fact]
        public void StagePolicy_ReceivesRendezvousMatchId_OnRePush()
        {
            var core = NewCore(); var coord = NewCoord(core);
            coord.HandleIssue(100, 10, "acc", "Name", Match);
            coord.HandleReserveAck(ServerPeer, LastPush().RequestId, ok: true, nakReason: 0);
            _sent.Clear(); _policySawMatchIds.Clear();

            coord.RePushReservations(Server, ServerPeer);

            Assert.Equal(new[] { Match }, _policySawMatchIds);
            Assert.Equal(7, LastPush().StageId);
        }

    }
}
