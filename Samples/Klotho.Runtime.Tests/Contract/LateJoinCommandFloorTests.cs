using System;
using System.Collections.Generic;

using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Late-join spawn floor regression guards.
    /// A late-joining guest's own cmd.Tick must never precede its joinTick (the tick whose
    /// PlayerJoinCommand seeds join-time deterministic state, e.g. loadout seeds) — an earlier
    /// spawn would consume pre-seed state identically on every peer, silently bypassing join-time
    /// guarantees. The joinTick is host-computed and carried in LateJoinAcceptMessage.JoinTick
    /// (single-sourced — the guest never re-derives the host formula), read in SeedLateJoinPlayers,
    /// flushed to the engine in SubscribeEngine, and applied as a targetTick lower bound in
    /// InputCommand.
    /// </summary>
    public sealed class LateJoinCommandFloorTests
    {
        // ── engine-level floor semantics ─────────────────────────────

        [Test] // no floor set → natural targetTick = CurrentTick(0) + InputDelayTicks
        public void InputCommand_NoFloor_NaturalTick()
        {
            var (engine, net) = NewEngine();

            engine.InputCommand(new EmptyCommand());

            Assert.AreEqual(4, ((CommandBase)XAssert.Single(net.Sent)).Tick); // default InputDelayTicks = 4
        }

        [Test] // floor above the natural tick lifts cmd.Tick to exactly joinTick
        public void InputCommand_FloorAboveNatural_LiftsToJoinTick()
        {
            var (engine, net) = NewEngine();
            engine.SetLateJoinCommandFloor(110);

            engine.InputCommand(new EmptyCommand());

            Assert.AreEqual(110, ((CommandBase)XAssert.Single(net.Sent)).Tick);
        }

        [Test] // floor below the natural tick is inert — no pull-back to the past
        public void InputCommand_FloorBelowNatural_Inert()
        {
            var (engine, net) = NewEngine();
            engine.SetLateJoinCommandFloor(3);

            engine.InputCommand(new EmptyCommand());

            Assert.AreEqual(4, ((CommandBase)XAssert.Single(net.Sent)).Tick);
        }

        // ── service → engine wiring (accept-carried joinTick + flush) ─────────

        [Test] // SeedLateJoinPlayers reads the host-computed accept.JoinTick (NOT a re-derivation from
               // CurrentTick + LJD — those are set to a conflicting sum on purpose) and SubscribeEngine
               // flushes it as the engine floor: accept(JoinTick=110) → first cmd.Tick = 110
        public void SeedLateJoinPlayers_AcceptJoinTick_FlushedAsEngineFloor()
        {
            var (engine, net) = NewEngine();
            var svc = new KlothoNetworkService();
            var accept = new LateJoinAcceptMessage
            {
                PlayerId = 2,
                CurrentTick = 50,       // 50 + 3 = 53 ≠ 110: proves the floor comes from JoinTick,
                LateJoinDelayTicks = 3, // not from re-deriving the host formula
                JoinTick = 110,
                RandomSeed = 1,
                PlayerCount = 1,
            };
            svc.SeedLateJoinPlayers(new LateJoinPayload { AcceptMessage = accept });

            svc.SubscribeEngine(engine);
            engine.InputCommand(new EmptyCommand());

            Assert.AreEqual(110, ((CommandBase)XAssert.Single(net.Sent)).Tick);
        }

        [Test] // JoinTick survives the wire round-trip (guards the codegen append after RosterTickets)
        public void LateJoinAcceptMessage_JoinTick_RoundTripsOnWire()
        {
            var ser = new MessageSerializer();
            var msg = new LateJoinAcceptMessage { JoinTick = 42, RosterTickets = { "t0", "t1" } };

            byte[] bytes = ser.Serialize(msg);
            var back = ser.Deserialize(bytes, bytes.Length) as LateJoinAcceptMessage;

            Assert.IsNotNull(back);
            Assert.AreEqual(42, back.JoinTick);
            Assert.AreEqual(new[] { "t0", "t1" }, back.RosterTickets);
        }

        [Test] // normal join (no late-join seed) leaves the floor unset — SubscribeEngine must not lift ticks
        public void SubscribeEngine_WithoutLateJoinSeed_NoFloor()
        {
            var (engine, net) = NewEngine();
            var svc = new KlothoNetworkService();

            svc.SubscribeEngine(engine);
            engine.InputCommand(new EmptyCommand());

            Assert.AreEqual(4, ((CommandBase)XAssert.Single(net.Sent)).Tick);
        }

        // ── harness ──────────────────────────────────────────────────

        private static (KlothoEngine engine, RecordingNetworkService net) NewEngine()
        {
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            var net = new RecordingNetworkService();
            engine.Initialize(new StubSimulation(), net, null);
            return (engine, net);
        }

        private sealed class StubSimulation : ISimulation
        {
            public int CurrentTick => 0;
            public void Initialize() { }
            public void Tick(List<ICommand> commands) { }
            public void Rollback(int targetTick) { }
            public void SaveSnapshot() { }
            public int GetNearestRollbackTick(int targetTick) => -1;
            public long GetStateHash() => 0;
            public void Reset() { }
            public void RestoreFromFullState(byte[] stateData) { }
            public byte[] SerializeFullState() => Array.Empty<byte>();
            public (byte[] data, long hash) SerializeFullStateWithHash() => (Array.Empty<byte>(), 0);
            public void EmitSyncEvents() { }
            public void OnPlayerJoined(int playerId, int tick) { }
#pragma warning disable 67
            public event Action<int> OnPlayerJoinedNotification;
#pragma warning restore 67
        }

        // Minimal IKlothoNetworkService — records SendCommand, everything else inert.
#pragma warning disable 67
        private sealed class RecordingNetworkService : IKlothoNetworkService
        {
            public readonly List<ICommand> Sent = new List<ICommand>();

            public SessionPhase Phase => SessionPhase.Playing;
            public SharedTimeClock SharedClock => default;
            public int PlayerCount => 0;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId => 0;
            public bool IsHost => false;
            public int RandomSeed => 0;
            public IReadOnlyList<IPlayerInfo> Players { get; } = new List<IPlayerInfo>();

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) => Sent.Add(command);
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int> OnResyncFailureReported;
            public event Action<int> OnMatchAbortReceived;
            public event Action<int, int, bool> OnSyncHashCompared;
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
        }
#pragma warning restore 67
    }
}
