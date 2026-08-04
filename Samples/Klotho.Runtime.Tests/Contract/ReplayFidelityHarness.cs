using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Headless P2P replay harness.
    ///
    /// Design: a single engine (local = player 0) plus a service-level fake (<see cref="FakeP2PNetworkService"/>).
    /// The remote peer (player 1) is simulated by raising <c>OnCommandReceived</c> (<see cref="DeliverRemote"/>)
    /// (<c>_inputBuffer</c> and <c>HandleCommandReceived</c> are private, so the delivery path is the only seam).
    /// <c>FakeTransport</c> is a no-op byte stub and cannot serve as a delivery path, so it is not used.
    ///
    /// Local input is filled by the engine's auto-inject, and the input-delay window (0..InputDelay-1) is
    /// pre-seeded empty for both players by the engine, so the harness delivers **only remote commands at ticks ≥ InputDelay**.
    /// </summary>
    internal sealed class ReplayFidelityHarness
    {
        public readonly KlothoEngine Engine;
        public readonly DeterministicReplaySim Sim;
        public readonly FakeP2PNetworkService Net;

        private readonly IKLogger _logger;
        private readonly int _inputDelay;
        private readonly float _tickDt;      // seconds per tick
        private int _deliveredRemoteThrough; // highest remote tick delivered (-1 = none)
        private readonly HashSet<int> _withheldRemote = new HashSet<int>(); // ticks auto-delivery must skip

        public const int RemotePlayerId = 1;
        public const int LocalPlayerId = 0;

        public ReplayFidelityHarness(IKLogger logger = null)
        {
            _logger = logger;
            var config = new SimulationConfig { Mode = NetworkMode.P2P };
            _inputDelay = config.InputDelayTicks;
            _tickDt = config.TickIntervalMs / 1000f;
            _deliveredRemoteThrough = _inputDelay - 1;  // 0..InputDelay-1 pre-seeded by the engine

            Sim = new DeterministicReplaySim();
            Net = new FakeP2PNetworkService();
            Engine = new KlothoEngine(config, new SessionConfig());
            Engine.Initialize(Sim, Net, logger);
            Engine.SetCommandFactory(new CommandFactory());
            // Drive the real game-start path (NOT Engine.Start() directly): HandleGameStart refreshes
            // _activePlayerIds, calls Start() (pre-seeds the input-delay window + recording), AND injects
            // the replay InitialStateSnapshot — StartReplay throws without it.
            Net.RaiseGameStart();
        }

        /// <summary>Deliver a command from the remote (player 1) at tick T (via the OnCommandReceived seam). Uses EmptyCommand if null.</summary>
        public void DeliverRemote(int tick, ICommand command = null)
        {
            var cmd = command ?? new EmptyCommand();
            if (cmd is CommandBase cb) { cb.PlayerId = RemotePlayerId; cb.Tick = tick; }
            Net.RaiseCommandReceived(cmd);
        }

        /// <summary>Exclude this tick's remote command from auto-delivery (for manual delivery or forcing prediction).</summary>
        public void WithholdRemote(int tick) => _withheldRemote.Add(tick);

        /// <summary>Drive the engine's Update n times (pump after delivery).</summary>
        public void Pump(int n = 1)
        {
            for (int i = 0; i < n; i++) Engine.Update(_tickDt);
        }

        /// <summary>Clean session: advance to targetTick while supplying remote input immediately (no prediction, except for withheld ticks).</summary>
        public void AdvanceTo(int targetTick)
        {
            int guard = 0;
            int maxIter = (targetTick + _inputDelay + 16) * 4 + 64;
            while (Engine.CurrentTick < targetTick && guard++ < maxIter)
            {
                // Keep remote (player 1) supplied a little ahead of the local landing slot
                // (local auto-injects at CurrentTick + InputDelay; RecommendedExtraDelay == 0 with the fake clock).
                int need = Engine.CurrentTick + _inputDelay + 2;
                while (_deliveredRemoteThrough < need)
                {
                    int t = ++_deliveredRemoteThrough;
                    if (!_withheldRemote.Contains(t)) DeliverRemote(t);
                }

                Engine.Update(_tickDt);
            }
        }

        /// <summary>Stop recording and return the IReplayData.</summary>
        public IReplayData StopAndGetReplay()
        {
            Engine.Stop();
            return Engine.GetCurrentReplayData();
        }

        /// <summary>
        /// Replay replayData on a separate engine and return that run's <see cref="DeterministicReplaySim"/>
        /// (for per-tick capture comparison). Same replay sequence as Unity's ReplayIntegrationTests.
        /// </summary>
        public DeterministicReplaySim Replay(IReplayData replayData)
        {
            var replaySim = new DeterministicReplaySim();
            var replayEngine = new KlothoEngine(new SimulationConfig { Mode = NetworkMode.P2P }, new SessionConfig());
            replayEngine.Initialize(replaySim, _logger);
            replayEngine.SetCommandFactory(new CommandFactory());
            replayEngine.StartReplay(replayData);

            int maxIterations = replayData.Metadata.TotalTicks * 2 + 16;
            for (int i = 0; i < maxIterations; i++)
            {
                if (replayEngine.State.IsEnded())
                    break;
                replayEngine.Update(replayData.Metadata.TickIntervalMs);
            }
            return replaySim;
        }
    }

    /// <summary>
    /// Service-level P2P fake: local player 0 + remote player 1 (2 players total). Records SendCommand and
    /// provides helpers to raise OnCommandReceived / OnFullStateReceived / OnGameStart (C# events cannot be
    /// invoked outside their declaring type, hence the internal Raise helpers). Extends the RecordingNetworkService from LateJoinCommandFloorTests.
    /// </summary>
