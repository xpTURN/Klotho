using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Headless SD-client replay harness (Plan-L1 §3-A). Mirrors the P2P <see cref="ReplayFidelityHarness"/>
    /// but drives a Server-Driven CLIENT engine through the public event surface of a fake
    /// <see cref="IServerDrivenNetworkService"/>.
    ///
    /// Bootstrap sequence (Plan-L1 §3-A, real-code order): Countdown (arms _expectingInitialFullState) →
    /// GameStart (Start(true) → StartRecording, State=Running) → initial FullState via
    /// OnServerFullStateReceived (sets the replay InitialStateSnapshot) → verified ticks via
    /// OnVerifiedStateReceived (engine records executionTick = entry.Tick - 1) → optional resync.
    ///
    /// The harness keeps a mirror <see cref="DeterministicReplaySim"/> (<see cref="_serverSim"/>) so a
    /// verified tick's stateHash matches what the client resimulates — otherwise every verified state would
    /// trip a determinism-failure resync.
    /// </summary>
    internal sealed class SdReplayHarness
    {
        public readonly KlothoEngine Engine;
        public readonly DeterministicReplaySim Sim;
        public readonly FakeSdNetworkService Net;

        private readonly IKLogger _logger;
        private readonly float _tickDt;
        private readonly DeterministicReplaySim _serverSim = new DeterministicReplaySim();  // mirror = authoritative lineage
        private int _nextEntryTick;   // next verified entry.Tick to feed (executionTick = entry.Tick - 1)

        public const int LocalPlayerId = 0;

        public SdReplayHarness(IKLogger logger = null)
        {
            _logger = logger;
            var config = new SimulationConfig { Mode = NetworkMode.ServerDriven };
            _tickDt = config.TickIntervalMs / 1000f;

            Sim = new DeterministicReplaySim();
            Net = new FakeSdNetworkService();
            Engine = new KlothoEngine(config, new SessionConfig());
            Engine.Initialize(Sim, Net, logger);
            Engine.SetCommandFactory(new CommandFactory());

            _serverSim.Initialize();

            // Bootstrap: Countdown arms the initial-FullState wait; GameStart starts recording + Running.
            Net.RaiseCountdownStarted(0);
            Net.RaiseGameStart();

            // Initial FullState (server → client): applies tick-0 state and sets the replay InitialStateSnapshot.
            var (data, hash) = _serverSim.SerializeFullStateWithHash();
            Net.RaiseServerFullState(0, data, hash);
            Net.RaiseBootstrapBegin(0, 0);

            _nextEntryTick = 1;   // first verified tick executes executionTick 0
        }

        /// <summary>
        /// Feeds one verified tick from the server: executes the local player's confirmed input on the
        /// mirror to get the authoritative hash, then raises OnVerifiedStateReceived and pumps the engine.
        ///
        /// The SD client has no local input source here, so it PREDICTS the local player's input each tick
        /// (KlothoEngine.ServerDrivenClient ExecuteClientPredictionTick). A verified state that omitted the
        /// local command would leave that prediction in place at resim and diverge, so the server must
        /// confirm the local input explicitly — exactly what a real server echoes back. Mirror and wire get
        /// separate instances with identical fields (hash is field-derived; the engine pools its copy).
        /// </summary>
        public void DeliverVerified()
        {
            int entryTick = _nextEntryTick++;
            int executionTick = entryTick - 1;

            // Mirror executes executionTick with the confirmed local input to produce the authoritative hash.
            _serverSim.Tick(LocalInput(executionTick));
            long stateHash = _serverSim.GetStateHash();

            Net.RaiseVerifiedState(entryTick, LocalInput(executionTick), stateHash);
            Pump(2);
        }

        /// <summary>
        /// Feeds one verified tick whose server stateHash deliberately MISMATCHES the client's resim,
        /// forcing an SD determinism failure: the client resimulates executionTick to the same hash the
        /// mirror produced, sees it disagree with the (corrupted) server hash, and requests a FullState.
        /// The failed tick is NOT recorded (the failure path returns before RecordTick), so the recorder
        /// frontier stays at the last good tick. Returns entry.Tick — what the client passes to
        /// SendFullStateRequest and the tick the resync FullState should carry.
        /// </summary>
        public int DeliverBadVerified()
        {
            int entryTick = _nextEntryTick++;
            int executionTick = entryTick - 1;

            _serverSim.Tick(LocalInput(executionTick));
            long badHash = _serverSim.GetStateHash() ^ 0x5A5A_5A5A_5A5A_5A5AL;   // ≠ client's resim → failure

            Net.RaiseVerifiedState(entryTick, LocalInput(executionTick), badHash);
            Pump(2);
            return entryTick;
        }

        private static List<ICommand> LocalInput(int tick)
        {
            var cmd = new EmptyCommand();
            if (cmd is CommandBase cb) { cb.PlayerId = LocalPlayerId; cb.Tick = tick; }
            return new List<ICommand> { cmd };
        }

        /// <summary>Advances the engine's Update loop n times (drives ProcessVerifiedBatch).</summary>
        public void Pump(int n = 1)
        {
            for (int i = 0; i < n; i++) Engine.Update(_tickDt);
        }

        public IReplayData StopAndGetReplay()
        {
            Engine.Stop();
            return Engine.GetCurrentReplayData();
        }

        /// <summary>Replays replayData on a fresh replay engine and returns its sim (per-tick capture).</summary>
        public DeterministicReplaySim Replay(IReplayData replayData)
        {
            var replaySim = new DeterministicReplaySim();
            var replayEngine = new KlothoEngine(new SimulationConfig { Mode = NetworkMode.ServerDriven }, new SessionConfig());
            replayEngine.Initialize(replaySim, _logger);
            replayEngine.SetCommandFactory(new CommandFactory());
            replayEngine.StartReplay(replayData);

            int maxIterations = replayData.Metadata.TotalTicks * 2 + 16;
            for (int i = 0; i < maxIterations; i++)
            {
                if (replayEngine.State.IsEnded()) break;
                replayEngine.Update(replayData.Metadata.TickIntervalMs);
            }
            return replaySim;
        }
    }

    /// <summary>
    /// Headless SD-client network fake. Local player 0, single player, IsServer=false. Provides Raise
    /// helpers (C# events can't be invoked outside their declaring type). Ported from the Unity
    /// EventDispatchSDClientTests.MockSDNetworkService, minus Unity deps.
    /// </summary>
