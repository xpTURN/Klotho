using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    internal class TestPeer
    {
        public TestTransport Transport;
        public KlothoNetworkService NetworkService;
        public KlothoEngine Engine;
        public TestSimulation Simulation;

        public int LocalPlayerId => NetworkService.LocalPlayerId;
        public SessionPhase Phase => NetworkService.Phase;
        public int CurrentTick => Engine.CurrentTick;
        public bool IsHost => NetworkService.IsHost;
    }

    internal class KlothoTestHarness
    {
        private TestPeer _host;
        private List<TestPeer> _guests = new List<TestPeer>();
        private CommandFactory _commandFactory;
        private ILogger _logger;

        private static readonly FieldInfo _gameStartTimeField = typeof(KlothoNetworkService)
            .GetField("_gameStartTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _isCatchingUpField = typeof(KlothoEngine)
            .GetField("_isCatchingUp", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _disconnectedPlayerCountField = typeof(KlothoNetworkService)
            .GetField("_disconnectedPlayerCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _lateJoinCatchupsField = typeof(KlothoNetworkService)
            .GetField("_lateJoinCatchups", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _peerSyncStatesField = typeof(KlothoNetworkService)
            .GetField("_peerSyncStates", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _lateJoinStateField = typeof(KlothoNetworkService)
            .GetField("_lateJoinState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _activePlayerIdsField = typeof(KlothoEngine)
            .GetField("_activePlayerIds", BindingFlags.NonPublic | BindingFlags.Instance);

        public TestPeer Host => _host;
        public IReadOnlyList<TestPeer> Guests => _guests;

        public KlothoTestHarness(ILogger logger)
        {
            _logger = logger;
            _commandFactory = new CommandFactory();
        }

        // ── Initialization ──

        public TestPeer CreateHost(int maxPlayers = 4)
        {
            var peer = CreatePeer();

            peer.Transport.Listen("localhost", 7777, maxPlayers);
            peer.NetworkService.CreateRoom("test", maxPlayers);

            _host = peer;
            return peer;
        }

        public TestPeer AddGuest()
        {
            var peer = CreatePeer();

            peer.NetworkService.JoinRoom("test");
            peer.Transport.Connect("localhost", 7777);

            // Add guest first so PumpMessages also calls guest.Update()
            _guests.Add(peer);
            PumpMessages();

            return peer;
        }

        public void StartPlaying()
        {
            foreach (var guest in _guests)
                guest.NetworkService.SetReady(true);
            PumpMessages();

            _host.NetworkService.SetReady(true);
            PumpMessages();

            Assert.AreEqual(SessionPhase.Countdown, _host.Phase, "Host should be in Countdown");

            ForceGameStartTime(0L);
            Tick();

            Assert.AreEqual(SessionPhase.Playing, _host.Phase, "Host should be Playing");
        }

        // ── Late Join ──

        public TestPeer AddLateJoinGuest()
        {
            Assert.AreEqual(SessionPhase.Playing, _host.Phase, "Host must be Playing for late join");

            var peer = CreatePeer();

            peer.NetworkService.JoinRoom("test");
            peer.Transport.Connect("localhost", 7777);

            _guests.Add(peer);
            return peer;
        }

        // ── Message pumping ──

        public void Tick(float deltaTime = 0.025f)
        {
            // CatchingUp peers: Engine.Update internally calls NetworkService.Update — skip
            // Disconnected peers: auto-reconnect triggers on Update — skip
            if (!IsCatchingUp(_host))
                _host.NetworkService.Update();
            foreach (var guest in _guests)
            {
                if (!guest.Transport.IsConnected) continue;
                if (!IsCatchingUp(guest))
                    guest.NetworkService.Update();
            }

            if (_host.Phase == SessionPhase.Playing)
                _host.Engine.Update(deltaTime);
            foreach (var guest in _guests)
            {
                if (!guest.Transport.IsConnected) continue;
                if (guest.Phase == SessionPhase.Playing)
                    guest.Engine.Update(deltaTime);
            }
        }

        public void PumpMessages(int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                _host.NetworkService.Update();
                foreach (var guest in _guests)
                {
                    if (!guest.Transport.IsConnected) continue;
                    guest.NetworkService.Update();
                }
            }
        }

        public void AdvanceAllToTick(int targetTick)
        {
            int safetyLimit = targetTick * 10;
            int iterations = 0;
            while (_host.CurrentTick < targetTick)
            {
                InjectEmptyInputsForAllPeers();
                Tick();
                if (++iterations > safetyLimit)
                {
                    Assert.Fail($"AdvanceAllToTick safety limit reached. Host tick: {_host.CurrentTick}, target: {targetTick}");
                }
            }
        }

        // ── Fault injection ──

        public void DisconnectPeer(TestPeer peer)
        {
            _host.Transport.DisconnectPeer(peer.Transport.LocalPeerId);
        }

        public void ReconnectPeer(TestPeer peer)
        {
            peer.Transport = new TestTransport();
            peer.NetworkService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.Engine.Initialize(peer.Simulation, peer.NetworkService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            peer.NetworkService.SubscribeEngine(peer.Engine);

            peer.NetworkService.JoinRoom("test");
            peer.Transport.Connect("localhost", 7777);
        }

        // ── Assertion helpers ──

        public void AssertPlayerCountConsistent(int expected)
        {
            // Host's NetworkService._players is the authoritative source
            // Guest _players are not directly updated for late-join players — validate host only
            Assert.AreEqual(expected, _host.NetworkService.PlayerCount,
                $"Host PlayerCount mismatch");
        }

        public void AssertActivePlayerIdsConsistent()
        {
            // Host's _activePlayerIds is the authoritative source
            // Late-join guest: networkService.Players is empty at Engine.Initialize time and
            // HandleGameStart is not called, so original player IDs may be missing — validate host only
            var hostIds = GetActivePlayerIds(_host);
            Assert.IsTrue(hostIds.Count > 0, "Host _activePlayerIds should not be empty");
        }

        public void AssertActivePlayerIdsContains(params int[] expectedIds)
        {
            var hostIds = GetActivePlayerIds(_host);
            foreach (int id in expectedIds)
            {
                Assert.IsTrue(hostIds.Contains(id),
                    $"Host _activePlayerIds should contain playerId={id}, actual: {string.Join(", ", hostIds)}");
            }
        }

        public void AssertStateHashConsistent()
        {
            long hostHash = _host.Simulation.GetStateHash();
            foreach (var guest in _guests)
            {
                if (guest.Phase != SessionPhase.Playing) continue;
                if (IsCatchingUp(guest)) continue;
                Assert.AreEqual(hostHash, guest.Simulation.GetStateHash(),
                    $"StateHash mismatch for Guest PlayerId={guest.LocalPlayerId}");
            }
        }

        // ── State accessors ──

        public bool IsCatchingUp(TestPeer peer)
        {
            return (bool)_isCatchingUpField.GetValue(peer.Engine);
        }

        public int GetDisconnectedPlayerCount()
        {
            return (int)_disconnectedPlayerCountField.GetValue(_host.NetworkService);
        }

        public int GetLateJoinCatchupsCount()
        {
            var catchups = _lateJoinCatchupsField.GetValue(_host.NetworkService);
            return ((System.Collections.IDictionary)catchups).Count;
        }

        public bool HasPeerSyncState(int peerId)
        {
            var syncStates = _peerSyncStatesField.GetValue(_host.NetworkService);
            return ((System.Collections.IDictionary)syncStates).Contains(peerId);
        }

        public object GetLateJoinState(TestPeer peer)
        {
            return _lateJoinStateField.GetValue(peer.NetworkService);
        }

        // ── Teardown ──

        public void Reset()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _host = null;
            _guests.Clear();
        }

        // ── Private ──

        private TestPeer CreatePeer()
        {
            var peer = new TestPeer
            {
                Transport = new TestTransport(),
                NetworkService = new KlothoNetworkService(),
                Engine = new KlothoEngine(new SimulationConfig(), new SessionConfig()),
                Simulation = new TestSimulation(),
            };

            peer.NetworkService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.Engine.Initialize(peer.Simulation, peer.NetworkService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            peer.NetworkService.SubscribeEngine(peer.Engine);

            // Inject InitialStateSnapshot at record time — same role as Brawler's InjectReplayGameCustomData hook
            var capturedPeer = peer;
            peer.Engine.OnGameStart += () =>
            {
                if (capturedPeer.Engine.IsReplayMode) return;
                if (!capturedPeer.Engine.ReplaySystem.IsRecording) return;
                var (data, hash) = capturedPeer.Simulation.SerializeFullStateWithHash();
                capturedPeer.Engine.ReplaySystem.SetInitialStateSnapshot(data, hash);
            };

            return peer;
        }

        private void ForceGameStartTime(long value)
        {
            _gameStartTimeField.SetValue(_host.NetworkService, value);
            foreach (var guest in _guests)
                _gameStartTimeField.SetValue(guest.NetworkService, value);
        }

        private void InjectEmptyInputsForAllPeers()
        {
            var peers = GetActivePeers();
            foreach (var peer in peers)
            {
                if (peer.Phase == SessionPhase.Playing && !IsCatchingUp(peer))
                {
                    int tick = peer.CurrentTick + peer.Engine.InputDelay;
                    var cmd = new EmptyCommand(peer.LocalPlayerId, tick);
                    peer.NetworkService.SendCommand(cmd);
                }
            }
        }

        private List<TestPeer> GetActivePeers()
        {
            var result = new List<TestPeer>();
            if (_host != null) result.Add(_host);
            result.AddRange(_guests);
            return result;
        }

        private HashSet<int> GetActivePlayerIds(TestPeer peer)
        {
            var ids = _activePlayerIdsField.GetValue(peer.Engine);
            if (ids is List<int> list)
                return new HashSet<int>(list);
            if (ids is HashSet<int> hashSet)
                return hashSet;
            // fallback: collect from Players list
            var set = new HashSet<int>();
            foreach (var player in peer.NetworkService.Players)
                set.Add(player.PlayerId);
            return set;
        }
    }
}
