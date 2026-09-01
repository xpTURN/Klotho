using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Configuration required to create a KlothoSession (replaces KlothoSessionConfig).
    /// </summary>
    public class KlothoSessionSetup
    {
        // ── Dependencies ──

        public IKLogger Logger { get; set; }

        /// <summary>
        /// Dev escape hatch: when true a Ready-path component-layout mismatch is logged but the peer is
        /// not refused. Default false (refuse). Deliberately here and not in ISimulationConfig — a guest
        /// runs the config it received over the wire, so a config-borne flag would always read its default
        /// on the very peer that needs to turn the check off (an Editor session against a dedicated server).
        /// </summary>
        public bool AllowLayoutMismatch { get; set; }
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

        /// <summary>
        /// Replay-playback session (set by KlothoSessionFlow.StartReplay). The session creates
        /// no network service: without this flag, Create followed the replay metadata's Mode
        /// down the host path and subscribed a full service to the live main transport — a
        /// ghost wiring whose stale-pong flush polluted the server's RTT push decisions and
        /// whose uninitialized SharedClock logged epoch values.
        /// </summary>
        public bool IsReplay { get; set; }

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

        // ── SessionConfig ──

        /// <summary>
        /// Host: specify the config directly (typically a USessionConfig ScriptableObject).
        /// Guest: ignored — received via GameStartMessage / LateJoinAcceptMessage / ReconnectAcceptMessage.
        /// Null falls back to a default SessionConfig instance.
        /// </summary>
        public ISessionConfig SessionConfig { get; set; }

        // ── Reconnect Credentials (cold-start Reconnect persistence) ──

        /// <summary>
        /// Store for persisting cold-start Reconnect credentials.
        /// Optional — when null, cold-start credentials are not persisted (host or anonymous client).
        /// </summary>
        public Network.IReconnectCredentialsStore CredentialsStore { get; set; } = null;

        /// <summary>
        /// App version stamp recorded into persisted credentials for version compatibility checks
        /// during cold-start Reconnect. Typically passed Application.version from the game side.
        /// </summary>
        public string AppVersion { get; set; } = null;

        /// <summary>
        /// Provides a stable device identifier embedded into persisted credentials.
        /// Optional — when null, GetDeviceId returns string.Empty.
        /// </summary>
        public Network.IDeviceIdProvider DeviceIdProvider { get; set; } = null;

        // ── Identity validation (P2P host) ──

        /// <summary>
        /// Authority-side ticket validator (P2P host). Optional; null = no validation (behaviour unchanged).
        /// Wired onto the host KlothoNetworkService in Create. The SD dedicated server is configured
        /// via RoomManager instead, not this setup.
        /// </summary>
        public Network.IPlayerIdentityValidator IdentityValidator { get; set; } = null;

        /// <summary>
        /// The P2P host's own lobby ticket, self-validated when the host adds itself to the roster.
        /// Resolved by the flow from IdentityProvider.GetTicket(). Empty/null when no lobby. Ignored without IdentityValidator.
        /// </summary>
        public string LocalIdentityTicket { get; set; } = null;

        /// <summary>
        /// Optional P2P player-config entitlement guard. When set, the host wires it and also enables
        /// original-ticket propagation with per-peer re-verification; the re-verifier auto-derives from
        /// <see cref="IdentityValidator"/> when that validator also implements
        /// <c>IPropagatedTicketVerifier</c>, and propagation is refused if no re-verifier is available. A
        /// null guard means no entitlement enforcement. The dedicated server configures its guard through
        /// RoomManager instead of this setup.
        /// </summary>
        public Network.IPlayerConfigEntitlementGuard PlayerConfigEntitlementGuard { get; set; } = null;

        // ── Lifecycle Observer ──

        /// <summary>
        /// Optional aggregated lifecycle callback receiver. When non-null, KlothoSession.Create
        /// subscribes its methods to NetworkService / Engine events; KlothoSession.Stop unsubscribes.
        /// Replaces per-game manual +=/-= wiring across StartHost / JoinGameAsync / ReconnectAsync / StopGame.
        /// </summary>
        public IKlothoSessionObserver LifecycleObserver { get; set; } = null;

        // ── Replay Save ──

        /// <summary>
        /// Optional replay output path. Set by KlothoSessionFlow on host / guest paths (convention —
        /// the field is public/settable; Replay leaves it null, Spectator uses a separate
        /// SpectatorSessionSetup type). When non-null, KlothoSession.Create forwards it to
        /// ConfigureReplaySave so Stop() saves the replay.
        /// </summary>
        public string ReplaySavePath { get; set; } = null;
        /// <summary>Whether the replay save also dumps a JSON sidecar.</summary>
        public bool ReplayDumpJson { get; set; } = false;

        /// <summary>
        /// Whether the engine records a replay. Default true. Stamped by KlothoSessionFlow from
        /// <c>KlothoFlowSetup.EnableReplayRecording</c>; per-peer and never on the wire.
        /// </summary>
        public bool EnableReplayRecording { get; set; } = true;
    }
}