#pragma warning disable 67
    internal sealed class FakeSdNetworkService : IServerDrivenNetworkService
    {
        public SessionPhase Phase => SessionPhase.Playing;
        public SharedTimeClock SharedClock => default;
        public int PlayerCount => 1;
        public int SpectatorCount => 0;
        public int PendingLateJoinCatchupCount => 0;
        public bool AllPlayersReady => true;
        public int LocalPlayerId => 0;
        public bool IsHost => false;
        public bool IsServer => false;
        public int RandomSeed => 0;
        public IReadOnlyList<IPlayerInfo> Players { get; } = new List<IPlayerInfo> { new FakeSdPlayerInfo(0) };

        // Records the client's determinism-failure resync request (SendFullStateRequest).
        public int LastFullStateRequestTick = -1;
        public int FullStateRequestCount;

        // ── Raise helpers (test seam) ────────────────────────────────
        public void RaiseCountdownStarted(long startTime) => OnCountdownStarted?.Invoke(startTime);
        public void RaiseGameStart() => OnGameStart?.Invoke();
        public void RaiseServerFullState(int tick, byte[] data, long hash) => OnServerFullStateReceived?.Invoke(tick, data, hash);
        public void RaiseBootstrapBegin(int firstTick, long tickStartMs) => OnBootstrapBegin?.Invoke(firstTick, tickStartMs);
        public void RaiseVerifiedState(int tick, IReadOnlyList<ICommand> commands, long hash)
            => OnVerifiedStateReceived?.Invoke(tick, commands, hash);

        // ── IKlothoNetworkService ────────────────────────────────────
        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
        public void CreateRoom(string roomName, int maxPlayers) { }
        public void JoinRoom(string roomName) { }
        public void LeaveRoom(bool keepReconnectCredentials = false) { }
        public void SetReady(bool ready) { }
        public void SendCommand(ICommand command) { }
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
        public void SendFullStateRequest(int currentTick) { LastFullStateRequestTick = currentTick; FullStateRequestCount++; }
        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }

        // ── IServerDrivenNetworkService ──────────────────────────────
        public void SendClientInput(int tick, ICommand command) { }
        public void SendReliableCommand(ICommand command) { }
        public void SendBootstrapReady(int playerId) { }
        public int GetMinClientAckedTick() => 0;
        public void ClearUnackedInputs() { }
        public byte[] GetPlayerEntitlement(int playerId) => null;

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
        public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
        public event Action<int> OnInputAckReceived;
        public event Action<int, byte[], long> OnServerFullStateReceived;
        public event Action<int, long> OnBootstrapBegin;
        public event Action<int, int, RejectionReason> OnCommandRejected;
    }
#pragma warning restore 67

    internal sealed class SdCapturingLogger : IKLogger
    {
        public readonly List<string> Lines = new List<string>();
        public bool IsEnabled(KLogLevel level) => true;
        public void Log(KLogLevel level, string message, Exception exception) => Lines.Add($"[{level}] {message}");
        public string Dump() => string.Join("\n", Lines);
    }

    internal sealed class FakeSdPlayerInfo : IPlayerInfo
    {
        public FakeSdPlayerInfo(int playerId) => PlayerId = playerId;
        public int PlayerId { get; }
        public string DisplayName => $"P{PlayerId}";
        public string Account => $"acct{PlayerId}";
        public bool IsReady => true;
        public int Ping => 0;
        public PlayerConnectionState ConnectionState => default;
    }
}
