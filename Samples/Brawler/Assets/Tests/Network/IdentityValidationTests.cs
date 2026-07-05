using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Samples.Identity;    // shared reference (real Ed25519 validator / issuer)
using xpTURN.Klotho.Samples.Identity.Sd; // SD redeem validator / DevLobbyCore / in-proc lobby fake

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Identity validation hook tests. Drives CompletePeerSync / CompleteLateJoinSync via reflection with
    /// a configurable mock validator and inspects boundary state (slot consumption, _players,
    /// _pendingValidation, AwaitingValidation, disconnect count).
    ///
    /// SD accept/late-join paths run FinalizeNormalJoin/FinalizeLateJoin whose tail (SyncComplete send,
    /// SimulationConfig) can throw without a fully wired engine — like ServerNetworkServiceMalformedTests,
    /// the boundary state (slot reserved / not) is asserted regardless of a downstream throw.
    ///
    /// Mock validation is single-threaded and deterministic (the test drives completion); the real
    /// cross-thread memory barrier is the validator implementation's responsibility, not covered here.
    /// </summary>
    [TestFixture]
    public class IdentityValidationTests
    {
        // ── Configurable mock validator ──────────────────────────

        private enum Mode { AcceptSync, RejectSync, PendThenAccept, PendForever }

        private sealed class MockValidation : IIdentityValidation
        {
            private volatile bool _complete;
            private IdentityValidationOutcome _outcome;
            public int DisposeCount;
            public IdentityValidationRequest Request;

            public MockValidation(bool complete, IdentityValidationOutcome outcome, IdentityValidationRequest req)
            {
                _complete = complete;
                _outcome = outcome;
                Request = req;
            }

            public bool IsComplete => _complete;
            public IdentityValidationOutcome Outcome => _outcome;

            public void Complete(IdentityValidationOutcome outcome) { _outcome = outcome; _complete = true; }
            public void Dispose() { DisposeCount++; }
        }

        private sealed class MockValidator : IPlayerIdentityValidator
        {
            private readonly Mode _mode;
            private readonly byte _rejectCode;
            private readonly string _account;
            private readonly string _displayName;

            public int BeginCount;
            public MockValidation Last;
            public IdentityValidationRequest LastRequest;

            public MockValidator(Mode mode, byte rejectCode = 9, string account = "acc", string displayName = "Alice")
            {
                _mode = mode;
                _rejectCode = rejectCode;
                _account = account;
                _displayName = displayName;
            }

            public IIdentityValidation BeginValidate(in IdentityValidationRequest request)
            {
                BeginCount++;
                LastRequest = request;
                switch (_mode)
                {
                    case Mode.AcceptSync:
                        Last = new MockValidation(true, IdentityValidationOutcome.Accept(_account, _displayName), request);
                        break;
                    case Mode.RejectSync:
                        Last = new MockValidation(true, IdentityValidationOutcome.Reject(_rejectCode), request);
                        break;
                    default: // PendThenAccept / PendForever
                        Last = new MockValidation(false, default, request);
                        break;
                }
                return Last;
            }

            public void CompleteAccept() => Last.Complete(IdentityValidationOutcome.Accept(_account, _displayName));
            public void CompleteReject(byte code) => Last.Complete(IdentityValidationOutcome.Reject(code));
        }

        // ── Reflection handles ─────────────────────────────────────────────────

        private static readonly Type _sdType = typeof(ServerNetworkService);
        private static readonly Type _peerSyncStateType = _sdType.Assembly.GetType("xpTURN.Klotho.Network.PeerSyncState");
        private static readonly MethodInfo _sdCompletePeerSync = _sdType.GetMethod("CompletePeerSync", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _sdUpdate = _sdType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo _sdPlayers = _sdType.GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdAssigned = _sdType.GetField("_assignedPlayerIdCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdPending = _sdType.GetField("_pendingValidation", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdSyncStates = _sdType.GetField("_peerSyncStates", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdGameStarted = _sdType.GetField("_gameStarted", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdPeerTickets = _sdType.GetField("_peerTickets", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _sdSessionMagic = _sdType.GetField("_sessionMagic", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _sdDisconnectedCount = _sdType.GetField("_disconnectedPlayerCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _sdRentDisconnected = _sdType.GetMethod("RentDisconnectedInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _sdHandleReconnect = _sdType.GetMethod("HandleReconnectRequest", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly Type _p2pType = typeof(KlothoNetworkService);
        private static readonly MethodInfo _p2pCompletePeerSync = _p2pType.GetMethod("CompletePeerSync", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _p2pPlayers = _p2pType.GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _p2pSyncStates = _p2pType.GetField("_peerSyncStates", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _p2pClaimedNames = _p2pType.GetField("_peerClaimedDisplayNames", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _p2pTickets = _p2pType.GetField("_peerTickets", BindingFlags.NonPublic | BindingFlags.Instance);

        private TestTransport _transport;
        private LogCapture _logger;
        private ServerNetworkService _sd;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _transport.Listen("localhost", 0, 4);
            _logger = new LogCapture();
            _sd = new ServerNetworkService();
            _sd.Initialize(_transport, null, _logger);
            _sd.CreateRoom("test", 4);
            _sd.SetRoomId(0); // production RoomManager always sets this after CreateRoom; the redeem room
                              // cross-check (P2, fail-closed) needs it to match the lobby's rt-match→room0 binding.
        }

        [TearDown]
        public void TearDown() => TestTransport.Reset();

        // ── Helpers ─────────────────────────────────────────────────────────────

        private object MakeSyncState(int peerId, bool isLateJoin = false)
        {
            var s = Activator.CreateInstance(_peerSyncStateType, nonPublic: true);
            _peerSyncStateType.GetField("PeerId").SetValue(s, peerId);
            _peerSyncStateType.GetField("IsLateJoin").SetValue(s, isLateJoin);
            _peerSyncStateType.GetField("Completed").SetValue(s, false);
            _peerSyncStateType.GetField("RttSamples").SetValue(s, new long[5]);
            _peerSyncStateType.GetField("ClockOffsetSamples").SetValue(s, new long[5]);
            _peerSyncStateType.GetField("SyncPacketsSent").SetValue(s, 5);
            return s;
        }

        private void RegisterSyncState(int peerId, object state)
            => ((IDictionary)_sdSyncStates.GetValue(_sd)).Add(peerId, state);

        private void InvokeCompletePeerSync(int peerId, object state)
        {
            try { _sdCompletePeerSync.Invoke(_sd, new[] { (object)peerId, state }); }
            catch (TargetInvocationException) { /* tail may throw without a wired engine; boundary asserted by caller */ }
        }

        private void InvokeUpdate()
        {
            try { _sdUpdate.Invoke(_sd, null); }
            catch (TargetInvocationException) { }
        }

        private int PlayerCount() => ((IList)_sdPlayers.GetValue(_sd)).Count;
        private int AssignedCount() => (int)_sdAssigned.GetValue(_sd);
        private int PendingCount() => ((IDictionary)_sdPending.GetValue(_sd)).Count;
        private bool SyncStateExists(int peerId) => ((IDictionary)_sdSyncStates.GetValue(_sd)).Contains(peerId);

        private void BackdatePending(int peerId, long beginMs)
        {
            var dict = (IDictionary)_sdPending.GetValue(_sd);
            var pending = dict[peerId];
            pending.GetType().GetField("BeginMs", BindingFlags.Public | BindingFlags.Instance).SetValue(pending, beginMs);
        }

        // ── SD tests ──────────────────────────────────────────────────────────

        [Test] // reject(sync) — slot not consumed, disconnect, sync state cleaned
        public void SD_RejectSync_NoSlotConsumed_Disconnects()
        {
            _sd.SetIdentityValidator(new MockValidator(Mode.RejectSync, rejectCode: 9));
            int peerId = 7;
            RegisterSyncState(peerId, MakeSyncState(peerId));
            int before = _transport.DisconnectPeerCallCount;

            InvokeCompletePeerSync(peerId, ((IDictionary)_sdSyncStates.GetValue(_sd))[peerId]);

            Assert.AreEqual(0, PlayerCount(), "rejected peer must not be added to _players");
            Assert.AreEqual(0, AssignedCount(), "rejected peer must not consume a slot");
            Assert.AreEqual(before + 1, _transport.DisconnectPeerCallCount, "reject disconnects the peer");
            Assert.IsFalse(SyncStateExists(peerId), "sync state cleaned on reject");
        }

        [Test] // pending→accept — parks (no slot) then finalizes on drain
        public void SD_Pending_ParksThenAcceptsOnDrain()
        {
            var mock = new MockValidator(Mode.PendThenAccept);
            _sd.SetIdentityValidator(mock);
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId));

            InvokeCompletePeerSync(peerId, dict[peerId]);
            Assert.AreEqual(1, PendingCount(), "incomplete validation parks the peer");
            Assert.AreEqual(0, AssignedCount(), "parked peer reserves no slot");
            Assert.IsTrue((bool)_peerSyncStateType.GetField("AwaitingValidation").GetValue(dict[peerId]), "AwaitingValidation set while parked");

            mock.CompleteAccept();
            InvokeUpdate(); // drain
            Assert.AreEqual(0, PendingCount(), "drain clears the pending entry on completion");
            Assert.AreEqual(1, mock.Last.DisposeCount, "handle disposed after completion");
        }

        [Test] // pending→timeout — backdated begin time → reject(11) on drain
        public void SD_Pending_TimesOut()
        {
            _sd.SetIdentityValidator(new MockValidator(Mode.PendForever));
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId));
            InvokeCompletePeerSync(peerId, dict[peerId]);
            Assert.AreEqual(1, PendingCount());

            BackdatePending(peerId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000); // well past ValidationTimeoutMs
            int before = _transport.DisconnectPeerCallCount;
            InvokeUpdate(); // drain → timeout

            Assert.AreEqual(0, PendingCount(), "timed-out entry evicted");
            Assert.AreEqual(0, AssignedCount(), "timed-out peer consumed no slot");
            Assert.AreEqual(before + 1, _transport.DisconnectPeerCallCount, "timeout disconnects the peer");
            Assert.IsFalse(SyncStateExists(peerId));
        }

        [Test] // pending→disconnect — proactive evict + Dispose
        public void SD_Pending_DisconnectEvictsAndDisposes()
        {
            var mock = new MockValidator(Mode.PendForever);
            _sd.SetIdentityValidator(mock);
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId));
            InvokeCompletePeerSync(peerId, dict[peerId]);
            Assert.AreEqual(1, PendingCount());

            // Simulate peer disconnect → HandlePeerDisconnected evicts + disposes the handle.
            var handleDisc = _sdType.GetMethod("HandlePeerDisconnected", BindingFlags.NonPublic | BindingFlags.Instance);
            try { handleDisc.Invoke(_sd, new object[] { peerId }); } catch (TargetInvocationException) { }

            Assert.AreEqual(0, PendingCount(), "disconnect evicts the pending entry");
            Assert.AreEqual(1, mock.Last.DisposeCount, "handle disposed on disconnect (cancel signal)");
            InvokeUpdate(); // drain sees nothing → no crash
        }

        [Test] // over-admission: pending peer counts toward capacity
        public void SD_PendingCountsTowardCapacity()
        {
            _sd.SetIdentityValidator(new MockValidator(Mode.PendForever));
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(7, MakeSyncState(7));
            InvokeCompletePeerSync(7, dict[7]);

            var countPending = _sdType.GetMethod("CountPendingHandshakes", BindingFlags.NonPublic | BindingFlags.Instance);
            int pending = (int)countPending.Invoke(_sd, null);
            Assert.AreEqual(1, pending, "AwaitingValidation peer still counts as a pending handshake (capacity)");
        }

        // ── SD real-redeem-validator integration (ticket capture → validator → finalize glue) ──
        // The redeem decision logic is unit-tested in SdRedeemValidatorTests; these assert the network glue:
        // the captured ticket reaches the validator, the async redeem parks the peer, and the drain
        // finalizes (slot reserved) or the sync local reject consumes no slot.

        private static byte[] RedeemKey()
        {
            var k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = (byte)(0x22 + i);
            return k;
        }

        private void SetPeerTicket(int peerId, string wire)
            => ((IDictionary)_sdPeerTickets.GetValue(_sd))[peerId] = wire;

        // Registry pre-bound rt-match → (rt-server, room0) Active — mimics a prior lobby assignment so
        // redeem's match-binding check (which consults the registry ledger) passes.
        private static LobbyRoomRegistry RtRegistry()
        {
            var reg = new LobbyRoomRegistry();
            reg.AddServer("rt-server", "127.0.0.1", 7777, 2, 2);
            reg.MatchLedger["rt-match"] = ("rt-server", 0);
            reg.Servers["rt-server"].Rooms[0].State = LobbyRoomRegistry.RoomState.Active;
            reg.Servers["rt-server"].Rooms[0].SessionId = "rt-match";
            return reg;
        }

        [Test] // real validator: valid ticket → async redeem parks → release + drain reserves a slot
        public void SD_RealRedeemValidator_ValidTicket_ParksThenReservesSlot()
        {
            const long now = 1_700_000_000_000L;
            byte[] key = RedeemKey();
            var core = new DevLobbyCore(new DevLobbyTicketIssuer(new FakeEd25519Backend(), key),
                                        new FakeEd25519Backend(), key, () => now, 30_000, RtRegistry());
            var gate = new InProcLobbyRedeemClient(core, InProcLobbyRedeemClient.Mode.ManualGate);
            _sd.SetIdentityValidator(new SdRedeemIdentityValidator(
                key, new FakeEd25519Backend(), gate, "rt-server", () => now, 2000, 0));

            string ticket = new DevLobbyTicketIssuer(new FakeEd25519Backend(), key)
                .Issue(new LobbyTicket("rtAcc", "RtName", "rt-match", now - 1000, now + 60_000, "rt-n1"));
            int peerId = 9;
            RegisterSyncState(peerId, MakeSyncState(peerId));
            SetPeerTicket(peerId, ticket);
            int disconnectsBefore = _transport.DisconnectPeerCallCount;

            InvokeCompletePeerSync(peerId, ((IDictionary)_sdSyncStates.GetValue(_sd))[peerId]);
            Assert.AreEqual(1, PendingCount(), "async redeem parks the peer");
            Assert.AreEqual(0, AssignedCount(), "parked peer reserves no slot");

            Assert.IsTrue(gate.ReleaseGated(), "release the pending redeem");
            // The continuation completes the handle off-thread; pump the drain until it finalizes.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (PendingCount() > 0 && sw.ElapsedMilliseconds < 2000)
            {
                InvokeUpdate();
                System.Threading.Thread.Sleep(5);
            }

            // The drain processed the completed redeem (no longer pending) and took the ACCEPT path —
            // unlike a reject it does not disconnect the peer. (Slot reservation / roster propagation runs
            // in FinalizeNormalJoin, whose tail needs a wired engine, so it is asserted in a full-session
            // integration run, not this hook-level harness — same boundary as the mock pending tests.)
            Assert.AreEqual(0, PendingCount(), "drained once the redeem completed");
            Assert.AreEqual(disconnectsBefore, _transport.DisconnectPeerCallCount, "an accepted peer is not disconnected");
        }

        [Test] // real validator: tampered ticket → synchronous local reject → no slot, disconnect
        public void SD_RealRedeemValidator_TamperedTicket_RejectsNoSlot()
        {
            const long now = 1_700_000_000_000L;
            byte[] key = RedeemKey();
            var core = new DevLobbyCore(new DevLobbyTicketIssuer(new FakeEd25519Backend(), key),
                                        new FakeEd25519Backend(), key, () => now, 30_000, RtRegistry());
            _sd.SetIdentityValidator(new SdRedeemIdentityValidator(
                key, new FakeEd25519Backend(),
                new InProcLobbyRedeemClient(core, InProcLobbyRedeemClient.Mode.Immediate),
                "rt-server", () => now, 2000, 0));

            string good = new DevLobbyTicketIssuer(new FakeEd25519Backend(), key)
                .Issue(new LobbyTicket("a", "b", "rt-match", now - 1000, now + 60_000, "rt-n2"));
            string tampered = (good[0] == 'A' ? 'B' : 'A') + good.Substring(1);
            int peerId = 11;
            RegisterSyncState(peerId, MakeSyncState(peerId));
            SetPeerTicket(peerId, tampered);
            int before = _transport.DisconnectPeerCallCount;

            InvokeCompletePeerSync(peerId, ((IDictionary)_sdSyncStates.GetValue(_sd))[peerId]);

            Assert.AreEqual(0, AssignedCount(), "a locally-rejected ticket consumes no slot");
            Assert.AreEqual(0, PendingCount(), "a synchronous reject does not park");
            Assert.AreEqual(before + 1, _transport.DisconnectPeerCallCount, "rejected peer disconnected");
            Assert.IsFalse(SyncStateExists(peerId), "sync state cleaned on reject");
        }

        // ── JoinFailReason mapping unit ─────────────────────────

        [Test]
        public void JoinFailReason_IdentityWireCodes_Map()
        {
            Assert.AreEqual(JoinFailReason.IdentityInvalid, JoinFailReasonExtensions.FromJoinReject(6));
            Assert.AreEqual(JoinFailReason.IdentityExpired, JoinFailReasonExtensions.FromJoinReject(7));
            Assert.AreEqual(JoinFailReason.IdentitySessionMismatch, JoinFailReasonExtensions.FromJoinReject(8));
            Assert.AreEqual(JoinFailReason.IdentityRejected, JoinFailReasonExtensions.FromJoinReject(9));
            Assert.AreEqual(JoinFailReason.IdentityRequired, JoinFailReasonExtensions.FromJoinReject(10));
            Assert.AreEqual(JoinFailReason.IdentityValidationFailed, JoinFailReasonExtensions.FromJoinReject(11));
            // existing codes unchanged (regression)
            Assert.AreEqual(JoinFailReason.RoomFull, JoinFailReasonExtensions.FromJoinReject(2));
            Assert.AreEqual(JoinFailReason.Unknown, JoinFailReasonExtensions.FromJoinReject(0));
            Assert.AreEqual(JoinFailReason.Unknown, JoinFailReasonExtensions.FromJoinReject(99));
        }

        [Test]
        public void JoinFailReason_IdentityCodes_HaveNameAndMessage()
        {
            foreach (var r in new[] { JoinFailReason.IdentityInvalid, JoinFailReason.IdentityExpired,
                JoinFailReason.IdentitySessionMismatch, JoinFailReason.IdentityRejected,
                JoinFailReason.IdentityRequired, JoinFailReason.IdentityValidationFailed })
            {
                Assert.IsFalse(string.IsNullOrEmpty(r.ToName()), $"{r} ToName");
                Assert.IsFalse(string.IsNullOrEmpty(r.ToDefaultMessage()), $"{r} ToDefaultMessage");
                Assert.AreNotEqual("Unknown", r.ToName(), $"{r} should have a distinct name");
            }
        }

        // ── request population ──────────────────────────────────

        [Test]
        public void SD_Request_PopulatedFromHandshake()
        {
            var mock = new MockValidator(Mode.PendForever);
            _sd.SetIdentityValidator(mock);
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId, isLateJoin: false));
            InvokeCompletePeerSync(peerId, dict[peerId]);

            Assert.AreEqual(peerId, mock.LastRequest.PeerId);
            Assert.IsFalse(mock.LastRequest.IsLateJoin, "normal join → IsLateJoin false");
            Assert.IsFalse(mock.LastRequest.IsHostSelf, "guest validation → IsHostSelf false");
            Assert.IsNotNull(mock.LastRequest.Ticket, "Ticket non-null (empty when none)");
        }

        // ── opt-in no-regression ─────────────────────────────────────────

        [Test]
        public void SD_NoValidator_FinalizesWithFallback()
        {
            // No SetIdentityValidator → validator == null → no parking, no reject; fallback identity.
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId));
            int before = _transport.DisconnectPeerCallCount;
            InvokeCompletePeerSync(peerId, dict[peerId]);

            Assert.AreEqual(0, PendingCount(), "no validator → no pending");
            Assert.AreEqual(before, _transport.DisconnectPeerCallCount, "no validator → no reject");
            // Peer admitted: _players.Add runs before the SyncComplete tail (which may throw without a
            // wired engine). Pre-game slot reservation reflects in _players, not _assignedPlayerIdCount.
            Assert.AreEqual(1, PlayerCount(), "no-validator peer is admitted (added to _players)");
        }

        [Test] // StartGame race: pending completes after _gameStarted → drop(RoomFull), no slot
        public void SD_StartGameRace_DropsForLateJoinRetry()
        {
            var mock = new MockValidator(Mode.PendThenAccept);
            _sd.SetIdentityValidator(mock);
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId));
            InvokeCompletePeerSync(peerId, dict[peerId]);
            Assert.AreEqual(1, PendingCount());

            _sdGameStarted.SetValue(_sd, true); // StartGame raced while parked
            int before = _transport.DisconnectPeerCallCount;
            mock.CompleteAccept();
            InvokeUpdate(); // drain → FinalizeNormalJoin re-checks _gameStarted → drop

            Assert.AreEqual(0, PendingCount(), "race-dropped entry evicted");
            Assert.AreEqual(0, PlayerCount(), "race-dropped peer not admitted");
            Assert.AreEqual(before + 1, _transport.DisconnectPeerCallCount, "dropped with RoomFull for LateJoin retry");
            Assert.IsFalse(SyncStateExists(peerId));
        }

        [Test] // LateJoin bypass guard: late-join peer is validated at its own hook
        public void SD_LateJoin_RejectedAtHook_NotAdmitted()
        {
            var mock = new MockValidator(Mode.RejectSync, rejectCode: 9);
            _sd.SetIdentityValidator(mock);
            int peerId = 7;
            var dict = (IDictionary)_sdSyncStates.GetValue(_sd);
            dict.Add(peerId, MakeSyncState(peerId, isLateJoin: true)); // → dispatches to CompleteLateJoinSync
            int before = _transport.DisconnectPeerCallCount;

            InvokeCompletePeerSync(peerId, dict[peerId]);

            Assert.IsTrue(mock.LastRequest.IsLateJoin, "late-join request flagged IsLateJoin");
            Assert.AreEqual(0, PlayerCount(), "late-join reject must not admit the peer (no bypass)");
            Assert.AreEqual(before + 1, _transport.DisconnectPeerCallCount, "rejected at the late-join hook");
            Assert.IsFalse(SyncStateExists(peerId));
        }

        // ── P2P tests (KlothoNetworkService) ────────────────────────────────────

        private KlothoNetworkService MakeP2PHost(TestTransport transport, IPlayerIdentityValidator validator, string hostTicket)
        {
            var svc = new KlothoNetworkService();
            svc.Initialize(transport, new CommandFactory(), _logger); // P2P Initialize needs a real factory (CreateEmptyCommand)
            if (validator != null)
            {
                svc.SetIdentityValidator(validator);          // must precede CreateRoom (host self-validation runs there)
                svc.SetLocalIdentityTicket(hostTicket ?? string.Empty);
            }
            svc.CreateRoom("test", 4); // adds host (PlayerId 0), sets _sessionMagic/_sharedClock, runs host self-validation
            return svc;
        }

        private static IList P2PPlayers(KlothoNetworkService svc) => (IList)_p2pPlayers.GetValue(svc);

        private void InvokeP2PCompletePeerSync(KlothoNetworkService svc, int peerId, object state)
        {
            try { _p2pCompletePeerSync.Invoke(svc, new[] { (object)peerId, state }); }
            catch (TargetInvocationException) { /* tail may throw without engine; boundary asserted by caller */ }
        }

        [Test] // host self: validated own ticket overlays DisplayName/Account
        public void P2P_HostSelf_Accept_UsesValidatedIdentity()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, new MockValidator(Mode.AcceptSync, account: "hostacc", displayName: "HostName"), "host-ticket");
            var hp = (IPlayerInfo)P2PPlayers(host)[0];
            Assert.AreEqual("HostName", hp.DisplayName, "host self-validated DisplayName");
            Assert.AreEqual("hostacc", hp.Account, "host self-validated Account");
        }

        [Test] // host self: host is never rejected → keeps "Host" fallback
        public void P2P_HostSelf_Reject_KeepsHostFallback()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, new MockValidator(Mode.RejectSync), "host-ticket");
            var hp = (IPlayerInfo)P2PPlayers(host)[0];
            Assert.AreEqual("Host", hp.DisplayName, "rejected host keeps the \"Host\" fallback (cannot kick itself)");
            Assert.AreEqual(string.Empty, hp.Account);
        }

        [Test] // host self: no validator → unchanged "Host" (no validation)
        public void P2P_HostSelf_NoValidator_IsHost()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, null, null);
            var hp = (IPlayerInfo)P2PPlayers(host)[0];
            Assert.AreEqual("Host", hp.DisplayName);
        }

        [Test] // P2P guest reject(sync) — not admitted, disconnected
        public void P2P_GuestReject_NotAdmitted()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, new MockValidator(Mode.RejectSync, rejectCode: 9), null);
            int peerId = 5;
            var states = (IDictionary)_p2pSyncStates.GetValue(host);
            states.Add(peerId, MakeSyncState(peerId));
            int before = t.DisconnectPeerCallCount;

            InvokeP2PCompletePeerSync(host, peerId, states[peerId]);

            Assert.AreEqual(1, P2PPlayers(host).Count, "only the host remains; guest rejected");
            Assert.AreEqual(before + 1, t.DisconnectPeerCallCount, "guest reject disconnects");
            Assert.IsFalse(states.Contains(peerId), "guest sync state cleaned");
        }

        [Test] // P2P guest accept(sync) — admitted with validated identity
        public void P2P_GuestAccept_UsesValidatedIdentity()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, new MockValidator(Mode.AcceptSync, account: "gacc", displayName: "Guest1"), null);
            int peerId = 5;
            var states = (IDictionary)_p2pSyncStates.GetValue(host);
            states.Add(peerId, MakeSyncState(peerId));

            InvokeP2PCompletePeerSync(host, peerId, states[peerId]);

            Assert.AreEqual(2, P2PPlayers(host).Count, "host + validated guest");
            var guest = (IPlayerInfo)P2PPlayers(host)[1];
            Assert.AreEqual("Guest1", guest.DisplayName, "guest validated DisplayName (claimed/fabricated ignored)");
            Assert.AreEqual("gacc", guest.Account, "guest validated Account");
        }

        [Test] // validator accepts with EMPTY name → fabricated $"Player{id}", claimed ignored, Account ""
        public void P2P_GuestAccept_EmptyName_FabricatesIgnoringClaimed()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, new MockValidator(Mode.AcceptSync, account: "", displayName: ""), null);
            int peerId = 5;
            var states = (IDictionary)_p2pSyncStates.GetValue(host);
            states.Add(peerId, MakeSyncState(peerId));
            // A claimed name is present but MUST be ignored when a validator ran.
            ((IDictionary)_p2pClaimedNames.GetValue(host))[peerId] = "ClaimedNick";

            InvokeP2PCompletePeerSync(host, peerId, states[peerId]);

            Assert.AreEqual(2, P2PPlayers(host).Count);
            var guest = (IPlayerInfo)P2PPlayers(host)[1];
            Assert.AreEqual($"Player{guest.PlayerId}", guest.DisplayName, "empty validated name → fabricated (NOT claimed)");
            Assert.AreNotEqual("ClaimedNick", guest.DisplayName, "claimed name ignored when validator ran (D17)");
            Assert.AreEqual(string.Empty, guest.Account, "no validated account → empty (claimed account never honored)");
        }

        // ── Reconnect no-revalidation ──────────────────────────────────

        [Test] // reconnect does NOT re-invoke the validator; identity preserved
        public void SD_Reconnect_DoesNotRevalidate()
        {
            // A validator is configured, but the reconnect path must never consult it.
            var mock = new MockValidator(Mode.AcceptSync, account: "acc", displayName: "Restored");
            _sd.SetIdentityValidator(mock);

            // Seed an already-admitted-then-disconnected player (playerId 1) directly.
            const int playerId = 1;
            var players = (IList)_sdPlayers.GetValue(_sd);
            players.Add(new PlayerInfo
            {
                PlayerId = playerId,
                DisplayName = "Restored",
                Account = "acc",
                ConnectionState = PlayerConnectionState.Disconnected,
            });
            var info = _sdRentDisconnected.Invoke(_sd, null);
            var infoType = info.GetType();
            infoType.GetField("PlayerId").SetValue(info, playerId);
            infoType.GetField("DisconnectTimeMs").SetValue(info, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            infoType.GetField("DeviceId").SetValue(info, string.Empty);
            _sdDisconnectedCount.SetValue(_sd, 1);

            long magic = (long)_sdSessionMagic.GetValue(_sd);
            var req = new ReconnectRequestMessage { SessionMagic = magic, PlayerId = playerId, DeviceId = string.Empty };

            try { _sdHandleReconnect.Invoke(_sd, new object[] { 42, req }); }
            catch (TargetInvocationException) { /* ReconnectAccept/FullState tail may throw without engine */ }

            Assert.AreEqual(0, mock.BeginCount, "reconnect must NOT invoke the validator (D20 no-revalidation)");
            var restored = (IPlayerInfo)players[0];
            Assert.AreEqual("Restored", restored.DisplayName, "identity preserved across reconnect");
            Assert.AreEqual("acc", restored.Account, "account preserved across reconnect");
        }

        // ── Real Ed25519 validator integration ──────────────
        // Reuses the P2P harness above with the real P2pEd25519IdentityValidator + DevLobbyTicketIssuer
        // (BouncyCastle) instead of the mock. Verdicts themselves are unit-tested in
        // P2pSignatureValidatorTests; here we confirm they route correctly through host self-validation /
        // CompletePeerSync and that the session-scoped nonce survives across joins. Dev keypair = seed
        // 0x01..0x20 → derived public key (RFC 8032 deterministic; matches P2pDevIdentity).

        private const string _itSid = "itest-match";
        private const long _itNow = 1_700_000_000_000L;
        private static readonly byte[] _itSeed =
        {
            0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,0x10,
            0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f,0x20,
        };
        private static readonly byte[] _itPub =
        {
            0x79,0xb5,0x56,0x2e,0x8f,0xe6,0x54,0xf9,0x40,0x78,0xb1,0x12,0xe8,0xa9,0x8b,0xa7,
            0x90,0x1f,0x85,0x3a,0xe6,0x95,0xbe,0xd7,0xe0,0xe3,0x91,0x0b,0xad,0x04,0x96,0x64,
        };

        private static string MintTicket(string account, string displayName, string nonce, long? expiresAt = null)
            => new DevLobbyTicketIssuer(new PureEd25519Backend(), _itSeed)
                .Issue(new LobbyTicket(account, displayName, _itSid, _itNow, expiresAt ?? (_itNow + 60_000), nonce));

        private static P2pEd25519IdentityValidator RealValidator()
            => new P2pEd25519IdentityValidator(_itPub, _itSid, () => _itNow, new PureEd25519Backend());

        private sealed class CountingValidator : IPlayerIdentityValidator
        {
            private readonly IPlayerIdentityValidator _inner;
            public int Count;
            public CountingValidator(IPlayerIdentityValidator inner) { _inner = inner; }
            public IIdentityValidation BeginValidate(in IdentityValidationRequest request)
            {
                Count++;
                return _inner.BeginValidate(request);
            }
        }

        [Test] // host self: a real validated ticket overlays DisplayName/Account
        public void P2P_Real_HostSelf_Accept_UsesTicketIdentity()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, RealValidator(), MintTicket("hostacc", "HostName", "host-n1"));
            var hp = (IPlayerInfo)P2PPlayers(host)[0];
            Assert.AreEqual("HostName", hp.DisplayName);
            Assert.AreEqual("hostacc", hp.Account);
        }

        [Test] // host self: expired ticket rejected, but host cannot kick itself → "Host" fallback
        public void P2P_Real_HostSelf_Expired_KeepsHostFallback()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, RealValidator(), MintTicket("hostacc", "HostName", "host-n2", expiresAt: _itNow - 1));
            var hp = (IPlayerInfo)P2PPlayers(host)[0];
            Assert.AreEqual("Host", hp.DisplayName, "host self-reject keeps the \"Host\" fallback (cannot kick itself)");
            Assert.AreEqual(string.Empty, hp.Account);
        }

        [Test] // semi-trust: the host is the single verifier; its validated value is the propagation source
        public void P2P_Real_SemiTrust_HostValidatesGuestOnce()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var counting = new CountingValidator(RealValidator());
            var host = MakeP2PHost(t, counting, MintTicket("h", "H", "host-n3"));
            int afterHostSelf = counting.Count; // host self-validation already ran in CreateRoom

            int peerId = 5;
            var states = (IDictionary)_p2pSyncStates.GetValue(host);
            states.Add(peerId, MakeSyncState(peerId));
            ((IDictionary)_p2pTickets.GetValue(host))[peerId] = MintTicket("gacc", "Guest", "g-n1");
            InvokeP2PCompletePeerSync(host, peerId, states[peerId]);

            Assert.AreEqual(2, P2PPlayers(host).Count, "guest admitted");
            Assert.AreEqual(afterHostSelf + 1, counting.Count, "host validates the guest exactly once (single authority)");
            var guest = (IPlayerInfo)P2PPlayers(host)[1];
            Assert.AreEqual("Guest", guest.DisplayName, "host-validated identity is the propagation source (guests trust it, no re-validation)");
            Assert.AreEqual("gacc", guest.Account);
        }

        [Test] // session-scoped nonce: replay rejected(9); a fresh ticket (re-auth) is admitted
        public void P2P_Real_NonceReplay_RejectedThenFreshAdmitted()
        {
            var t = new TestTransport(); t.Listen("p2p", 0, 4);
            var host = MakeP2PHost(t, RealValidator(), MintTicket("h", "H", "host-n4"));
            var states = (IDictionary)_p2pSyncStates.GetValue(host);
            var tickets = (IDictionary)_p2pTickets.GetValue(host);

            string shared = MintTicket("gacc", "Guest", "nonce-shared");
            states.Add(5, MakeSyncState(5)); tickets[5] = shared;
            InvokeP2PCompletePeerSync(host, 5, states[5]);
            Assert.AreEqual(2, P2PPlayers(host).Count, "first use admitted");

            int before = t.DisconnectPeerCallCount;
            states.Add(6, MakeSyncState(6)); tickets[6] = shared; // SAME ticket → replayed nonce
            InvokeP2PCompletePeerSync(host, 6, states[6]);
            Assert.AreEqual(2, P2PPlayers(host).Count, "replayed nonce not admitted (session-scoped)");
            Assert.AreEqual(before + 1, t.DisconnectPeerCallCount, "replay disconnects");
            Assert.IsFalse(states.Contains(6));

            states.Add(7, MakeSyncState(7)); tickets[7] = MintTicket("gacc", "Guest", "nonce-fresh"); // re-auth
            InvokeP2PCompletePeerSync(host, 7, states[7]);
            Assert.AreEqual(3, P2PPlayers(host).Count, "fresh ticket (new nonce) admitted — re-auth recovery");
        }
    }
}
