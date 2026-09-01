using System;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>Thrown when a flow setup fails build-time coherence validation.</summary>
    public sealed class FlowSetupValidationException : ArgumentException
    {
        public FlowSetupValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Fluent assembler for <see cref="KlothoFlowSetup"/>. Groups the route-agnostic dependencies
    /// into cohesive feature methods so related fields are set together, then validates feature
    /// coherence at Build(). The required CallbacksFactory is a constructor argument (compile-time).
    /// Object-initializer construction of KlothoFlowSetup remains supported as an escape hatch.
    /// Build() runs once at startup — not a per-frame path.
    /// </summary>
    public sealed class KlothoFlowSetupBuilder
    {
        private readonly KlothoFlowSetup _s = new KlothoFlowSetup();

        /// <param name="callbacksFactory">Required. Builds Sim/View callbacks per session-create.</param>
        public KlothoFlowSetupBuilder(Func<ISimulationConfig, ISessionConfig, SessionCallbacks> callbacksFactory)
        {
            _s.CallbacksFactory = callbacksFactory
                ?? throw new ArgumentNullException(nameof(callbacksFactory));
        }

        // ── Always-relevant dependencies ──
        public KlothoFlowSetupBuilder WithLogger(IKLogger logger)              { _s.Logger = logger; return this; }
        public KlothoFlowSetupBuilder WithTransport(INetworkTransport transport) { _s.Transport = transport; return this; }
        public KlothoFlowSetupBuilder WithAssetRegistry(IDataAssetRegistry registry) { _s.AssetRegistry = registry; return this; }
        public KlothoFlowSetupBuilder WithLifecycleObserver(IKlothoSessionObserver observer) { _s.LifecycleObserver = observer; return this; }

        // ── Replay save output (host / guest sessions; framework saves on Stop) ──
        // null path (default) disables saving — matches the always-relevant group's no-null-check style.
        public KlothoFlowSetupBuilder WithReplaySave(string path, bool dumpJson = false) { _s.ReplaySavePath = path; _s.ReplayDumpJson = dumpJson; return this; }

        // ── Replay recording (host / guest sessions; on by default) ──
        // Opt out for a long solo session that will never save a replay: the recorder's buffer grows
        // with the match. Everything downstream of a recording — the file, the tick-0 snapshot
        // injection, any later verification — goes with it, so this is deliberately not a knob you
        // reach for by default. Build() flags "not recording" combined with WithReplaySave.
        public KlothoFlowSetupBuilder WithoutReplayRecording() { _s.EnableReplayRecording = false; return this; }

        // ── Client handshake identity (guest / reconnect entry points) ──
        public KlothoFlowSetupBuilder WithHandshake(string appVersion, IDeviceIdProvider deviceIdProvider)
        {
            _s.AppVersion       = appVersion       ?? throw new ArgumentNullException(nameof(appVersion));
            _s.DeviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
            return this;
        }

        // ── Reconnect credentials store (requires handshake — checked at Build) ──
        public KlothoFlowSetupBuilder WithReconnect(IReconnectCredentialsStore store)
        {
            _s.CredentialsStore = store ?? throw new ArgumentNullException(nameof(store));
            return this;
        }

        // ── Auto PlayerConfig send on guest / reconnect entry ──
        public KlothoFlowSetupBuilder WithAutoPlayerConfig(Func<PlayerConfigBase> factory)
        {
            _s.InitialPlayerConfigFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            return this;
        }

        // ── Lobby identity — the guest carries a lobby-issued ticket in the join handshake.
        // Validation is host/server-side; this builder only attaches the provider that supplies the ticket.
        public KlothoFlowSetupBuilder WithLobbyIdentity(IPlayerIdentityProvider provider)
        {
            _s.IdentityProvider = provider ?? throw new ArgumentNullException(nameof(provider));
            return this;
        }

        // ── No-lobby claimed display name — an unverified nickname for the local player.
        // Independent of WithLobbyIdentity; ignored by the authority when a validated ticket is present.
        public KlothoFlowSetupBuilder WithDisplayName(string displayName)
        {
            _s.ClaimedDisplayName = displayName;
            return this;
        }

        // ── Authority-side ticket validator (P2P host). Validates incoming guest tickets and the host's
        // own ticket. Optional; unset = no validation (behaviour unchanged). The SD dedicated server
        // injects its validator via the server config / RoomManager instead, not this builder.
        public KlothoFlowSetupBuilder WithIdentityValidator(IPlayerIdentityValidator validator)
        {
            _s.IdentityValidator = validator ?? throw new ArgumentNullException(nameof(validator));
            return this;
        }

        // ── P2P player-config entitlement guard. When set, the host enables original-ticket propagation with
        // per-peer re-verification and every peer clamps client selections against the lobby-signed
        // entitlement. Requires WithIdentityValidator, since the validator doubles as the re-verifier; when
        // unset, no entitlement enforcement is applied and behaviour is unchanged.
        public KlothoFlowSetupBuilder WithPlayerConfigEntitlementGuard(IPlayerConfigEntitlementGuard guard)
        {
            _s.PlayerConfigEntitlementGuard = guard ?? throw new ArgumentNullException(nameof(guard));
            return this;
        }

        // ── Spectator no-transport overload support ──
        public KlothoFlowSetupBuilder WithSpectator(Func<INetworkTransport> transportFactory)
        {
            _s.SpectatorTransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            return this;
        }

        // Shared coherence predicate: a P2P entitlement guard needs the identity validator (which doubles as
        // its re-verifier). Called from Build() (throw, fluent path) and KlothoSession.Create (log, the
        // object-initializer escape hatch) so the two enforcement sites cannot silently diverge.
        internal static bool GuardRequiresValidatorViolated(
            IPlayerConfigEntitlementGuard guard, IPlayerIdentityValidator validator)
            => guard != null && validator == null;

        /// <summary>
        /// Validates feature coherence and returns the assembled setup.
        /// Hard errors (always throw):
        ///   • WithReconnect set without WithHandshake — reconnect credentials are produced by a prior
        ///     normal join, which requires handshake identity (the reconnect handshake itself uses only
        ///     the persisted credentials). A reconnect-only setup could never create the credentials it
        ///     reconnects with.
        /// Advisory (logged; strict=true promotes to throw) — best-effort:
        ///   • cannot drive any connect entry point: no Transport (host/replay), no handshake identity
        ///     (guest/reconnect), no SpectatorTransportFactory (spectator). Replay-from-file is exempt
        ///     (offline — needs neither), so a replay-only setup is intentionally not flagged.
        /// </summary>
        public KlothoFlowSetup Build(bool strict = false)
        {
            // AppVersion can only be null here if WithHandshake was never called (it sets both
            // atomically); the AppVersion check is defensive for the object-initializer escape hatch.
            if (_s.CredentialsStore != null && (_s.DeviceIdProvider == null || _s.AppVersion == null))
                throw new FlowSetupValidationException(
                    "WithReconnect requires WithHandshake (AppVersion + DeviceIdProvider) — reconnect credentials are minted by a prior normal join, which needs handshake identity.");

            // A P2P entitlement guard is only wired when a validator is present (KlothoSession.Create nests the
            // guard setup inside the validator branch, since the validator doubles as the re-verifier). Without
            // the validator the guard is silently dropped — entitlement enforcement OFF with no signal. Fail
            // fast here. Safe as a hard error: FlowSetup's guard is P2P-only (SD wires its guard via
            // RoomManagerConfig, not this builder), so this cannot break an SD dedicated server.
            if (GuardRequiresValidatorViolated(_s.PlayerConfigEntitlementGuard, _s.IdentityValidator))
                throw new FlowSetupValidationException(
                    "WithPlayerConfigEntitlementGuard requires WithIdentityValidator (the validator doubles as the P2P re-verifier) — without it the guard is silently ignored and entitlement enforcement is OFF.");

            // Spectator drives via SpectatorTransportFactory; replay-from-file needs neither — both exempt.
            // WithLobbyIdentity (a ticket-only guest) also signals a guest connect path,
            // so a guest configured with just a ticket provider is not falsely flagged.
            if (_s.Transport == null && _s.DeviceIdProvider == null && _s.SpectatorTransportFactory == null && _s.IdentityProvider == null)
            {
                const string msg = "Flow setup has no Transport (host/replay), no handshake identity (guest/reconnect), and no SpectatorTransportFactory — it can drive no connect entry point (replay-from-file is still possible).";
                if (strict) throw new FlowSetupValidationException(msg);
                _s.Logger?.KWarning($"[KlothoFlowSetupBuilder] {msg}");
            }

            // Advisory: recording off with a save path set. Nothing breaks — SaveToFile finds no data
            // and logs — but it logs on every Stop, and the pairing says the author expected a file.
            if (!_s.EnableReplayRecording && _s.ReplaySavePath != null)
            {
                const string msg = "WithoutReplayRecording is set together with WithReplaySave — there will be no replay to save, and Stop() will warn every time.";
                if (strict) throw new FlowSetupValidationException(msg);
                _s.Logger?.KWarning($"[KlothoFlowSetupBuilder] {msg}");
            }

            // Advisory: a validator is host/server-side. Without a host-capable entry point
            // (Transport, used by StartHostAndListen) it is never consulted. The SD dedicated server
            // injects its validator outside this builder, so this only flags a misconfigured P2P setup.
            if (_s.IdentityValidator != null && _s.Transport == null)
            {
                const string msg = "WithIdentityValidator is set but there is no Transport (host entry point) — the validator will never be consulted on this setup.";
                if (strict) throw new FlowSetupValidationException(msg);
                _s.Logger?.KWarning($"[KlothoFlowSetupBuilder] {msg}");
            }
            return _s;
        }
    }
}
