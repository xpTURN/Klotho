using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Player count accounting tests (P2P / KlothoNetworkService).
    /// Covers EffectivePlayerCount, FindSmallestUnusedPlayerId, TryReservePlayerSlot,
    /// GameStart snapshot, Phase setter integration, race guard, stale-entry cleanup.
    ///
    /// Out of scope here:
    ///   - Race-fallback LateJoin retry — application-level (client retry policy
    ///     lives outside KlothoNetworkService).
    ///   - SD-only scenarios — require a ServerNetworkService harness.
    /// </summary>
    [TestFixture]
    public class PlayerCountAccountingTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        // Private field reflection
        private static readonly FieldInfo _gameStartedField = typeof(KlothoNetworkService)
            .GetField("_gameStarted", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _assignedPlayerIdCountField = typeof(KlothoNetworkService)
            .GetField("_assignedPlayerIdCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _nextPlayerIdField = typeof(KlothoNetworkService)
            .GetField("_nextPlayerId", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _playersField = typeof(KlothoNetworkService)
            .GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _peerSyncStatesField = typeof(KlothoNetworkService)
            .GetField("_peerSyncStates", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type _peerSyncStateType = typeof(KlothoNetworkService).Assembly
            .GetType("xpTURN.Klotho.Network.PeerSyncState");
        private static readonly MethodInfo _completePeerSyncMethod = typeof(KlothoNetworkService)
            .GetMethod("CompletePeerSync", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _tryReservePlayerSlotMethod = typeof(KlothoNetworkService)
            .GetMethod("TryReservePlayerSlot", BindingFlags.NonPublic | BindingFlags.Instance);

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        // ── Reflection accessors ─────────────────────────────────────

        private static bool GetGameStarted(KlothoNetworkService svc)
            => (bool)_gameStartedField.GetValue(svc);
        private static int GetAssignedPlayerIdCount(KlothoNetworkService svc)
            => (int)_assignedPlayerIdCountField.GetValue(svc);
        private static int GetNextPlayerId(KlothoNetworkService svc)
            => (int)_nextPlayerIdField.GetValue(svc);
        private static List<int> GetPlayerIds(KlothoNetworkService svc)
        {
            var list = (System.Collections.IList)_playersField.GetValue(svc);
            var ids = new List<int>();
            foreach (var p in list)
            {
                var pi = (IPlayerInfo)p;
                ids.Add(pi.PlayerId);
            }
            return ids;
        }

        private static System.Collections.IDictionary GetPeerSyncStates(KlothoNetworkService svc)
            => (System.Collections.IDictionary)_peerSyncStatesField.GetValue(svc);

        // Build a minimal PeerSyncState instance via reflection — runtime type is internal,
        // so the test cannot reference it by name.
        private static object MakePeerSyncState(int peerId, bool isLateJoin, bool completed)
        {
            var state = Activator.CreateInstance(_peerSyncStateType, nonPublic: true);
            _peerSyncStateType.GetField("PeerId").SetValue(state, peerId);
            _peerSyncStateType.GetField("IsLateJoin").SetValue(state, isLateJoin);
            _peerSyncStateType.GetField("Completed").SetValue(state, completed);
            _peerSyncStateType.GetField("RttSamples").SetValue(state, new long[5]);
            _peerSyncStateType.GetField("ClockOffsetSamples").SetValue(state, new long[5]);
            _peerSyncStateType.GetField("SyncPacketsSent").SetValue(state, 5);
            return state;
        }

        // ── Field/property/setter integration ──────────────

        [Test]
        public void CreateRoom_InitializesCountersToZero()
        {
            var host = _harness.CreateHost(4);

            Assert.IsFalse(GetGameStarted(host.NetworkService), "_gameStarted should be false at CreateRoom");
            Assert.AreEqual(0, GetAssignedPlayerIdCount(host.NetworkService), "_assignedPlayerIdCount should be 0");
            Assert.AreEqual(1, GetNextPlayerId(host.NetworkService), "_nextPlayerId should be 1 (stale, unused in Pre-GameStart)");
            Assert.AreEqual(4, host.NetworkService.MaxPlayerCapacity, "MaxPlayerCapacity should mirror MaxPlayers");
        }

        [Test]
        public void LeaveRoom_PhaseSetter_ResetsCountersOnDisconnected()
        {
            var host = _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.PumpMessages();
            _harness.StartPlaying();

            // Pre-state: counters non-zero post GameStart
            Assert.IsTrue(GetGameStarted(host.NetworkService));

            host.NetworkService.LeaveRoom();

            Assert.AreEqual(SessionPhase.Disconnected, host.NetworkService.Phase);
            Assert.IsFalse(GetGameStarted(host.NetworkService), "Phase setter Disconnected branch must reset _gameStarted");
            Assert.AreEqual(0, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(1, GetNextPlayerId(host.NetworkService));
        }

        // ── GameStart snapshot ──────────────────────────────

        [Test]
        public void GameStart_SnapshotInitializesCounters_DenseDistribution()
        {
            var host = _harness.CreateHost(4);
            _harness.AddGuest(); // PlayerId=1
            _harness.AddGuest(); // PlayerId=2
            _harness.PumpMessages();

            // Pre-GameStart — _nextPlayerId is stale at 1
            Assert.AreEqual(1, GetNextPlayerId(host.NetworkService), "Pre-GameStart should not bump _nextPlayerId (slot reuse path)");
            Assert.IsFalse(GetGameStarted(host.NetworkService));

            _harness.StartPlaying();

            Assert.IsTrue(GetGameStarted(host.NetworkService));
            Assert.AreEqual(3, GetAssignedPlayerIdCount(host.NetworkService), "snapshot = _players.Count");
            Assert.AreEqual(3, GetNextPlayerId(host.NetworkService), "max(0,1,2)+1 = 3");
        }

        [Test]
        public void GameStart_SnapshotRealignsNextPlayerId_AfterPreGameStartCycles()
        {
            // Pre-GameStart join+leave cycles should not inflate _nextPlayerId;
            // the GameStart snapshot must realign to max(PlayerId)+1.
            var host = _harness.CreateHost(4);

            // Multiple guests join then leave during Pre-GameStart
            var g1 = _harness.AddGuest(); // PlayerId=1
            _harness.AddGuest();          // PlayerId=2
            _harness.PumpMessages();
            _harness.DisconnectPeer(g1);
            _harness.PumpMessages();

            // Re-add a guest — Pre-GameStart smallest-unused should give PlayerId=1 again
            var g3 = _harness.AddGuest();
            _harness.PumpMessages();

            // _players is {host(0), g(2), g3(1)} in some order — max=2
            var ids = GetPlayerIds(host.NetworkService);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, ids,
                "Pre-GameStart should reuse PlayerId=1 (smallest unused), not allocate 3");

            // _nextPlayerId still stale (Pre-GameStart never bumps it)
            Assert.AreEqual(1, GetNextPlayerId(host.NetworkService));

            _harness.StartPlaying();

            // Snapshot realigns
            Assert.AreEqual(3, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(3, GetNextPlayerId(host.NetworkService), "snapshot must = max(PlayerId)+1, not stale 1");
        }

        // ── Pre-GameStart slot reuse ────────────────────────

        [Test]
        public void PreGameStart_GuestLeaveFreesSlot_NewGuestCanJoin()
        {
            // Ready-before-leave should NOT permanently consume the slot.
            var host = _harness.CreateHost(2);
            var g1 = _harness.AddGuest();
            _harness.PumpMessages();

            Assert.AreEqual(2, host.NetworkService.PlayerCount, "host + g1");

            _harness.DisconnectPeer(g1);
            _harness.PumpMessages();

            Assert.AreEqual(1, host.NetworkService.PlayerCount, "g1 leave should free the slot");

            // New guest can take the freed slot
            var g2 = _harness.AddGuest();
            _harness.PumpMessages();

            Assert.AreEqual(2, host.NetworkService.PlayerCount, "g2 should be admitted into the freed slot");
            Assert.AreEqual(1, g2.NetworkService.LocalPlayerId, "smallest unused = 1 (host=0)");
        }

        [Test]
        public void PreGameStart_ReassignsSmallestUnusedId()
        {
            // host(0)+g1(1)+g2(2) → g1 leaves → g3 joins → gets PlayerId=1.
            var host = _harness.CreateHost(4);
            var g1 = _harness.AddGuest(); // PlayerId=1
            _harness.AddGuest();          // PlayerId=2
            _harness.PumpMessages();

            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, GetPlayerIds(host.NetworkService));

            _harness.DisconnectPeer(g1);
            _harness.PumpMessages();

            var g3 = _harness.AddGuest();
            _harness.PumpMessages();

            Assert.AreEqual(1, g3.NetworkService.LocalPlayerId,
                "FindSmallestUnusedPlayerId should pick 1 (smallest unused, since 0=host, 2=g2)");
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, GetPlayerIds(host.NetworkService));
        }

        // ── Capacity gates ───────────────────────────────────────────

        [Test]
        public void PreGameStart_GateRejectsBeyondCapacity()
        {
            // Pre-GameStart variant — fill MaxPlayers, next join rejected.
            var host = _harness.CreateHost(2);
            _harness.AddGuest(); // host(0) + g1(1) — full
            _harness.PumpMessages();

            Assert.AreEqual(2, host.NetworkService.PlayerCount);

            // Attempt another guest — should be rejected by gate before handshake completes
            _harness.AddGuest();
            _harness.PumpMessages();

            // Authoritative check: host-side state. Rejected peer's LocalPlayerId is unreliable
            // (handshake never completes, so HandleSyncComplete never fires).
            Assert.AreEqual(2, host.NetworkService.PlayerCount, "host PlayerCount must remain at MaxPlayerCapacity=2");
            Assert.AreEqual(2, GetPlayerIds(host.NetworkService).Count, "_players list must remain at 2");
        }

        // ── Sparse distribution invariants ────────────

        [Test]
        public void Sparse_GameStart_LateJoinReachesMaxPlayerCapacity_NoBotCollision()
        {
            // host(0)+g1(1)+g3(3) sparse → GameStart → LateJoin gets PlayerId=4=MaxPlayerCapacity.
            // P2P real player range becomes [0, MaxPlayerCapacity], but the bot range
            // [MaxPlayerCapacity+1, ...] stays disjoint.
            var host = _harness.CreateHost(4);
            _harness.AddGuest(); // PlayerId=1
            var g2 = _harness.AddGuest(); // PlayerId=2
            _harness.AddGuest(); // PlayerId=3
            _harness.PumpMessages();

            // Disconnect g2 (PlayerId=2) — Pre-GameStart removes immediately
            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();

            CollectionAssert.AreEquivalent(new[] { 0, 1, 3 }, GetPlayerIds(host.NetworkService),
                "Pre-GameStart leave with sparse distribution");

            _harness.StartPlaying();

            // Snapshot: _assignedPlayerIdCount=3, _nextPlayerId=max(0,1,3)+1=4
            Assert.AreEqual(3, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(4, GetNextPlayerId(host.NetworkService));

            // LateJoin: Math.Max(3, 3) + 0 = 3 < 4 → allow, newId = _nextPlayerId++ = 4
            var lateJoiner = _harness.AddLateJoinGuest();
            _harness.PumpMessages();

            Assert.AreEqual(4, lateJoiner.NetworkService.LocalPlayerId,
                "LateJoin should get PlayerId=MaxPlayerCapacity (=4) under sparse Pre-GameStart");

            // Bot range starts at MaxPlayerCapacity+1=5. Verify no overlap.
            const int expectedBot0 = 4 /*MaxPlayerCapacity*/ + 1;
            Assert.AreNotEqual(expectedBot0, lateJoiner.NetworkService.LocalPlayerId,
                "LateJoiner PlayerId must not collide with bot[0]");
        }

        [Test]
        public void Sparse_MultiGap_BlocksSecondLateJoin_AtCriticalBoundary()
        {
            // P2P MaxPlayerCapacity=4 with {host(0), g1(1), g2(2), g3(3)} → leave g1+g2 →
            //   _players={0,3} → GameStart → snapshot _assignedPlayerIdCount=2, _nextPlayerId=4.
            //   1st LateJoin: Math.Max(2,3)+0=3 < 4 → allow (newId=4)
            //   2nd LateJoin: Math.Max(3,4)+0=4 >= 4 → reject (without Math.Max, would have allowed → newId=5=bot[0]).
            var host = _harness.CreateHost(4);
            var g1 = _harness.AddGuest();
            var g2 = _harness.AddGuest();
            _harness.AddGuest(); // g3 PlayerId=3
            _harness.PumpMessages();
            _harness.DisconnectPeer(g1);
            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();

            CollectionAssert.AreEquivalent(new[] { 0, 3 }, GetPlayerIds(host.NetworkService));

            _harness.StartPlaying();
            Assert.AreEqual(2, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(4, GetNextPlayerId(host.NetworkService));

            // 1st LateJoin allowed → PlayerId=4
            var late1 = _harness.AddLateJoinGuest();
            _harness.PumpMessages();
            Assert.AreEqual(4, late1.NetworkService.LocalPlayerId, "1st LateJoin should get PlayerId=4");
            Assert.AreEqual(3, host.NetworkService.PlayerCount, "after late1: host(0)+g3(3)+late1(4)");
            Assert.AreEqual(3, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(5, GetNextPlayerId(host.NetworkService));

            // 2nd LateJoin must be rejected — Math.Max(3, 4) = 4 >= MaxPlayerCapacity
            var late2 = _harness.AddLateJoinGuest();
            _harness.PumpMessages();

            // Sparse distribution: capacity gate triggers below _players.Count==MaxPlayerCapacity.
            // _assignedPlayerIdCount must remain 3 (no slot reserved for late2).
            Assert.AreEqual(3, host.NetworkService.PlayerCount,
                "2nd LateJoin must be rejected — host PlayerCount stays at 3 (sparse capacity loss)");
            Assert.AreEqual(3, GetAssignedPlayerIdCount(host.NetworkService),
                "2nd LateJoin reject must NOT consume a slot");
            Assert.AreNotEqual(5, late2.NetworkService.LocalPlayerId,
                "2nd LateJoin must NOT receive PlayerId=5 (= bot[0] range — Critical regression)");
        }

        // ── Race guard / stale entry cleanup ───────────────

        [Test]
        public void RaceGuard_CompletePeerSyncAfterGameStart_RejectsStandardHandshake()
        {
            // Peer A starts a standard handshake → StartGame() fires while in flight →
            // peer A's final SyncReply arrives → CompletePeerSync entered with _gameStarted=true.
            // The race guard must reject without consuming a slot.
            //
            // Direct invocation via reflection — simulating the precise SyncReply timing through
            // PumpMessages alone is brittle; this drives the guard's exact preconditions.
            var host = _harness.CreateHost(4);
            _harness.AddGuest(); // PlayerId=1 — real handshake completes
            _harness.PumpMessages();

            int playerCountBefore = host.NetworkService.PlayerCount;
            int assignedBefore = GetAssignedPlayerIdCount(host.NetworkService);

            // Fabricate a mid-flight standard handshake entry
            const int fakePeerId = 99;
            var state = MakePeerSyncState(fakePeerId, isLateJoin: false, completed: false);
            var peerSyncStates = GetPeerSyncStates(host.NetworkService);
            peerSyncStates.Add(fakePeerId, state);

            // Simulate StartGame() flipping _gameStarted while the handshake is in flight
            _gameStartedField.SetValue(host.NetworkService, true);

            // Simulate SyncReply arrival → CompletePeerSync invoked
            _completePeerSyncMethod.Invoke(host.NetworkService, new[] { (object)fakePeerId, state });

            // Race guard: stale entry removed, no slot consumed
            Assert.IsFalse(peerSyncStates.Contains(fakePeerId),
                "Race guard must remove _peerSyncStates entry immediately");
            Assert.AreEqual(playerCountBefore, host.NetworkService.PlayerCount,
                "Race-rejected peer must NOT be added to _players");
            Assert.AreEqual(assignedBefore, GetAssignedPlayerIdCount(host.NetworkService),
                "Race-rejected peer must NOT consume a slot");
        }

        [Test]
        public void TryReservePlayerSlot_PostGameStartOverflow_RemovesStaleEntryImmediately()
        {
            // TryReservePlayerSlot Post-GameStart overflow branch → reject must
            // remove the _peerSyncStates entry synchronously (before the transport disconnect callback),
            // so CountPendingHandshakes immediately drops and the next peer's gate evaluates correctly.
            //
            // Reaches the overflow branch by direct invocation with a fabricated state — the
            // production capacity gate prevents reaching it under normal flow, by design.
            var host = _harness.CreateHost(2);
            _harness.AddGuest(); // host(0) + g1(1) — full at GameStart
            _harness.PumpMessages();
            _harness.StartPlaying();

            // Post-GameStart: _assignedPlayerIdCount=2, _nextPlayerId=2, MaxPlayerCapacity=2
            //   Math.Max(2, 1) = 2 >= 2 → overflow branch
            const int fakePeerId = 99;
            var state = MakePeerSyncState(fakePeerId, isLateJoin: true, completed: false);
            var peerSyncStates = GetPeerSyncStates(host.NetworkService);
            peerSyncStates.Add(fakePeerId, state);

            int pendingBefore = peerSyncStates.Count;
            Assert.IsTrue(peerSyncStates.Contains(fakePeerId), "precondition: stale entry present");

            // TryReservePlayerSlot's reject path emits ZLogError — declare expectation so the
            // Unity Test Runner doesn't treat it as an unhandled error.
            LogAssert.Expect(LogType.Error,
                new Regex(@"\[KlothoNetworkService\] Post-GameStart slot overflow"));

            var args = new object[] { fakePeerId, /*newPlayerId out*/ 0 };
            bool result = (bool)_tryReservePlayerSlotMethod.Invoke(host.NetworkService, args);

            Assert.IsFalse(result, "Post-GameStart overflow must reject");
            Assert.IsFalse(peerSyncStates.Contains(fakePeerId),
                "Stale entry must be removed synchronously");
            Assert.AreEqual(pendingBefore - 1, peerSyncStates.Count,
                "_peerSyncStates count must drop by exactly the rejected entry");
            Assert.AreEqual(2, GetAssignedPlayerIdCount(host.NetworkService),
                "Reject path must NOT increment _assignedPlayerIdCount");
        }

        // ── Post-GameStart full room rejects LateJoin ───────

        [Test]
        public void FullRoom_LateJoin_Rejected_AtCapacity()
        {
            // Post-GameStart variant — full at GameStart, no sparse → LateJoin reject.
            var host = _harness.CreateHost(2);
            _harness.AddGuest(); // host(0) + g1(1)
            _harness.PumpMessages();
            _harness.StartPlaying();

            Assert.AreEqual(2, GetAssignedPlayerIdCount(host.NetworkService));
            Assert.AreEqual(2, GetNextPlayerId(host.NetworkService));

            _harness.AddLateJoinGuest();
            _harness.PumpMessages();

            Assert.AreEqual(2, host.NetworkService.PlayerCount, "PlayerCount must stay at MaxPlayerCapacity");
            Assert.AreEqual(2, GetAssignedPlayerIdCount(host.NetworkService), "_assignedPlayerIdCount unchanged");
        }
    }
}
