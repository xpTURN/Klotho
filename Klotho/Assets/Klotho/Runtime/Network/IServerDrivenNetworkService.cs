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

        // SendFullStateRequest(int) — inherited from IKlothoNetworkService, no need to redeclare

        /// <summary>
        /// Server → client: full-state received (for determinism-failure recovery).
        /// Separated into the SD-specific handler path — wire only this event in Initialize() instead of OnFullStateReceived.
        /// </summary>
        event Action<int, byte[], long> OnServerFullStateReceived;

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

        /// <summary>
        /// Server only: broadcast full state to all remote SD clients (including spectators).
        /// Used for the initial FullState transmission (blocking) at session start.
        /// The server itself is not included in the remote peer list, so it is naturally excluded.
        /// Throws NotSupportedException on the client implementation.
        /// </summary>
        void BroadcastFullState(int tick, byte[] stateData, long stateHash);
    }
}
