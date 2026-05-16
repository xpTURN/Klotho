using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// ServerDriven-specific extension interface.
    /// Inherits from IKlothoNetworkService so it can be passed directly to KlothoEngine.Initialize().
    /// </summary>
    public interface IServerDrivenNetworkService : IKlothoNetworkService
    {
        bool IsServer { get; }

        /// <summary>
        /// Server → client: verified tick state (tick, confirmedInputs, stateHash)
        /// </summary>
        event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;

        /// <summary>
        /// Server → client: input receipt acknowledgement (ackedTick)
        /// </summary>
        event Action<int> OnInputAckReceived;

        /// <summary>
        /// Client → server: send input (Unreliable)
        /// </summary>
        void SendClientInput(int tick, ICommand command);

        /// <summary>
        /// Client → server: signal that Initial FullState has been applied and the client is ready
        /// for the first server tick. ReliableOrdered. Server side throws NotSupportedException.
        /// </summary>
        void SendBootstrapReady(int playerId);

        // SendFullStateRequest(int) — inherited from IKlothoNetworkService, no need to redeclare

        /// <summary>
        /// Server → client: full-state received (for determinism-failure recovery).
        /// Separated into the SD-specific handler path — wire only this event in Initialize() instead of OnFullStateReceived.
        /// </summary>
        event Action<int, byte[], long> OnServerFullStateReceived;

        /// <summary>
        /// Server → client: bootstrap window closed (firstTick, tickStartTimeMs).
        /// Sent right before the server starts its first tick. Carries the server's actual tick-start
        /// wall-clock so the client can align its accumulator (matters most under timeout paths).
        /// </summary>
        event Action<int, long> OnBootstrapBegin;

        /// <summary>
        /// Server → client: command rejection notification (tick, commandTypeId, reason).
        /// Hint-only — receiver decides whether to clear local latches / surface to UI.
        /// </summary>
        event Action<int, int, RejectionReason> OnCommandRejected;

        /// <summary>
        /// Server only: the slowest client's confirmed-progress tick.
        /// Used as the cleanup baseline for CleanupOldData and to determine snapshot retention range.
        /// Throws NotSupportedException on the client implementation.
        /// </summary>
        int GetMinClientAckedTick();

        /// <summary>
        /// Client only: clear the unacked input resend queue.
        /// Called on Reconnect or when restoring FullState after a determinism failure.
        /// No-op on the server implementation.
        /// </summary>
        void ClearUnackedInputs();

        // BroadcastFullState(int, byte[], long) — inherited from IKlothoNetworkService.
        // SD-server impl broadcasts to all remote SD clients (including spectators); SD-client impl
        // throws NotSupportedException.
    }
}