#pragma warning disable 67
    internal sealed class FakeP2PNetworkService : IKlothoNetworkService
    {
        public readonly List<ICommand> Sent = new List<ICommand>();

        public SessionPhase Phase => SessionPhase.Playing;
        public SharedTimeClock SharedClock => default;
        public int PlayerCount => 2;
        public int SpectatorCount => 0;
        public int PendingLateJoinCatchupCount => 0;
        public bool AllPlayersReady => true;
        public int LocalPlayerId => 0;
        public bool IsHost => true;
        public int RandomSeed => 0;
        public IReadOnlyList<IPlayerInfo> Players { get; } = new List<IPlayerInfo>
        {
            new FakePlayerInfo(0),
            new FakePlayerInfo(1),
        };

        // ── Raise helpers (test seam) ────────────────────────────────
        public void RaiseCommandReceived(ICommand command) => OnCommandReceived?.Invoke(command);
        public void RaiseFullStateReceived(int tick, byte[] stateData, long stateHash, FullStateKind kind)
            => OnFullStateReceived?.Invoke(tick, stateData, stateHash, kind);
        public void RaiseGameStart() => OnGameStart?.Invoke();
        // Guest→host resync-failure report (recovery ladder rung 3). Drives the host's
        // HandleResyncFailureReported → TryCorrectiveReset (host self-apply) path.
        public void RaiseResyncFailureReported(int playerId, int tick) => OnResyncFailureReported?.Invoke(playerId, tick);

        // ── IKlothoNetworkService (records SendCommand, everything else inert) ──
        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
        public void CreateRoom(string roomName, int maxPlayers) { }
        public void JoinRoom(string roomName) { }
        public void LeaveRoom(bool keepReconnectCredentials = false) { }
        public void SetReady(bool ready) { }
        public void SendCommand(ICommand command)
        {
            Sent.Add(command);
            // Local loopback: the real KlothoNetworkService.SendCommand processes the sender's own
            // command locally right away (HandleCommandMessage) — that is how a P2P local command
            // enters the engine's input buffer (InputCommand does NOT add it directly in P2P mode).
            OnCommandReceived?.Invoke(command);
        }
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

    internal sealed class FakePlayerInfo : IPlayerInfo
    {
        public FakePlayerInfo(int playerId) => PlayerId = playerId;
        public int PlayerId { get; }
        public string DisplayName => $"P{PlayerId}";
        public string Account => $"acct{PlayerId}";
        public bool IsReady => true;
        public int Ping => 0;
        public PlayerConnectionState ConnectionState => default;
    }
}
