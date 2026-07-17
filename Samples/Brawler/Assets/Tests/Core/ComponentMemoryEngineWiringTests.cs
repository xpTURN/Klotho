#pragma warning disable CS0067   // mock network-service events are declared to satisfy the interface, not raised
using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Engine-internal component-memory peak-sampler wiring (sample wiring lives in KlothoEngine):
    ///   - The gate (ISimulationConfig.ComponentMemoryPeakSampling AND simulation is EcsSimulation) decides arming.
    ///   - ArmComponentMemorySampler is called from BOTH Initialize bodies — network (3-arg) and network-less
    ///     (2-arg, replay/spectator). The network-less body has no hash-history arming, so a single call site
    ///     would silently miss replay/spectator; both bodies are covered below.
    ///   - Stop dumps the report once and disposes the sampler; a re-init re-arms a fresh one.
    ///
    /// The sampler's own fold/read-only behaviour is covered by ComponentMemoryAnalyzerTests; the
    /// OnFrameVerified verified-once contract the auto-subscribe relies on by ComponentMemoryPeakResimTests.
    /// Peak == verified-path peak across rollback/re-sim is a separate real-EcsSimulation-through-engine
    /// integration test and is not covered here.
    /// </summary>
    [TestFixture]
    public class ComponentMemoryEngineWiringTests
    {
        private const string MemTag = "[Mem] component-memory peak report";

        private static EcsSimulation MakeSim()
            => new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);

        private static SimulationConfig MakeConfig(bool sampling)
            => new SimulationConfig
            {
                ComponentMemoryPeakSampling = sampling,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
            };

        private static KlothoEngine MakeEngine(bool sampling)
            => new KlothoEngine(MakeConfig(sampling), new SessionConfig());

        private static int MemDumpCount(LogCapture c)
        {
            int n = 0;
            foreach (var e in c.Entries)
                if (e.Level == KLogLevel.Information && e.Message.Contains(MemTag)) n++;
            return n;
        }

        // Gate off: the sampler is never armed (no per-tick sampling, no dump).
        [Test]
        public void ET1_GateOff_DoesNotArm()
        {
            var engine = MakeEngine(sampling: false);
            engine.Initialize(MakeSim(), new LogCapture());

            Assert.IsNull(engine._memSampler, "gate off -> sampler not armed");
        }

        // Gate on arms via the 2-arg Initialize (network-less body — replay/spectator path). Regression
        // guard: the network-less body has no hash-history arming next to it, so it is easy to miss.
        [Test]
        public void ET2_GateOn_Arms_NetworklessBody()
        {
            var engine = MakeEngine(sampling: true);
            engine.Initialize(MakeSim(), new LogCapture());

            Assert.IsNotNull(engine._memSampler, "network-less body must arm (replay/spectator path)");
            Assert.AreEqual(0, engine._memSampler.ObservedTicks, "a freshly armed sampler has observed nothing yet");
        }

        // Gate on arms via the 3-arg Initialize (network body — authoritative peer path).
        [Test]
        public void ET2_GateOn_Arms_NetworkBody()
        {
            var engine = MakeEngine(sampling: true);
            engine.Initialize(MakeSim(), new MockNetworkService(), new LogCapture());

            Assert.IsNotNull(engine._memSampler, "network body must arm");
        }

        // Stop dumps the report exactly once and disposes the sampler. The peak column comes from the
        // accumulated PeakCounts; Sample(frame) is the exact operation the OnFrameVerified subscription runs.
        [Test]
        public void ET3_StopDumpsReportOnceAndDisposes()
        {
            var capture = new LogCapture();
            var engine = MakeEngine(sampling: true);
            var sim = MakeSim();
            engine.Initialize(sim, capture);
            Assert.IsNotNull(engine._memSampler);

            int ownerTid = ComponentStorageRegistry.GetTypeId<OwnerComponent>();
            for (int i = 0; i < 5; i++)
                sim.Frame.Add(sim.Frame.CreateEntity(), new OwnerComponent { OwnerId = i });
            engine._memSampler.Sample(sim.Frame);
            engine._memSampler.Sample(sim.Frame);

            Assert.AreEqual(2, engine._memSampler.ObservedTicks);
            Assert.AreEqual(5, engine._memSampler.PeakCounts[ownerTid], "peak reflects the 5 live owners");

            Assert.AreEqual(0, MemDumpCount(capture), "no dump before Stop");
            engine.Stop();
            Assert.AreEqual(1, MemDumpCount(capture), "Stop dumps the [Mem] report exactly once");
            Assert.IsNull(engine._memSampler, "Stop disposes + nulls the sampler");
        }

        // E-T4a — re-init after Stop re-arms a fresh sampler (Dispose+null in Stop prevents double-subscribe).
        [Test]
        public void ET4a_ReInit_AfterStop_ReArmsFreshSampler()
        {
            var engine = MakeEngine(sampling: true);
            engine.Initialize(MakeSim(), new LogCapture());
            var first = engine._memSampler;
            Assert.IsNotNull(first);

            engine.Stop();
            Assert.IsNull(engine._memSampler, "Stop disposes + nulls");

            engine.Initialize(MakeSim(), new LogCapture());
            Assert.IsNotNull(engine._memSampler, "re-init re-arms");
            Assert.AreNotSame(first, engine._memSampler, "a fresh sampler, not the disposed one");
        }

        // Non-EcsSimulation + gate on: the is-EcsSimulation guard skips arming without throwing.
        [Test]
        public void ET5_NonEcsSimulation_GateOn_SkipsArming_NoThrow()
        {
            var engine = MakeEngine(sampling: true);

            Assert.DoesNotThrow(() => engine.Initialize(new TestSimulation(), new LogCapture()));
            Assert.IsNull(engine._memSampler, "non-EcsSimulation -> arming skipped");
        }

        // ── Minimal IKlothoNetworkService for the network (3-arg) Initialize body ──────────────

        private sealed class MockPlayerInfo : IPlayerInfo
        {
            public int PlayerId { get; set; }
            public string DisplayName { get; set; } = "";
            public string Account { get; set; } = "";
            public bool IsReady { get; set; } = true;
            public int Ping { get; set; }
            public PlayerConnectionState ConnectionState { get; set; } = PlayerConnectionState.Connected;
        }

        private sealed class MockNetworkService : IKlothoNetworkService
        {
            public SessionPhase Phase { get; set; } = SessionPhase.Playing;
            public SharedTimeClock SharedClock { get; set; }
            public int PlayerCount { get; set; } = 1;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady { get; set; } = true;
            public int LocalPlayerId { get; set; } = 0;
            public bool IsHost { get; set; } = true;
            public int RandomSeed { get; set; } = 42;
            public IReadOnlyList<IPlayerInfo> Players => new List<IPlayerInfo> { new MockPlayerInfo { PlayerId = 0 } };

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int, bool> OnSyncHashCompared;
            public event Action<int, int> OnResyncFailureReported;
            public event Action<int> OnMatchAbortReceived;
            public event Action<int, int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
            public event Action<IPlayerInfo> OnPlayerDisconnected;
            public event Action<IPlayerInfo> OnPlayerReconnected;
            public event Action OnReconnecting;
            public event Action<ReconnectRejectReason> OnReconnectFailed;
            public event Action OnReconnected;
            public event Action<int, int> OnLateJoinPlayerAdded;
            public event Action<SessionPhase> OnPhaseChanged;
            public event Action<int> OnPlayerCountChanged;
            public event Action<bool> OnAllPlayersReadyChanged;

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
        }
    }
}
