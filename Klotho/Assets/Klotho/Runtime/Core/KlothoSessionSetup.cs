using Microsoft.Extensions.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Configuration required to create a KlothoSession (replaces KlothoSessionConfig).
    /// </summary>
    public class KlothoSessionSetup
    {
        // ── Dependencies ──

        public ILogger Logger { get; set; }
        public ISimulationCallbacks SimulationCallbacks { get; set; }
        public IViewCallbacks ViewCallbacks { get; set; }

        // ── Connection ──

        /// <summary>
        /// Host: specify the transport directly.
        /// Guest: automatically obtained from Connection (this field is ignored).
        /// </summary>
        public Network.INetworkTransport Transport { get; set; }

        /// <summary>
        /// Guest-only: result of KlothoConnection.Connect().
        /// Null indicates host mode.
        /// Contains Transport, SimulationConfig, and handshake results.
        /// </summary>
        public ConnectionResult Connection { get; set; }

        /// <summary>
        /// SD multi-room only: the room ID the client was assigned to.
        /// -1 (default) means single-room or no room-based routing.
        /// Retained for re-sending RoomHandshake on ServerDrivenClientService Reconnect.
        /// </summary>
        public int RoomId { get; set; } = -1;

        // ── DataAssetRegistry ──

        /// <summary>
        /// Externally-built asset registry.
        /// If null, the existing path is used (internal DataAssetRegistry created + registered in RegisterSystems).
        /// </summary>
        public IDataAssetRegistry AssetRegistry { get; set; }

        // ── SimulationConfig ──

        /// <summary>
        /// Host: loaded from a ScriptableObject and specified directly.
        /// Guest: automatically obtained from Connection.SimulationConfig (this field is ignored).
        /// </summary>
        public ISimulationConfig SimulationConfig { get; set; }

        // ── SessionConfig fields (decided by host; ignored on guest — received via GameStartMessage) ──

        public int RandomSeed { get; set; } = 0;
        public int MaxPlayers { get; set; } = 4;
        public int MinPlayers { get; set; } = 2;
        public bool AllowLateJoin { get; set; } = true;
        public int ReconnectTimeoutMs { get; set; } = 60000;
        public int ReconnectMaxRetries { get; set; } = 3;
        public int LateJoinDelayTicks { get; set; } = 10;
        public int ResyncMaxRetries { get; set; } = 3;
        public int DesyncThresholdForResync { get; set; } = 3;
        public int CountdownDurationMs { get; set; } = 3000;
        public int CatchupMaxTicksPerFrame { get; set; } = 200;
    }
}
