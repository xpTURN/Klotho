using System;
using xpTURN.Klotho.Logging;
using System.Buffers;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public class SpectatorService : ISpectatorService
    {
        private SpectatorState _state = SpectatorState.Idle;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private IKLogger _logger;
        private readonly MessageSerializer _messageSerializer = new MessageSerializer();

        private IKlothoEngine _engine;
        private int _spectatorId;
        private int _roomId = -1;
        private int _latestReceivedTick = -1;

        private SpectatorStartInfo _pendingStartInfo;
        private Core.ISimulationConfig _pendingSimulationConfig;
        private Core.ISessionConfig _pendingSessionConfig;

        // GC-zero cache
        private readonly SpectatorJoinMessage _joinMessageCache = new SpectatorJoinMessage();
        private readonly FullStateRequestMessage _fullStateRequestCache = new FullStateRequestMessage();
        private readonly RoomHandshakeMessage _roomHandshakeCache = new RoomHandshakeMessage();

        // _pendingInputs: do not store SpectatorInputMessage instances directly — copy to decouple receive buffer lifetime
        private readonly Queue<(int startTick, int tickCount, byte[] inputData, int dataLen)> _pendingInputs
            = new Queue<(int, int, byte[], int)>();

        // SD VerifiedStateMessage buffering (arrivals during Synchronizing)
        private readonly Queue<(int tick, byte[] data, int dataLen)> _pendingVerifiedStates
            = new Queue<(int, byte[], int)>();

        // Live player roster (identity included) — seeded from SpectatorAcceptMessage.Roster, then mutated by lobby
        // PlayerJoinNotification / PlayerLeaveNotification / PlayerReadyMessage (pre-game) and LateJoinNotification
        // (mid-match) arrivals. UI/Session layer reads via Players / PlayerCount / OnRosterChanged / OnPlayerCountChanged;
        // this is the live roster (not _pendingStartInfo, which is cleared after the first OnSpectatorStarted fire).
        // The pending start roster is reconciled from this list just before delivery — see ReconcileStartInfoRoster.
        private readonly List<SpectatorPlayerInfo> _players = new List<SpectatorPlayerInfo>();

        public SpectatorState State => _state;
        public int LatestReceivedTick => _latestReceivedTick;
        public int DelayFrames => (_latestReceivedTick >= 0 && _engine != null) ? _latestReceivedTick - _engine.CurrentTick : 0;
        public int PlayerCount => _players.Count;
        public IReadOnlyList<IPlayerInfo> Players => _players;

        public event Action<SpectatorStartInfo> OnSpectatorStarted;
        public event Action<int, ICommand> OnConfirmedInputReceived;
        public event Action<int> OnTickConfirmed;
        public event Action<string> OnSpectatorStopped;
        public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
        public event Action<int> OnPlayerCountChanged;
        public event Action OnRosterChanged;

        /// <summary>
        /// Raised when SimulationConfig is received from SpectatorAcceptMessage.
        /// Spectator host authority model: create EcsSimulation + Engine in this event.
        /// </summary>
        public event Action<Core.ISimulationConfig> OnSimulationConfigReceived;

        /// <summary>
        /// Raised when SessionConfig is received from <see cref="SpectatorAcceptMessage"/>.
        /// </summary>
        /// <remarks>
        /// Fires sequentially right after <see cref="OnSimulationConfigReceived"/> in the same Accept message handler block,
        /// so subscribers can safely create the engine once both events have been received.
        /// </remarks>
        public event Action<Core.ISessionConfig> OnSessionConfigReceived;

        /// <summary>
        /// Deferred engine injection. Called after creating Engine in the OnSimulationConfigReceived handler.
        /// </summary>
        public void SetEngine(Core.IKlothoEngine engine)
        {
            _engine = engine;
        }

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKlothoEngine engine, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _engine = engine;
            _transport.OnConnected += HandleConnected;
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnDisconnected += HandleDisconnected;
        }

        public void SetLogger(IKLogger logger) => _logger = logger;

        public void Connect(string hostAddress, int port, int roomId = -1)
        {
            _roomId = roomId;
            _state = SpectatorState.Connecting;
            if (!_transport.Connect(hostAddress, port))
            {
                _logger?.KError($"[SpectatorService] Failed to start client transport for {hostAddress}:{port}");
                _state = SpectatorState.Disconnected;
                OnSpectatorStopped?.Invoke("Failed to start client transport");
            }
        }

        public void Disconnect()
        {
            _transport.Disconnect();
        }

        public void Update()
        {
            _transport.PollEvents();
        }

        private void HandleConnected()
        {
            if (_roomId >= 0)
            {
                _roomHandshakeCache.RoomId = _roomId;
                using (var serialized = _messageSerializer.SerializePooled(_roomHandshakeCache))
                {
                    _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }

            _joinMessageCache.SpectatorName = "Spectator";
            using (var serialized = _messageSerializer.SerializePooled(_joinMessageCache))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
            _state = SpectatorState.Synchronizing;
        }

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[SpectatorService] Malformed payload from peerId={peerId}, length={length}");
                return;
            }

            _logger?.KTrace($"[SpectatorService] Received: {message.GetType().Name}, state={_state}");

            switch (message)
            {
                case SpectatorAcceptMessage accept:
                    _spectatorId = accept.SpectatorId;
                    // only store _pendingStartInfo, removed _state = Watching transition.
                    // Building the pending start roster here (before the config callbacks) is safe —
                    // it is just stored and delivered later via OnSpectatorStarted.
                    var startPlayers = new List<SpectatorPlayerInfo>(accept.Roster.Count);
                    var startIds = new List<int>(accept.Roster.Count);
                    for (int i = 0; i < accept.Roster.Count; i++)
                    {
                        var entry = accept.Roster[i];
                        startPlayers.Add(new SpectatorPlayerInfo
                        {
                            PlayerId        = entry.PlayerId,
                            DisplayName     = entry.DisplayName.ToString(),
                            Account         = entry.Account.ToString(),
                            IsReady         = entry.ReadyState != 0,
                            ConnectionState = (PlayerConnectionState)entry.ConnectionState,
                        });
                        startIds.Add(entry.PlayerId);
                    }
                    _pendingStartInfo = new SpectatorStartInfo
                    {
                        RandomSeed    = accept.RandomSeed,
                        TickInterval  = accept.TickIntervalMs,
                        PlayerCount   = startIds.Count,
                        PlayerIds     = startIds,
                        Players       = startPlayers,
                    };
                    _pendingSimulationConfig = accept.ToSimulationConfig();
                    OnSimulationConfigReceived?.Invoke(_pendingSimulationConfig);
                    _pendingSessionConfig = accept.ToSessionConfig();
                    // OnSessionConfigReceived drives KlothoSession.FinishSpectatorBootstrap →
                    // SubscribeStateForwarders within this synchronous call. Seed _players
                    // AFTER the call returns so the forwarder is already wired by the time
                    // OnRosterChanged / OnPlayerCountChanged fire — otherwise the initial push is
                    // dropped and UI shows an empty roster until the next change.
                    OnSessionConfigReceived?.Invoke(_pendingSessionConfig);

                    int prevCount = _players.Count;
                    _players.Clear();
                    for (int i = 0; i < accept.Roster.Count; i++)
                    {
                        var entry = accept.Roster[i];
                        _players.Add(new SpectatorPlayerInfo
                        {
                            PlayerId        = entry.PlayerId,
                            DisplayName     = entry.DisplayName.ToString(),
                            Account         = entry.Account.ToString(),
                            IsReady         = entry.ReadyState != 0,
                            ConnectionState = (PlayerConnectionState)entry.ConnectionState,
                        });
                    }
                    OnRosterChanged?.Invoke();
                    if (prevCount != _players.Count)
                        OnPlayerCountChanged?.Invoke(_players.Count);

                    if (accept.LastVerifiedTick >= 0)
                    {
                        _fullStateRequestCache.RequestTick = accept.LastVerifiedTick;
                        using (var serialized = _messageSerializer.SerializePooled(_fullStateRequestCache))
                            _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                    // keep _state = Synchronizing
                    break;

                case GameStartMessage _:
                    // transition only when Synchronizing && _pendingStartInfo != null
                    if (_state == SpectatorState.Synchronizing && _pendingStartInfo != null)
                    {
                        _state = SpectatorState.Watching;
                        ReconcileStartInfoRoster();
                        OnSpectatorStarted?.Invoke(_pendingStartInfo);
                        _pendingStartInfo = null;
                    }
                    break;

                case FullStateResponseMessage stateMsg:
                    _state = SpectatorState.Watching;
                    // 1. StartSpectator first (initialize engine state)
                    if (_pendingStartInfo != null)
                    {
                        ReconcileStartInfoRoster();
                        OnSpectatorStarted?.Invoke(_pendingStartInfo);
                        _pendingStartInfo = null;
                    }
                    // 2. State restore + ResetToTick (must be called after StartSpectator so CurrentTick is not overwritten)
                    OnFullStateReceived?.Invoke(stateMsg.Tick, stateMsg.StateData, stateMsg.StateHash, FullStateKind.Unicast);
                    // 2b. Static environment check — AFTER the apply, BEFORE the buffer drain below. The drain
                    // executes further ticks, and a command inside them can rebake the navmesh, which would push
                    // this peer's fingerprint past the snapshot the sender measured and report a mismatch that is
                    // not one. Same ordering as the player paths (KlothoNetworkService.FullStateResync /
                    // ServerDrivenClientService.HandleFullStateResponse).
                    (_engine as KlothoEngine)?.CheckStaticGeometryFingerprint(stateMsg.StaticFingerprint);
                    // 3. Buffer drain: discard ticks <= snapshot tick, apply only later ticks
                    // Case A: catch-up arrived before FullStateResponse → buffer then drain
                    while (_pendingInputs.Count > 0)
                    {
                        var (startTick, tickCount, inputData, dataLen) = _pendingInputs.Dequeue();
                        if (startTick + tickCount - 1 >= stateMsg.Tick)
                            ProcessSpectatorInput(startTick, tickCount, inputData, dataLen);
                        ArrayPool<byte>.Shared.Return(inputData);
                    }
                    // SD VerifiedStateMessage buffer drain
                    // tick is VerifiedStateMessage.Tick (post-execution tick). Execution tick = tick-1.
                    // Apply only commands after the FullState tick (tick-1 >= stateMsg.Tick → tick > stateMsg.Tick)
                    while (_pendingVerifiedStates.Count > 0)
                    {
                        var (tick, vData, vDataLen) = _pendingVerifiedStates.Dequeue();
                        if (tick > stateMsg.Tick)
                            ProcessVerifiedState(tick, vData.AsSpan(0, vDataLen));
                        ArrayPool<byte>.Shared.Return(vData);
                    }
                    break;

                case SpectatorInputMessage input:
                    if (_state == SpectatorState.Synchronizing)
                    {
                        // copy to avoid _messageCache reuse conflicts and decouple receive buffer lifetime
                        int len = input.InputDataLength;
                        byte[] copy = ArrayPool<byte>.Shared.Rent(len);
                        Buffer.BlockCopy(input.InputData, 0, copy, 0, len);
                        _pendingInputs.Enqueue((input.StartTick, input.TickCount, copy, len));
                    }
                    else
                    {
                        // Case B: arrived after FullStateResponse → handle directly
                        ProcessSpectatorInput(input.StartTick, input.TickCount, input.InputData, input.InputDataLength);
                    }
                    break;

                case LateJoinNotificationMessage notification:
                    HandleLateJoinNotification(peerId, notification);
                    break;

                case PlayerJoinNotificationMessage joinNotification:
                    HandlePlayerJoinNotification(peerId, joinNotification);
                    break;

                case PlayerLeaveNotificationMessage leaveNotification:
                    HandlePlayerLeaveNotification(peerId, leaveNotification);
                    break;

                case PlayerReadyMessage readyMessage:
                    HandlePlayerReady(peerId, readyMessage);
                    break;

                case PlayerStateNotificationMessage stateNotification:
                    HandlePlayerStateNotification(peerId, stateNotification);
                    break;

                // SD server sends VerifiedStateMessage each tick instead of SpectatorInputMessage
                case VerifiedStateMessage verifiedMsg:
                    if (_state == SpectatorState.Synchronizing)
                    {
                        // arrived before FullState received — copy raw bytes then buffer
                        var span = verifiedMsg.ConfirmedInputsSpan;
                        byte[] vCopy = ArrayPool<byte>.Shared.Rent(span.Length);
                        span.CopyTo(vCopy);
                        _pendingVerifiedStates.Enqueue((verifiedMsg.Tick, vCopy, span.Length));
                    }
                    else if (_state == SpectatorState.Watching)
                    {
                        ProcessVerifiedState(verifiedMsg.Tick, verifiedMsg.ConfirmedInputsSpan);
                    }
                    break;
            }
        }

        private void HandleDisconnected(DisconnectReason _)
        {
            _state = SpectatorState.Disconnected;
            OnSpectatorStopped?.Invoke("Host disconnected");
        }

        /// <summary>
        /// Handle SD VerifiedStateMessage. Deserialize the confirmed inputs of a single tick and dispatch.
        /// VerifiedStateMessage.Tick is the post-simulation tick (server CurrentTick+1), so it is
        /// confirmed as tick-1 to match the command's actual execution tick (cmd.Tick = server CurrentTick).
        /// </summary>
        private void ProcessVerifiedState(int tick, ReadOnlySpan<byte> confirmedInputsSpan)
        {
            int executionTick = tick - 1;
            var commands = _commandFactory.DeserializeCommands(confirmedInputsSpan);
            for (int i = 0; i < commands.Count; i++)
                OnConfirmedInputReceived?.Invoke(executionTick, commands[i]);
            OnTickConfirmed?.Invoke(executionTick);
            _latestReceivedTick = Math.Max(_latestReceivedTick, executionTick);
        }

        // Sync the pending start roster from the live _players just before it is delivered to the
        // engine. _pendingStartInfo was seeded from the SpectatorAccept snapshot, but lobby
        // joins/leaves between accept and game start mutate _players only, so without this the engine's
        // _activePlayerIds would be built from a stale roster while PlayerCount is already current.
        private void ReconcileStartInfoRoster()
        {
            if (_pendingStartInfo == null)
                return;
            _pendingStartInfo.PlayerIds.Clear();
            _pendingStartInfo.Players.Clear();
            for (int i = 0; i < _players.Count; i++)
            {
                _pendingStartInfo.PlayerIds.Add(_players[i].PlayerId);
                // Per-item copy: _players holds mutable instances updated in place by
                // HandlePlayerReady / state notifications. Sharing the live reference would let
                // those post-start mutations retroactively alter this start-time snapshot.
                _pendingStartInfo.Players.Add(new SpectatorPlayerInfo
                {
                    PlayerId        = _players[i].PlayerId,
                    DisplayName     = _players[i].DisplayName,
                    Account         = _players[i].Account,
                    IsReady         = _players[i].IsReady,
                    Ping            = _players[i].Ping,
                    ConnectionState = _players[i].ConnectionState,
                });
            }
            _pendingStartInfo.PlayerCount = _players.Count;
        }

        private void HandleLateJoinNotification(int peerId, LateJoinNotificationMessage msg)
        {
            // Spectator only receives from host. In client-mode LiteNetLibTransport the server
            // peer is exposed as peerId=0 — drop forged sends from any other source.
            if (peerId != 0)
            {
                _logger?.KWarning($"[SpectatorService][HandleLateJoinNotification] Ignored from non-host peerId={peerId}");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[SpectatorService][HandleLateJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            // LateJoinNotification carries no ready state (game already running) → IsReady=false.
            _players.Add(new SpectatorPlayerInfo
            {
                PlayerId    = msg.PlayerId,
                DisplayName = msg.DisplayName ?? "",
                Account     = msg.Account ?? "",
                IsReady     = false,
            });
            OnRosterChanged?.Invoke();
            OnPlayerCountChanged?.Invoke(_players.Count);

            _logger?.KInformation($"[SpectatorService][HandleLateJoinNotification] Late join player added: playerId={msg.PlayerId}, joinTick={msg.JoinTick}");
        }

        // A player completed the normal-join (lobby) handshake on the host/server. Add it to the
        // spectator's roster so PlayerCount stays consistent before StartGame. Only the host/server
        // (peerId 0) is a valid source, and a duplicate is a no-op.
        private void HandlePlayerJoinNotification(int peerId, PlayerJoinNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SpectatorService][HandlePlayerJoinNotification] Ignored from non-host peerId={peerId}");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[SpectatorService][HandlePlayerJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            _players.Add(new SpectatorPlayerInfo
            {
                PlayerId        = msg.PlayerId,
                DisplayName     = msg.DisplayName ?? "",
                Account         = msg.Account ?? "",
                IsReady         = msg.IsReady,
                ConnectionState = (PlayerConnectionState)msg.ConnectionState,
            });
            OnRosterChanged?.Invoke();
            OnPlayerCountChanged?.Invoke(_players.Count);
            _logger?.KInformation($"[SpectatorService][HandlePlayerJoinNotification] Lobby player added: playerId={msg.PlayerId}");
        }

        // A player left during the lobby. Remove it from the spectator's roster. Only the host/server
        // (peerId 0) is a valid source, and an unknown PlayerId is a no-op.
        private void HandlePlayerLeaveNotification(int peerId, PlayerLeaveNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SpectatorService][HandlePlayerLeaveNotification] Ignored from non-host peerId={peerId}");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _players.RemoveAt(i);
                    OnRosterChanged?.Invoke();
                    OnPlayerCountChanged?.Invoke(_players.Count);
                    _logger?.KInformation($"[SpectatorService][HandlePlayerLeaveNotification] Lobby player removed: playerId={msg.PlayerId}");
                    return;
                }
            }
        }

        // A player toggled ready during the lobby. Update the spectator's roster entry in place so the
        // ready display stays consistent with players. Only the host/server (peerId 0) is a valid source,
        // and an unknown PlayerId is a no-op.
        private void HandlePlayerReady(int peerId, PlayerReadyMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SpectatorService][HandlePlayerReady] Ignored from non-host peerId={peerId}");
                return;
            }

            // A spectator sends no ready of its own, but it RECEIVES every player's — so the relayed
            // layout fingerprint is a free check on this spectator's own build. Environment is left to the
            // FullState path (that one carries the combined fold and lands after the state is applied).
            var setupEngine = _engine as KlothoEngine;
            if (setupEngine != null)
            {
                var verdict = setupEngine.CheckReadyFingerprints(
                    msg.PlayerId, msg.LayoutFingerprint, msg.EnvironmentFingerprint,
                    compareEnvironment: false);
                if (verdict == ReadyFingerprintVerdict.LayoutMismatch && !setupEngine.AllowLayoutMismatch)
                {
                    // No transport control over players — leave instead.
                    setupEngine.AbortMatch(AbortReason.LayoutMismatch);
                    return;
                }
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    if (_players[i].IsReady != msg.IsReady)
                    {
                        _players[i].IsReady = msg.IsReady;
                        OnRosterChanged?.Invoke();
                    }
                    return;
                }
            }
        }

        // A player's connection state changed mid-game (disconnect / reconnect / leave). Update the
        // spectator roster so connection-state display stays live (P2P: host Broadcast reaches spectators;
        // SD: host sends to _spectators). Only the host/server (peerId 0) is a valid source.
        private void HandlePlayerStateNotification(int peerId, PlayerStateNotificationMessage msg)
        {
            if (peerId != 0)
            {
                _logger?.KWarning($"[SpectatorService][HandlePlayerStateNotification] Ignored from non-host peerId={peerId}");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId != msg.PlayerId)
                    continue;

                switch ((PlayerStateChange)msg.State)
                {
                    case PlayerStateChange.Disconnected:
                        _players[i].ConnectionState = PlayerConnectionState.Disconnected;
                        OnRosterChanged?.Invoke();
                        break;

                    case PlayerStateChange.Reconnected:
                        _players[i].ConnectionState = PlayerConnectionState.Connected;
                        OnRosterChanged?.Invoke();
                        break;

                    case PlayerStateChange.Left:
                        _players.RemoveAt(i);
                        OnRosterChanged?.Invoke();
                        OnPlayerCountChanged?.Invoke(_players.Count);
                        break;
                }
                return;
            }
        }

        private void ProcessSpectatorInput(int startTick, int tickCount, byte[] inputData, int dataLength)
        {
            _logger?.KTrace($"[SpectatorService] ProcessInput: startTick={startTick}, tickCount={tickCount}, dataLen={dataLength}");
            var reader = new SpanReader(inputData, 0, dataLength);
            for (int tick = startTick; tick < startTick + tickCount; tick++)
            {
                int commandCount = reader.ReadInt32();
                for (int i = 0; i < commandCount; i++)
                {
                    // dispatch one command at a time — Action<int, ICommand> signature (GC-free)
                    var cmd = _commandFactory.DeserializeCommandRaw(ref reader);
                    OnConfirmedInputReceived?.Invoke(tick, cmd);
                }
                _logger?.KTrace($"[SpectatorService] OnTickConfirmed: tick={tick}, cmdCount={commandCount}");
                OnTickConfirmed?.Invoke(tick);
            }
            _latestReceivedTick = Math.Max(_latestReceivedTick, startTick + tickCount - 1);
        }
    }
}
