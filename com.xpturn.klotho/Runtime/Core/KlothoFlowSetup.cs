using System;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Game-side dependencies for <see cref="KlothoSessionFlow"/>. All 5 entry points share these.
    /// Per-entry-point fields (SimulationConfig / SessionConfig / Connection / RoomId) are passed
    /// as method arguments instead.
    /// </summary>
    public class KlothoFlowSetup
    {
        public IKLogger Logger { get; set; }

        /// <summary>
        /// Default transport for <c>Connection == null</c> entry points (host / replay).
        /// Guest async wrappers take an explicit transport; the live <c>Connection.Transport</c>
        /// overrides this for guest paths inside <c>KlothoSession.Create</c>.
        /// </summary>
        public INetworkTransport Transport { get; set; }

        public IDataAssetRegistry AssetRegistry { get; set; }
        public IReconnectCredentialsStore CredentialsStore { get; set; }
        public string AppVersion { get; set; }
        public IDeviceIdProvider DeviceIdProvider { get; set; }
        // Lobby identity for the guest connect path. Both are optional and independent.
        public IPlayerIdentityProvider IdentityProvider { get; set; }
        public string ClaimedDisplayName { get; set; }
        // Authority-side ticket validator (P2P host). Optional; null = no validation (behaviour unchanged).
        // The SD dedicated server injects its validator via the server config / RoomManager instead.
        public IPlayerIdentityValidator IdentityValidator { get; set; }
        // Optional P2P player-config entitlement guard. When set, the host enables original-ticket
        // propagation with per-peer re-verification; null means no entitlement enforcement.
        public IPlayerConfigEntitlementGuard PlayerConfigEntitlementGuard { get; set; }
        public IKlothoSessionObserver LifecycleObserver { get; set; }

        /// <summary>
        /// Optional replay output path. KlothoSessionFlow stamps this onto host / guest sessions
        /// (Replay / Spectator are omitted) so the framework saves the replay on Stop. Null disables.
        /// </summary>
        public string ReplaySavePath { get; set; }
        /// <summary>Whether the replay save also dumps a JSON sidecar.</summary>
        public bool ReplayDumpJson { get; set; }

        /// <summary>
        /// Whether the engine records a replay for host / guest sessions. Default true — recording is
        /// what the replay file, the tick-0 snapshot injection, and any later server-side verification
        /// are all made of, so turning it off is opt-out, never opt-in.
        ///
        /// Per-peer and never on the wire: recording is a local decision about this machine's memory
        /// and disk, unlike the SessionConfig fields the host imposes on everyone.
        ///
        /// Set false for a long-running solo session that will never save a replay — the recorder's
        /// buffer grows with the match and is only bounded by its end. Pair it with a null
        /// ReplaySavePath; Build() flags the combination of "not recording" and "save on Stop".
        /// </summary>
        public bool EnableReplayRecording { get; set; } = true;

        /// <summary>
        /// Invoked once sim/session config are known. The game builds Sim/View callbacks
        /// sized against the supplied configs. For Normal guest the supplied <c>sessionCfg</c>
        /// is the seed value (the live session is reseeded from GameStartMessage shortly after).
        /// For LateJoin / Reconnect / Spectator the supplied <c>sessionCfg</c> is server-authoritative.
        /// For Replay the supplied <c>sessionCfg</c> is <c>null</c> — the game must handle that case.
        /// </summary>
        public Func<ISimulationConfig, ISessionConfig, SessionCallbacks> CallbacksFactory { get; set; }

        /// <summary>
        /// Optional. When set, the framework calls this factory and broadcasts the returned
        /// <see cref="PlayerConfigBase"/> via <see cref="KlothoSession.SendPlayerConfig"/>
        /// on guest entry paths (Normal join / LateJoin / cold-start Reconnect through
        /// <c>CreateForConnection</c>) and on <c>StartLocal</c>.
        ///
        /// Host is intentionally NOT auto-sent: <c>FireOnSessionCreated</c> fires before
        /// <c>HostGame</c> assigns <c>IsHost</c> / <c>LocalPlayerId</c>, so the host has no
        /// authoritative identity at that point. Host code must call
        /// <c>session.SendPlayerConfig(...)</c> manually after <c>HostGame</c> + <c>Transport.Listen</c>
        /// succeed. <c>StartLocal</c> is exempt from that reasoning rather than from the rule: it
        /// controls its own ordering and fires the callback after <c>HostGame</c> (and before
        /// <c>SetReady</c>), so a solo player's selection is in place for tick 0.
        ///
        /// Skipped on replay / spectator (no NetworkService).
        ///
        /// The factory is invoked every session-create (not cached). Read the latest local
        /// selection inside the factory body so reconnect-induced re-sends pick up the most
        /// recent intent.
        ///
        /// If a game-side guest <c>OnSessionCreated</c> handler also calls <c>SendPlayerConfig</c>,
        /// both invocations reach the engine and <c>OnPlayerConfigReceived</c> fires twice
        /// (second time with <c>firstTime == false</c>).
        ///
        /// Set to null (default) to opt out — the game can still call <c>session.SendPlayerConfig</c>
        /// manually on any path.
        /// </summary>
        public Func<PlayerConfigBase> InitialPlayerConfigFactory { get; set; }

        /// <summary>
        /// Optional. When set, the framework instantiates a fresh transport via this factory
        /// for entry points that need their own transport (currently: the no-transport
        /// <c>SpectateAsync</c> overload in <c>KlothoSessionFlowAsync</c>). The factory must
        /// return a non-null <see cref="INetworkTransport"/>.
        ///
        /// Lifetime: the returned transport is handed to <c>KlothoSession.CreateSpectator</c>
        /// and held as the spectator session's transport. <c>KlothoSession.Stop</c> tears it down
        /// via <c>SpectatorService.Disconnect</c> (which calls <c>Transport.Disconnect</c>
        /// internally), with a defensive fallback to <c>_spectatorTransport.Disconnect</c> when
        /// bootstrap fails before the service is wired. Callers must not retain the returned
        /// reference or call Disconnect on it.
        ///
        /// Leave null to opt out — callers must supply transports explicitly to the
        /// transport-bearing overloads.
        /// </summary>
        public Func<INetworkTransport> SpectatorTransportFactory { get; set; }
    }
}
