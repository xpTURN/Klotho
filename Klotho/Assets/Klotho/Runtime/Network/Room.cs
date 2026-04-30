using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Room lifecycle state
    /// </summary>
    public enum RoomState
    {
        Empty,
        Active,
        Draining,
        Disposing
    }

    /// <summary>
    /// An independent game room within a multi-room server.
    /// Each room owns its own simulation, network service, and engine instance.
    /// No state is shared between rooms.
    /// </summary>
    public class Room
    {
        public int RoomId { get; }
        public RoomState State { get; set; }

        public ISimulationConfig SimulationConfig { get; }
        public ISessionConfig SessionConfig { get; }
        public ISimulation Simulation { get; }
        public CommandFactory CommandFactory { get; }
        public RoomScopedTransport Transport { get; }
        public ServerNetworkService NetworkService { get; }
        public KlothoEngine Engine { get; }
        public ISimulationCallbacks Callbacks { get; }

        public ConcurrentQueue<InboundEntry> InboundQueue { get; } = new ConcurrentQueue<InboundEntry>();

        // Straggler tracking
        public int StragglerCount { get; set; }
        public bool IsStraggler { get; set; }

        private readonly ILogger _logger;

        public Room(
            int roomId,
            ISimulationConfig simConfig,
            ISessionConfig sessionConfig,
            ISimulation simulation,
            CommandFactory commandFactory,
            RoomScopedTransport transport,
            ServerNetworkService networkService,
            KlothoEngine engine,
            ISimulationCallbacks callbacks,
            ILogger logger)
        {
            RoomId = roomId;
            SimulationConfig = simConfig;
            SessionConfig = sessionConfig;
            Simulation = simulation;
            CommandFactory = commandFactory;
            Transport = transport;
            NetworkService = networkService;
            Engine = engine;
            Callbacks = callbacks;
            _logger = logger;

            State = RoomState.Empty;
        }

        /// <summary>
        /// Invoked by a ThreadPool worker during stage 2 of the server loop.
        /// Drains the inbound queue and updates the engine.
        /// </summary>
        public void Update(float elapsedSec)
        {
            DrainInboundQueue();
            Engine.Update(elapsedSec);

            if (State == RoomState.Active && ShouldDrain())
            {
                State = RoomState.Draining;
                _logger?.ZLogInformation($"[Room {RoomId}] → Draining (all connections gone)");
            }
        }

        /// <summary>
        /// Consumes every message from the inbound queue and raises them as RoomScopedTransport events.
        /// Consumed Data buffers are returned via StreamPool.ReturnBuffer.
        /// </summary>
        public void DrainInboundQueue()
        {
            while (InboundQueue.TryDequeue(out InboundEntry entry))
            {
                switch (entry.Type)
                {
                    case InboundEventType.Data:
                        try
                        {
                            Transport.RaiseDataReceived(entry.PeerId, entry.Buffer, entry.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger?.ZLogError($"[Room {RoomId}] RaiseDataReceived exception: peerId={entry.PeerId}, len={entry.Length}, ex={ex.Message}");
                        }
                        finally
                        {
                            StreamPool.ReturnBuffer(entry.Buffer);
                        }
                        break;

                    case InboundEventType.Connected:
                        Transport.RaisePeerConnected(entry.PeerId);
                        break;

                    case InboundEventType.Disconnected:
                        Transport.RaisePeerDisconnected(entry.PeerId);
                        break;
                }
            }
        }

        /// <summary>
        /// Determines whether the room should transition to Draining.
        /// Must be evaluated after DrainInboundQueue() completes — evaluating it
        /// while a ConnectEvent from the same batch is still unprocessed can yield a false positive.
        /// </summary>
        public bool ShouldDrain()
        {
            return NetworkService.PeerToPlayerCount == 0
                && NetworkService.PendingPeerCount == 0
                && NetworkService.PeerSyncStateCount == 0
                && NetworkService.DisconnectedPlayerCount == 0;
        }

        /// <summary>
        /// Releases resources after draining completes. Invoked by RoomManager on the main thread.
        /// </summary>
        public void Dispose()
        {
            NetworkService.LeaveRoom();

            // Clear any leftover queue entries (safety guard)
            while (InboundQueue.TryDequeue(out InboundEntry entry))
            {
                if (entry.Type == InboundEventType.Data && entry.Buffer != null)
                    StreamPool.ReturnBuffer(entry.Buffer);
            }

            StragglerCount = 0;
            IsStraggler = false;
            State = RoomState.Empty;

            _logger?.ZLogInformation($"[Room {RoomId}] → Empty (disposed)");
        }

        public void MarkStraggler()
        {
            IsStraggler = true;
            StragglerCount++;
        }

        public void ClearStraggler()
        {
            IsStraggler = false;
            StragglerCount = 0;
        }
    }
}
