using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

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
        string DisplayName { get; }
        string Account { get; }
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
        void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger);

        /// <summary>
        /// Create room (host)
        /// </summary>
        void CreateRoom(string roomName, int maxPlayers);

        /// <summary>
        /// Join room
        /// </summary>
        void JoinRoom(string roomName);

        /// <summary>
        /// Leave room.
        /// keepReconnectCredentials: when true, persisted cold-start Reconnect credentials are
        /// retained (used by process-shutdown paths so a relaunch can still attempt Reconnect).
        /// Default false matches "user-intent leave" — credentials are discarded.
        /// </summary>
        void LeaveRoom(bool keepReconnectCredentials = false);

        /// <summary>
        /// Set ready state
        /// </summary>
        void SetReady(bool ready);

        /// <summary>
        /// Sends a command (transmits local input to other players / server).
        ///
        /// <para><b>Ownership contract</b>: the caller transfers sole ownership of <paramref name="command"/>
        /// to the implementation on entry. The caller MUST NOT retain or reuse the instance after this call
        /// returns — implementations may store it, return it to CommandPool, or hand it to the transport
        /// for serialization. Violating this contract risks pool poisoning.</para>
        ///
        /// <para>Exception: service-internal cache patterns (e.g. <c>_emptyCommandCache</c> reused across
        /// SendCommand calls) are safe when the implementation serializes the instance to bytes
        /// synchronously before this method returns AND does not pool-return the instance — the caller's
        /// retained reference is then no longer aliased to a buffer-stored or pool-returned instance. This
        /// holds for the current P2P and SD client implementations but is an implementation property,
        /// not a contract guarantee.</para>
        /// </summary>
        void SendCommand(ICommand command);

        /// <summary>
        /// Wait for commands of a specific tick
        /// </summary>
        void RequestCommandsForTick(int tick);

        /// <summary>
        /// Send sync hash. <paramref name="cmdHash"/> is the command digest of the tick's executed
        /// set — carried alongside the state hash so a desync can be classified as input vs state
        /// divergence at the comparison point. Diagnostic-only; never drives recovery.
        /// </summary>
        void SendSyncHash(int tick, long hash, long cmdHash);

        /// <summary>
        /// Guest → host (Reliable): reports that determinism recovery is failing on this peer
        /// (post-apply hash mismatch or resync retries exhausted). Drives recovery ladder
        /// rungs 3-4 on the host. No-op outside P2P guests.
        /// </summary>
        void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash);

        /// <summary>
        /// Host → guests (ReliableOrdered): broadcast that the match is aborted — recovery
        /// ladder exhausted. reason maps to Core.AbortReason. No-op outside P2P host.
        /// </summary>
        void BroadcastMatchAbort(byte reason);

        /// <summary>
        /// Invalidates the local peer's stored sync hashes for ticks >= fromTick.
        /// Called by the engine on rollback: re-simulation may change state at those ticks
        /// (e.g. presumed-drop empty fills replaced by late real commands), so stale local
        /// hashes must not be compared against. No-op outside P2P.
        /// </summary>
        void InvalidateLocalSyncHashes(int fromTick);

        /// <summary>
        /// Invalidates ALL peers' stored sync hashes for ticks >= fromTick (local and remote).
        /// Called by the engine on FullState apply (resync / corrective reset): the post-apply
        /// re-simulation recomputes hashes that would otherwise be compared against pre-reset
        /// remote entries straddling the reset boundary, producing a false mismatch.
        /// No-op outside P2P.
        /// </summary>
        void InvalidateSyncHashes(int fromTick);

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
        /// Host-side: a guest reported failing recovery (playerId, tick). Drives the host's
        /// corrective-reset attempt budget and the rung-4 abort.
        /// </summary>
        event Action<int, int> OnResyncFailureReported;

        /// <summary>
        /// Guest-side: the host broadcast a match abort (reason maps to Core.AbortReason).
        /// </summary>
        event Action<int> OnMatchAbortReceived;

        /// <summary>
        /// Raised for every completed sync-hash comparison, match and mismatch alike.
        /// Drives event-based promotion of the engine's last-matched sync anchor:
        /// a matched comparison promotes, absence of comparisons (dropped hashes) skips.
        /// </summary>
        event Action<int, int, bool> OnSyncHashCompared; // tick, remotePlayerId, matched

        /// <summary>
        /// Frame advantage received event (playerId, senderTick, senderAdvantage).
        /// senderAdvantage is the sender's measured frame-advantage at send time.
        /// </summary>
        event Action<int, int, int> OnFrameAdvantageReceived;

        /// <summary>
        /// Local player ID assigned event (client: fired after SyncComplete is received)
        /// </summary>
        event Action<int> OnLocalPlayerIdAssigned;

        /// <summary>
        /// Set the current local tick (included in CommandMessage.SenderTick)
        /// </summary>
        void SetLocalTick(int tick);

        /// <summary>
        /// Set the current local frame-advantage (round of CalculateLocalAdvantage), included in
        /// CommandMessage.SenderAdvantage for the frame-advantage exchange. Pushed each tick from the
        /// engine OUTSIDE the timesync-enabled guard so a throttle-disabled guest still reports a
        /// truthful advantage to the host. No-op for server-driven services.
        /// </summary>
        void SetLocalAdvantage(int advantage);

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
        /// Reconnect failed event (guest side). The value is one of
        /// <see cref="ReconnectRejectReason"/> values.
        /// </summary>
        event Action<ReconnectRejectReason> OnReconnectFailed;

        /// <summary>
        /// Reconnect completed event (guest side)
        /// </summary>
        event Action OnReconnected;

        /// <summary>
        /// New player added via Late Join (playerId, joinTick)
        /// </summary>
        event Action<int, int> OnLateJoinPlayerAdded;

        /// <summary>
        /// Fired when <see cref="Phase"/> changes. Argument is the new phase. Subscribers must not
        /// throw — exceptions surface up the setter call site (NetworkService internals).
        /// </summary>
        event Action<SessionPhase> OnPhaseChanged;

        /// <summary>
        /// Fired when <see cref="PlayerCount"/> changes. Argument is the new count.
        /// </summary>
        event Action<int> OnPlayerCountChanged;

        /// <summary>
        /// Fired when <see cref="AllPlayersReady"/> changes. Argument is the new value.
        /// </summary>
        event Action<bool> OnAllPlayersReadyChanged;
    }
}
