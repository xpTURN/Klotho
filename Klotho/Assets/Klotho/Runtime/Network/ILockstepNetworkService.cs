using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Player connection state (supports reconnection)
    /// </summary>
    public enum PlayerConnectionState
    {
        Connected,
        Disconnected,
    }

    /// <summary>
    /// Player information
    /// </summary>
    public interface IPlayerInfo
    {
        int PlayerId { get; }
        string PlayerName { get; }
        bool IsReady { get; }
        int Ping { get; }
        PlayerConnectionState ConnectionState { get; }
    }

    /// <summary>
    /// Network session phase
    /// </summary>
    public enum SessionPhase
    {
        None,         // Initial state
        Lobby,        // CreateRoom/JoinRoom complete, awaiting handshake
        Syncing,      // Handshake in progress (SyncRequest/Reply round trip)
        Synchronized, // Handshake complete, awaiting Ready
        Countdown,    // Game start countdown in progress
        Playing,      // Game in progress
        Disconnected  // Disconnected
    }

    /// <summary>
    /// Lockstep network service interface.
    /// Responsible for command synchronization and player management.
    /// </summary>
    public interface IKlothoNetworkService
    {
        /// <summary>
        /// Current session phase
        /// </summary>
        SessionPhase Phase { get; }

        /// <summary>
        /// Shared clock synchronized through the handshake
        /// </summary>
        SharedTimeClock SharedClock { get; }
        /// <summary>
        /// Number of connected players
        /// </summary>
        int PlayerCount { get; }

        /// <summary>
        /// Number of connected spectators
        /// </summary>
        int SpectatorCount { get; }

        /// <summary>
        /// Number of pending Late Join catchups (guests in catch-up phase awaiting verified input batches).
        /// FireVerifiedInputBatch is gated on (SpectatorCount > 0 || PendingLateJoinCatchupCount > 0)
        /// so that catchup input batches are dispatched even when no spectators exist
        /// (typical P2P LAN setup).
        /// </summary>
        int PendingLateJoinCatchupCount { get; }

        /// <summary>
        /// Whether all players are ready
        /// </summary>
        bool AllPlayersReady { get; }

        /// <summary>
        /// Local player ID
        /// </summary>
        int LocalPlayerId { get; }

        /// <summary>
        /// Whether this is the host
        /// </summary>
        bool IsHost { get; }

        /// <summary>
        /// Random seed shared via GameStartMessage (host authoritative)
        /// </summary>
        int RandomSeed { get; }

        /// <summary>
        /// Information for all connected players
        /// </summary>
        IReadOnlyList<IPlayerInfo> Players { get; }

        /// <summary>
        /// Initialize network
        /// </summary>
        void Initialize(INetworkTransport transport, ICommandFactory commandFactory, ILogger logger);

        /// <summary>
        /// Create room (host)
        /// </summary>
        void CreateRoom(string roomName, int maxPlayers);

        /// <summary>
        /// Join room
        /// </summary>
        void JoinRoom(string roomName);

        /// <summary>
        /// Leave room
        /// </summary>
        void LeaveRoom();

        /// <summary>
        /// Set ready state
        /// </summary>
        void SetReady(bool ready);

        /// <summary>
        /// Send command (transmit local input to other players)
        /// </summary>
        void SendCommand(ICommand command);

        /// <summary>
        /// Wait for commands of a specific tick
        /// </summary>
        void RequestCommandsForTick(int tick);

        /// <summary>
        /// Send sync hash
        /// </summary>
        void SendSyncHash(int tick, long hash);

        /// <summary>
        /// Per-frame update
        /// </summary>
        void Update();

        /// <summary>
        /// Flush queued outbound messages (PollEvents only, without the full Update logic)
        /// </summary>
        void FlushSendQueue();

        void ClearOldData(int tick);

        /// <summary>
        /// Send the local player's PlayerConfig to the host.
        /// </summary>
        void SendPlayerConfig(int playerId, Core.PlayerConfigBase playerConfig);

        /// <summary>
        /// Game start event
        /// </summary>
        event Action OnGameStart;

        /// <summary>
        /// Countdown start event (startTime: game start time in SharedNow units)
        /// </summary>
        event Action<long> OnCountdownStarted;

        /// <summary>
        /// Player joined event
        /// </summary>
        event Action<IPlayerInfo> OnPlayerJoined;

        /// <summary>
        /// Player left event
        /// </summary>
        event Action<IPlayerInfo> OnPlayerLeft;

        /// <summary>
        /// Command received event
        /// </summary>
        event Action<ICommand> OnCommandReceived;

        /// <summary>
        /// Desync detected event
        /// </summary>
        event Action<int, int, long, long> OnDesyncDetected; // playerId, tick, localHash, remoteHash

        /// <summary>
        /// Frame advantage received event (playerId, senderTick)
        /// </summary>
        event Action<int, int> OnFrameAdvantageReceived;

        /// <summary>
        /// Local player ID assigned event (client: fired after SyncComplete is received)
        /// </summary>
        event Action<int> OnLocalPlayerIdAssigned;

        /// <summary>
        /// Set the current local tick (included in CommandMessage.SenderTick)
        /// </summary>
        void SetLocalTick(int tick);

        /// <summary>
        /// Send a full-state request to the host
        /// </summary>
        void SendFullStateRequest(int currentTick);

        /// <summary>
        /// Send a full-state response to a specific peer (host only)
        /// </summary>
        void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash);

        /// <summary>
        /// Broadcast the full state to every remote peer. Host / SD-server only — the
        /// client / guest implementation skips or throws. Provided as a unified API across
        /// modes so callers can issue a single-shot rebroadcast (e.g. corrective reset)
        /// without branching on transport type.
        /// </summary>
        void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast);

        /// <summary>
        /// Client full-state request (peerId, requestTick) — received by host
        /// </summary>
        event Action<int, int> OnFullStateRequested;

        /// <summary>
        /// Full state received from host (tick, stateData, stateHash, kind) — received by client
        /// </summary>
        event Action<int, byte[], long, FullStateKind> OnFullStateReceived;

        /// <summary>
        /// Player disconnected event (reconnect grace period started).
        /// Fires only during Playing. Disconnects before Playing fire as OnPlayerLeft.
        /// </summary>
        event Action<IPlayerInfo> OnPlayerDisconnected;

        /// <summary>
        /// Player reconnected event (host side)
        /// </summary>
        event Action<IPlayerInfo> OnPlayerReconnected;

        /// <summary>
        /// Reconnect attempt in progress event (guest side)
        /// </summary>
        event Action OnReconnecting;

        /// <summary>
        /// Reconnect failed event (guest side)
        /// </summary>
        event Action<string> OnReconnectFailed;

        /// <summary>
        /// Reconnect completed event (guest side)
        /// </summary>
        event Action OnReconnected;

        /// <summary>
        /// New player added via Late Join (playerId, joinTick)
        /// </summary>
        event Action<int, int> OnLateJoinPlayerAdded;
    }
}
