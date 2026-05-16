using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;
using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Regression guards for reconnect-related invariants.
    /// - Test harness: ReconnectPeer must invoke engine.SeedReconnectFullState.
    /// - Production: Reconnect must not trigger PresumedDrop false-positive, and the
    ///   cold-start LateJoin path must preserve its IsReconnect=false semantics.
    /// </summary>
    [TestFixture]
    internal class KlothoReconnectInvariantTests
    {
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("KlothoReconnectInvariantTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        // ── Test harness lint ──

        [Test]
        public void ReconnectPeer_CallsSeedReconnectFullState()
        {
            // Source-level lint: ReconnectPeer must invoke engine.SeedReconnectFullState.
            // Prevents regression to the dead-path (HandleFullStateReceived Resync branch only)
            // which leaves State at WaitingForPlayers.
            string harnessPath = Path.Combine(
                Application.dataPath, "Klotho", "Tests", "Helpers", "KlothoTestHarness.cs");
            Assert.IsTrue(File.Exists(harnessPath),
                $"KlothoTestHarness.cs not found: {harnessPath}");

            string content = File.ReadAllText(harnessPath);
            // Strict pattern — matches `engine.SeedReconnectFullState(...)` and
            // `peer.Engine.SeedReconnectFullState(...)`. Excludes comments and unrelated identifiers.
            int seedCallCount = Regex.Matches(content,
                @"\b(?:engine|\.Engine)\s*\.\s*SeedReconnectFullState\s*\(").Count;

            Assert.GreaterOrEqual(seedCallCount, 1,
                "ReconnectPeer must call engine.SeedReconnectFullState.");
        }

        // ── Reconnect path must not trigger PresumedDrop fp ──

        [Test]
        public void Reconnect_DoesNotTriggerPresumedDropFalsePositive()
        {
            // E2E: disconnect 5s + reconnect with QuorumMissDropTicks=10 (catchup latency > threshold).
            // Asserts host's _presumedDropFalsePositiveCount delta == 0 across the reconnect cycle.
            // Pre-fix: host's inject lifecycle race triggered the watchdog once during the
            // catchup gap window, yielding fp=1.
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = 10,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();

                const int baselineTick = 50;
                harness.AdvanceAllToTick(baselineTick);

                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                int disconnectTicks = (int)(5.0f * 1000f / simConfig.TickIntervalMs);
                int stalledEndTick = baselineTick + disconnectTicks;

                harness.DisconnectPeer(guest);
                harness.AdvanceWithStalledPeer(stalledEndTick, guest.LocalPlayerId);

                harness.ReconnectPeer(guest);
                harness.PumpMessages(20);

                int recoverEndTick = stalledEndTick + 100;
                harness.AdvanceAllToTick(recoverEndTick);

                int fpAfter = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                Assert.AreEqual(fpBefore, fpAfter,
                    $"Reconnect path must not trigger PresumedDrop watchdog activation. " +
                    $"fp delta = {fpAfter - fpBefore} (catchup-window race regression).");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── Cold-start LateJoin must preserve IsReconnect=false ──

        [Test]
        public void LateJoinCatchup_InjectGateUnchanged_ColdStartJoinTick()
        {
            // Asserts that for cold-start LateJoin, the _lateJoinCatchups entry has IsReconnect=false.
            // This preserves the original JoinTick gate (the IsReconnect branch routes cold-start
            // to the JoinTick path) — pre-join input is semantically undefined for a never-joined player.
            var simConfig = new SimulationConfig { TickIntervalMs = 50 };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(4);
                harness.AddGuest();
                harness.StartPlaying();
                harness.AdvanceAllToTick(50);

                var lateJoinGuest = harness.AddLateJoinGuest();
                harness.PumpMessages(20);

                // Reflection access — _lateJoinCatchups is Dictionary<int, LateJoinCatchupInfo>
                // (internal), accessed via the test assembly's InternalsVisibleTo path.
                var field = typeof(KlothoNetworkService).GetField(
                    "_lateJoinCatchups", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, "_lateJoinCatchups field not found via reflection");

                var catchups = field.GetValue(harness.Host.NetworkService) as IDictionary<int, LateJoinCatchupInfo>;
                Assert.IsNotNull(catchups, "_lateJoinCatchups field cast failed");

                var info = catchups.Values.FirstOrDefault(c => c.PlayerId == lateJoinGuest.LocalPlayerId);
                Assert.IsNotNull(info,
                    $"LateJoinCatchupInfo entry not found for playerId={lateJoinGuest.LocalPlayerId}");
                Assert.IsFalse(info.IsReconnect,
                    "Cold-start LateJoin must have IsReconnect=false — preserves the JoinTick gate " +
                    "in InjectCatchupPlayerInputs.");
            }
            finally
            {
                harness.Reset();
            }
        }

        // ── Abort during catchup must clear watchdog suppression ──

        [Test]
        public void Reconnect_AbortedByTransportDisconnect_WatchdogReactivates()
        {
            // Edge case: if the guest disconnects again DURING the catchup window
            // (between HandleReconnectRequest and JoinTick),
            // HandlePeerDisconnected (Handshake.cs:296) must remove the _lateJoinCatchups entry.
            // After this, IsPlayerInActiveCatchup returns false → watchdog suppression is correctly
            // LIFTED, allowing normal disconnect handling to take over.
            //
            // Verifies:
            //   (a) _lateJoinCatchups entry removed after abort
            //   (b) no PresumedDrop fp regression (real disconnect, not false positive)
            //   (c) host marks player as Disconnected
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = 10,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.AdvanceAllToTick(50);

                // First disconnect — normal stall path.
                harness.DisconnectPeer(guest);
                harness.AdvanceWithStalledPeer(150, guest.LocalPlayerId);

                int fpBefore = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                // First reconnect — populates _lateJoinCatchups on host.
                harness.ReconnectPeer(guest);
                harness.PumpMessages(20);

                // Second disconnect — aborts the reconnect flow on host's side.
                // HandlePeerDisconnected must remove the _lateJoinCatchups entry, clearing the
                // watchdog suppression for this player.
                harness.DisconnectPeer(guest);
                harness.PumpMessages(20);

                harness.AdvanceWithStalledPeer(250, guest.LocalPlayerId);

                int fpAfter = harness.Host.NetworkService.PresumedDropFalsePositiveCount;

                // (a) _lateJoinCatchups entry removed.
                var field = typeof(KlothoNetworkService).GetField(
                    "_lateJoinCatchups", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, "_lateJoinCatchups field not found via reflection");
                var catchups = field.GetValue(harness.Host.NetworkService) as IDictionary<int, LateJoinCatchupInfo>;
                Assert.IsNotNull(catchups);
                bool stillTracked = catchups.Values.Any(c => c.PlayerId == guest.LocalPlayerId);
                Assert.IsFalse(stillTracked,
                    "After abort during catchup, _lateJoinCatchups must no longer track the aborted peer " +
                    "(otherwise IsPlayerInActiveCatchup stays true and watchdog suppression becomes permanent).");

                // (b) no fp regression — real disconnect should not bump the false-positive counter.
                Assert.AreEqual(fpBefore, fpAfter,
                    $"Abort during catchup must not cause PresumedDrop fp regression. " +
                    $"fp delta = {fpAfter - fpBefore}.");

                // (c) host marks player Disconnected.
                IPlayerInfo hostPlayer = null;
                for (int i = 0; i < harness.Host.NetworkService.Players.Count; i++)
                {
                    if (harness.Host.NetworkService.Players[i].PlayerId == guest.LocalPlayerId)
                    {
                        hostPlayer = harness.Host.NetworkService.Players[i];
                        break;
                    }
                }
                Assert.IsNotNull(hostPlayer, $"Host's player entry not found for pid={guest.LocalPlayerId}");
                Assert.AreEqual(PlayerConnectionState.Disconnected, hostPlayer.ConnectionState,
                    "After abort, host must mark player as Disconnected for normal disconnect handling.");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
