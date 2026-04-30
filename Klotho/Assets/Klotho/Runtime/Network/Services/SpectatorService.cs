using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public class SpectatorService : ISpectatorService
    {
        private SpectatorState _state = SpectatorState.Idle;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private ILogger _logger;
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

        public SpectatorState State => _state;
        public int LatestReceivedTick => _latestReceivedTick;
        public int DelayFrames => (_latestReceivedTick >= 0 && _engine != null) ? _latestReceivedTick - _engine.CurrentTick : 0;

        public event Action<SpectatorStartInfo> OnSpectatorStarted;
        public event Action<int, ICommand> OnConfirmedInputReceived;
        public event Action<int> OnTickConfirmed;
        public event Action<string> OnSpectatorStopped;
        public event Action<int, byte[], long> OnFullStateReceived;

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

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKlothoEngine engine, ILogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _engine = engine;
            _transport.OnConnected += HandleConnected;
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnDisconnected += HandleDisconnected;
        }

        public void SetLogger(ILogger logger) => _logger = logger;

        public void Connect(string hostAddress, int port, int roomId = -1)
        {
            _roomId = roomId;
            _state = SpectatorState.Connecting;
            if (!_transport.Connect(hostAddress, port))
            {
                _logger?.ZLogError($"[SpectatorService] Failed to start client transport for {hostAddress}:{port}");
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
                _logger?.ZLogWarning($"[SpectatorService] Malformed payload from peerId={peerId}, length={length}");
                return;
            }

            _logger?.ZLogTrace($"[SpectatorService] Received: {message.GetType().Name}, state={_state}");

            switch (message)
            {
                case SpectatorAcceptMessage accept:
                    _spectatorId = accept.SpectatorId;
                    // only store _pendingStartInfo, removed _state = Watching transition
                    _pendingStartInfo = new SpectatorStartInfo
                    {
                        RandomSeed    = accept.RandomSeed,
                        TickInterval  = accept.TickIntervalMs,
                        PlayerCount   = accept.PlayerIds.Count,
                        PlayerIds     = new List<int>(accept.PlayerIds),
                    };
                    _pendingSimulationConfig = accept.ToSimulationConfig();
                    OnSimulationConfigReceived?.Invoke(_pendingSimulationConfig);
                    _pendingSessionConfig = accept.ToSessionConfig();
                    OnSessionConfigReceived?.Invoke(_pendingSessionConfig);
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
                        OnSpectatorStarted?.Invoke(_pendingStartInfo);
                        _pendingStartInfo = null;
                    }
                    break;

                case FullStateResponseMessage stateMsg:
                    _state = SpectatorState.Watching;
                    // 1. StartSpectator first (initialize engine state)
                    if (_pendingStartInfo != null)
                    {
                        OnSpectatorStarted?.Invoke(_pendingStartInfo);
                        _pendingStartInfo = null;
                    }
                    // 2. State restore + ResetToTick (must be called after StartSpectator so CurrentTick is not overwritten)
                    OnFullStateReceived?.Invoke(stateMsg.Tick, stateMsg.StateData, stateMsg.StateHash);
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

        private void ProcessSpectatorInput(int startTick, int tickCount, byte[] inputData, int dataLength)
        {
            _logger?.ZLogTrace($"[SpectatorService] ProcessInput: startTick={startTick}, tickCount={tickCount}, dataLen={dataLength}");
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
                _logger?.ZLogTrace($"[SpectatorService] OnTickConfirmed: tick={tick}, cmdCount={commandCount}");
                OnTickConfirmed?.Invoke(tick);
            }
            _latestReceivedTick = Math.Max(_latestReceivedTick, startTick + tickCount - 1);
        }
    }
}
