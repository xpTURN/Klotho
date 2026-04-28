using System;
using System.Collections;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.LiteNetLib;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// LiteNetLibTransport real UDP integration test.
    /// Configuration: Host 1 + Client 3 (4P).
    /// </summary>
    [TestFixture]
    public class LiteNetLibIntegrationTests
    {
        private const int Port = 19701;
        private const string Address = "127.0.0.1";

        private LiteNetLibTransport _hostTransport;
        private LiteNetLibTransport _client1Transport;
        private LiteNetLibTransport _client2Transport;
        private LiteNetLibTransport _client3Transport;

        private KlothoNetworkService _hostService;
        private KlothoNetworkService _client1Service;
        private KlothoNetworkService _client2Service;
        private KlothoNetworkService _client3Service;

        private CommandFactory _commandFactory;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory setup (same as ZLogger)
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
            _commandFactory = new CommandFactory();

            _hostTransport    = new LiteNetLibTransport(_logger);
            _client1Transport = new LiteNetLibTransport(_logger);
            _client2Transport = new LiteNetLibTransport(_logger);
            _client3Transport = new LiteNetLibTransport(_logger);

            _hostService    = new KlothoNetworkService();
            _client1Service = new KlothoNetworkService();
            _client2Service = new KlothoNetworkService();
            _client3Service = new KlothoNetworkService();

            _hostService.Initialize(_hostTransport,       _commandFactory, _logger);
            _client1Service.Initialize(_client1Transport, _commandFactory, _logger);
            _client2Service.Initialize(_client2Transport, _commandFactory, _logger);
            _client3Service.Initialize(_client3Transport, _commandFactory, _logger);
        }

        [TearDown]
        public void TearDown()
        {
            _hostTransport.Disconnect();
            _client1Transport.Disconnect();
            _client2Transport.Disconnect();
            _client3Transport.Disconnect();
        }

        #region Helpers

        private void HostCreateRoom()
        {
            _hostTransport.Listen(Address, Port, 4);
            _hostService.CreateRoom("test4p", 4);
        }

        private void AllClientsJoin()
        {
            _client1Service.JoinRoom("test4p");
            _client1Transport.Connect(Address, Port);

            _client2Service.JoinRoom("test4p");
            _client2Transport.Connect(Address, Port);

            _client3Service.JoinRoom("test4p");
            _client3Transport.Connect(Address, Port);
        }

        private void PumpAll(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                _hostService.Update();
                _client1Service.Update();
                _client2Service.Update();
                _client3Service.Update();
            }
        }

        private IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds = 5f)
        {
            float elapsed = 0f;
            while (!condition() && elapsed < timeoutSeconds)
            {
                PumpAll();
                yield return null;
                elapsed += UnityEngine.Time.deltaTime;
            }
        }

        #endregion

        #region Handshake

        [UnityTest]
        public IEnumerator Handshake_4P_AllSynchronized()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            Assert.AreEqual(SessionPhase.Synchronized, _hostService.Phase);
            Assert.AreEqual(SessionPhase.Synchronized, _client1Service.Phase);
            Assert.AreEqual(SessionPhase.Synchronized, _client2Service.Phase);
            Assert.AreEqual(SessionPhase.Synchronized, _client3Service.Phase);
        }

        [UnityTest]
        public IEnumerator Handshake_4P_PlayerIdsAssigned()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            Assert.AreEqual(0, _hostService.LocalPlayerId);
            Assert.IsTrue(_hostService.IsHost);

            // Client PlayerIds are 1~3 (assigned in order, may vary by connection order)
            int c1Id = _client1Service.LocalPlayerId;
            int c2Id = _client2Service.LocalPlayerId;
            int c3Id = _client3Service.LocalPlayerId;

            Assert.AreNotEqual(0, c1Id);
            Assert.AreNotEqual(0, c2Id);
            Assert.AreNotEqual(0, c3Id);
            Assert.AreNotEqual(c1Id, c2Id);
            Assert.AreNotEqual(c2Id, c3Id);
            Assert.AreNotEqual(c1Id, c3Id);
        }

        [UnityTest]
        public IEnumerator Handshake_4P_HostHas4Players()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() => _hostService.PlayerCount == 4);

            Assert.AreEqual(4, _hostService.PlayerCount);
        }

        [UnityTest]
        public IEnumerator Handshake_4P_OnPlayerJoined_FiresThreeTimes()
        {
            HostCreateRoom();

            int joinCount = 0;
            _hostService.OnPlayerJoined += _ => joinCount++;

            AllClientsJoin();

            yield return WaitUntil(() => joinCount >= 3);

            Assert.AreEqual(3, joinCount);
        }

        #endregion

        #region Ready → GameStart

        [UnityTest]
        public IEnumerator GameStart_4P_AllCountdown()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            // All clients Ready
            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);

            // Pump so the Ready message reaches the Host
            yield return WaitUntil(() => false, 0.5f); // 0.5s pumping

            // Host Ready → AllReady → StartGame
            _hostService.SetReady(true);

            yield return WaitUntil(() => _hostService.Phase == SessionPhase.Countdown);

            Assert.AreEqual(SessionPhase.Countdown, _hostService.Phase);

            // Clients also receive GameStartMessage → Countdown
            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Countdown &&
                _client2Service.Phase == SessionPhase.Countdown &&
                _client3Service.Phase == SessionPhase.Countdown);

            Assert.AreEqual(SessionPhase.Countdown, _client1Service.Phase);
            Assert.AreEqual(SessionPhase.Countdown, _client2Service.Phase);
            Assert.AreEqual(SessionPhase.Countdown, _client3Service.Phase);
        }

        [UnityTest]
        public IEnumerator GameStart_4P_OnCountdownStarted_FiresOnAll()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            long hostStartTime   = 0;
            long client1StartTime = 0;
            long client2StartTime = 0;
            long client3StartTime = 0;

            _hostService.OnCountdownStarted    += t => hostStartTime   = t;
            _client1Service.OnCountdownStarted += t => client1StartTime = t;
            _client2Service.OnCountdownStarted += t => client2StartTime = t;
            _client3Service.OnCountdownStarted += t => client3StartTime = t;

            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);
            yield return WaitUntil(() => false, 0.5f);
            _hostService.SetReady(true);

            yield return WaitUntil(() =>
                client1StartTime > 0 && client2StartTime > 0 && client3StartTime > 0);

            Assert.Greater(hostStartTime, 0);
            Assert.AreEqual(hostStartTime, client1StartTime);
            Assert.AreEqual(hostStartTime, client2StartTime);
            Assert.AreEqual(hostStartTime, client3StartTime);
        }

        #endregion

        #region Command relay

        [UnityTest]
        public IEnumerator Command_HostReceivesFromClient()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);
            yield return WaitUntil(() => false, 0.5f);
            _hostService.SetReady(true);
            yield return WaitUntil(() => _hostService.Phase == SessionPhase.Countdown);

            int hostCount    = 0;
            int client1Count = 0;
            _hostService.OnCommandReceived    += _ => hostCount++;
            _client1Service.OnCommandReceived += _ => client1Count++;

            // Client1 sends a command
            var command = new MoveCommand(_client1Service.LocalPlayerId, 1, FPVector3.Zero);
            _client1Service.SendCommand(command);

            yield return WaitUntil(() => client1Count > 0 && hostCount > 0);

            Assert.AreEqual(1, client1Count, "Client1: received exactly once");
            Assert.AreEqual(1, hostCount,    "Host: received exactly once");

            // Verify no duplicate reception after additional pumping
            yield return WaitUntil(() => false, 0.3f);
            Assert.AreEqual(1, client1Count, "Client1: no duplicate reception");
            Assert.AreEqual(1, hostCount,    "Host: no duplicate reception");
        }

        [UnityTest]
        public IEnumerator Command_RelayedToAllClients()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);
            yield return WaitUntil(() => false, 0.5f);
            _hostService.SetReady(true);
            yield return WaitUntil(() => _hostService.Phase == SessionPhase.Countdown);

            int hostCount    = 0;
            int client2Count = 0;
            int client3Count = 0;
            _hostService.OnCommandReceived    += _ => hostCount++;
            _client2Service.OnCommandReceived += _ => client2Count++;
            _client3Service.OnCommandReceived += _ => client3Count++;

            // Client1 sends a command → Host relays → Client2, Client3 receive
            var command = new MoveCommand(_client1Service.LocalPlayerId, 1, FPVector3.Zero);
            _client1Service.SendCommand(command);

            yield return WaitUntil(() => hostCount > 0 && client2Count > 0 && client3Count > 0);

            Assert.AreEqual(1, hostCount,    "Host: received exactly once");
            Assert.AreEqual(1, client2Count, "Client2: received exactly once");
            Assert.AreEqual(1, client3Count, "Client3: received exactly once");

            // Verify no duplicate reception after additional pumping
            yield return WaitUntil(() => false, 0.3f);
            Assert.AreEqual(1, hostCount,    "Host: no duplicate reception");
            Assert.AreEqual(1, client2Count, "Client2: no duplicate reception");
            Assert.AreEqual(1, client3Count, "Client3: no duplicate reception");
        }

        [UnityTest]
        public IEnumerator Command_HostSendCommand_ReceivedByAllClients()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);
            yield return WaitUntil(() => false, 0.5f);
            _hostService.SetReady(true);
            yield return WaitUntil(() => _hostService.Phase == SessionPhase.Countdown);

            int hostCount    = 0;
            int client1Count = 0;
            int client2Count = 0;
            int client3Count = 0;

            _hostService.OnCommandReceived    += _ => hostCount++;
            _client1Service.OnCommandReceived += _ => client1Count++;
            _client2Service.OnCommandReceived += _ => client2Count++;
            _client3Service.OnCommandReceived += _ => client3Count++;

            var command = new MoveCommand(0, 2, FPVector3.Zero);
            _hostService.SendCommand(command);

            yield return WaitUntil(() =>
                hostCount > 0 && client1Count > 0 && client2Count > 0 && client3Count > 0);

            Assert.AreEqual(1, hostCount,    "Host: received exactly once");
            Assert.AreEqual(1, client1Count, "Client1: received exactly once");
            Assert.AreEqual(1, client2Count, "Client2: received exactly once");
            Assert.AreEqual(1, client3Count, "Client3: received exactly once");

            // Verify no duplicate reception after additional pumping
            yield return WaitUntil(() => false, 0.3f);
            Assert.AreEqual(1, hostCount,    "Host: no duplicate reception");
            Assert.AreEqual(1, client1Count, "Client1: no duplicate reception");
            Assert.AreEqual(1, client2Count, "Client2: no duplicate reception");
            Assert.AreEqual(1, client3Count, "Client3: no duplicate reception");
        }

        #endregion

        #region Ping / Pong

        private IEnumerator ReachPlayingPhase()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            _client1Service.SetReady(true);
            _client2Service.SetReady(true);
            _client3Service.SetReady(true);
            yield return WaitUntil(() => false, 0.5f);
            _hostService.SetReady(true);

            // Wait for Countdown (3s) to complete — actual elapsed time required since it is SharedTimeClock based
            yield return WaitUntil(() => _hostService.Phase == SessionPhase.Playing, 10f);
        }

        [UnityTest]
        public IEnumerator Ping_ClientPongUpdatesHostPing()
        {
            yield return ReachPlayingPhase();

            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase, "Playing phase precondition");

            // Wait for Ping interval (1s) + round trip
            yield return WaitUntil(() =>
            {
                var players = _hostService.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].PlayerId != 0 && players[i].Ping > 0)
                        return true;
                }
                return false;
            }, 5f);

            bool anyPingUpdated = false;
            foreach (var p in _hostService.Players)
            {
                if (p.PlayerId != 0 && p.Ping > 0)
                    anyPingUpdated = true;
            }
            Assert.IsTrue(anyPingUpdated, "Host's player.Ping should be updated after the client's Pong response");
        }

        [UnityTest]
        public IEnumerator Ping_NoDuplicatePongProcessed()
        {
            yield return ReachPlayingPhase();

            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase, "Playing phase precondition");

            // Wait for first Ping cycle
            yield return WaitUntil(() =>
            {
                var players = _hostService.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].PlayerId != 0 && players[i].Ping > 0)
                        return true;
                }
                return false;
            }, 5f);

            // Snapshot each client's Ping value
            int ping1Before = -1;
            foreach (var p in _hostService.Players)
            {
                if (p.PlayerId == _client1Service.LocalPlayerId)
                    ping1Before = p.Ping;
            }
            Assert.Greater(ping1Before, -1, "Client1 Ping should have an initial value");

            // Additional 0.3s pumping — must not jump due to duplicate Pong handling (no negative values or spikes)
            yield return WaitUntil(() => false, 0.3f);

            int ping1After = -1;
            foreach (var p in _hostService.Players)
            {
                if (p.PlayerId == _client1Service.LocalPlayerId)
                    ping1After = p.Ping;
            }
            Assert.GreaterOrEqual(ping1After, 0, "Ping value must not be negative");
        }

        #endregion

        #region Peer disconnect

        [UnityTest]
        public IEnumerator PeerDisconnect_HostRemovesPlayer()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() => _hostService.PlayerCount == 4);

            _client3Transport.Disconnect();

            yield return WaitUntil(() => _hostService.PlayerCount == 3);

            Assert.AreEqual(3, _hostService.PlayerCount);
        }

        [UnityTest]
        public IEnumerator PeerDisconnect_OnPlayerLeft_Fires()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() => _hostService.PlayerCount == 4);

            IPlayerInfo leftPlayer = null;
            _hostService.OnPlayerLeft += p => leftPlayer = p;

            _client2Transport.Disconnect();

            yield return WaitUntil(() => leftPlayer != null);

            Assert.IsNotNull(leftPlayer);
        }

        [UnityTest]
        public IEnumerator PeerDisconnect_ClientBecomesDisconnected()
        {
            HostCreateRoom();
            AllClientsJoin();

            yield return WaitUntil(() =>
                _client1Service.Phase == SessionPhase.Synchronized &&
                _client2Service.Phase == SessionPhase.Synchronized &&
                _client3Service.Phase == SessionPhase.Synchronized);

            _client1Transport.Disconnect();

            yield return WaitUntil(() => _client1Service.Phase == SessionPhase.Disconnected);

            Assert.AreEqual(SessionPhase.Disconnected, _client1Service.Phase);

            yield return new WaitForSeconds(1.0f);

            Assert.AreEqual(SessionPhase.Disconnected, _client1Service.Phase);
        }

        #endregion
    }

}
