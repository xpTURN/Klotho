using System;
using System.Threading;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// 5-route session builder. Sync primitives only — UniTask wrappers live in
    /// <c>KlothoSessionFlowAsync</c> (Runtime.Unity).
    /// </summary>
    public sealed class KlothoSessionFlow
    {
        private readonly KlothoFlowSetup _setup;

        public KlothoSessionFlow(KlothoFlowSetup setup)
        {
            _setup = setup ?? throw new ArgumentNullException(nameof(setup));
            if (setup.CallbacksFactory == null)
                throw new ArgumentException("KlothoFlowSetup.CallbacksFactory is required.", nameof(setup));
        }

        // ── Host ──

        public KlothoSession StartHost(ISimulationConfig simulationConfig, ISessionConfig sessionConfig)
        {
            var session = BuildHostSession(simulationConfig, sessionConfig);
            FireOnSessionCreated(session, SessionEntryKind.Host);
            return session;
        }

        /// <summary>
        /// Everything StartHost does except firing the observer: validate, build callbacks, create the
        /// session. Split out because StartLocal needs the same construction but must fire a different
        /// kind, at a later point (after HostGame, so the auto-PlayerConfig send has an identity to use).
        /// Calling StartHost from StartLocal would fire the observer twice — once as Host, once as Local —
        /// and auto-wire under the wrong kind.
        /// </summary>
        private KlothoSession BuildHostSession(ISimulationConfig simulationConfig, ISessionConfig sessionConfig)
        {
            if (simulationConfig == null) throw new ArgumentNullException(nameof(simulationConfig));
            if (sessionConfig == null)    throw new ArgumentNullException(nameof(sessionConfig));

            // The host authors this config and every guest ends up running it, so an invalid one stops here.
            // KlothoEngine.Initialize cannot catch it: its authoritative-caller test reads IsHost, which only
            // becomes true in HostGame -> CreateRoom, i.e. after Initialize — a P2P host therefore took the
            // guest's log-and-proceed branch. This entry point knows it is hosting without having to ask.
            simulationConfig.Validate();

            var callbacks = _setup.CallbacksFactory(simulationConfig, sessionConfig);
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                Transport = _setup.Transport,
                AssetRegistry = _setup.AssetRegistry,
                LifecycleObserver = _setup.LifecycleObserver,
                ReplaySavePath = _setup.ReplaySavePath,
                ReplayDumpJson = _setup.ReplayDumpJson,
                EnableReplayRecording = _setup.EnableReplayRecording,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                SimulationConfig = simulationConfig,
                SessionConfig = sessionConfig,
                IdentityValidator = _setup.IdentityValidator, // P2P host: validate guest tickets + its own
                LocalIdentityTicket = _setup.IdentityProvider?.GetTicket(), // host's own ticket for self-validation
                PlayerConfigEntitlementGuard = _setup.PlayerConfigEntitlementGuard, // enables clamp + ticket propagation
            });
            return session;
        }

        /// <summary>
        /// Creates a host session, activates the host role, and starts listening — in the correct order,
        /// with automatic teardown on any failure after the session is created. Folds the StartHost +
        /// HostGame + Transport.Listen sequence into a single entry point so callers never orphan a
        /// half-started session.
        ///
        /// Outcomes:
        ///   • success            → returns the running session.
        ///   • listen bind failed → tears the session down and returns null (expected, recoverable).
        ///   • any other failure  → tears the session down, then rethrows (e.g. HostGame/CreateRoom).
        /// In every non-success case the session is already torn down before this method returns/throws.
        /// </summary>
        public KlothoSession StartHostAndListen(
            ISimulationConfig simulationConfig,
            ISessionConfig    sessionConfig,
            string            roomName,
            string            address,
            int               port)
        {
            if (_setup.Transport == null)
                throw new InvalidOperationException("KlothoFlowSetup.Transport is required for StartHostAndListen.");

            var session = StartHost(simulationConfig, sessionConfig);
            try
            {
                session.HostGame(roomName, sessionConfig.MaxPlayers);
                if (!_setup.Transport.Listen(address, port, sessionConfig.MaxPlayers))
                {
                    _setup.Logger?.KError($"Host listen failed on {address}:{port} — tearing down session.");
                    session.Stop();
                    return null;
                }
            }
            catch
            {
                // Tear the half-started session down so callers never orphan it; swallow any
                // secondary teardown error so the original exception is the one that propagates.
                try { session.Stop(); }
                catch (Exception stopEx) { _setup.Logger?.KError(stopEx, $"Teardown after host-bootstrap failure threw"); }
                throw;
            }
            return session;
        }

        // ── Local (single player) ──

        /// <summary>
        /// Starts a single-player session: one local player, no socket, running immediately.
        ///
        /// <para>Folds the four steps a solo session needs — create, take the host role, send the
        /// local PlayerConfig, declare ready — into one call, because getting them wrong fails
        /// silently. SetReady before HostGame returns without a log (the phase gate), and a
        /// MinPlayers other than 1 leaves the start gate false forever, also without a log.</para>
        ///
        /// <para>The transport is whatever the setup carries; <see cref="Network.NullTransport"/> is
        /// the one that costs nothing — no bind, no firewall prompt, no port held. A real transport
        /// works too and simply never has a peer.</para>
        ///
        /// <para><paramref name="sessionConfig"/> is COPIED, not used. MinPlayers is forced to 1 on
        /// the copy — that is a condition for the match to start at all, not a preference. Everything
        /// else is left as authored, including CountdownDurationMs (the default 3000 means this
        /// returns a session that does nothing for three seconds — author 0 for an instant start) and
        /// MaxPlayers (games derive bot id ranges from it, so narrowing it would change tick-0 state).
        /// The caller's object is never written to; the Unity inspector config is a ScriptableObject
        /// whose play-mode edits persist.</para>
        ///
        /// <para>Observers receive <see cref="SessionEntryKind.Local"/>. On any failure after the
        /// session exists it is torn down before this returns or throws.</para>
        /// </summary>
        public KlothoSession StartLocal(
            ISimulationConfig simulationConfig,
            ISessionConfig    sessionConfig,
            string            roomName = "Local")
        {
            if (sessionConfig == null) throw new ArgumentNullException(nameof(sessionConfig));

            var localConfig = SessionConfig.CopyOf(sessionConfig);
            if (localConfig.MinPlayers != 1)
            {
                _setup.Logger?.KInformation(
                    $"[KlothoSessionFlow] StartLocal: MinPlayers {localConfig.MinPlayers} -> 1 (on a copy; " +
                    $"the authored config is untouched). A single-player match cannot start otherwise.");
                localConfig.MinPlayers = 1;
            }

            var session = BuildHostSession(simulationConfig, localConfig);
            try
            {
                // Order is load-bearing. HostGame first: it assigns IsHost / LocalPlayerId and moves the
                // phase to Synchronized, which is what lets the auto-PlayerConfig send below have an
                // identity and what opens SetReady's gate. Then the observer, which also performs the
                // auto-wiring. SetReady last: it starts the match.
                session.HostGame(roomName, localConfig.MaxPlayers);
                FireOnSessionCreated(session, SessionEntryKind.Local);
                session.SetReady(true);
            }
            catch
            {
                // Mirrors StartHostAndListen: never leave a half-started session behind, and let the
                // original failure be the one that propagates. Stop is idempotent, so the teardown
                // FireOnSessionCreated already performs on an observer throw is not a problem here.
                try { session.Stop(); }
                catch (Exception stopEx) { _setup.Logger?.KError(stopEx, $"Teardown after StartLocal failure threw"); }
                throw;
            }
            return session;
        }

        // ── Replay ──

        public KlothoSession StartReplay(IReplayData replayData, ISimulationConfig simulationConfig)
            => StartReplay(replayData, simulationConfig, KlothoEngine.ReplayInitialState.RestoreSnapshot);

        /// <summary>
        /// Starts a replay session, choosing how tick 0 is obtained — see
        /// <see cref="KlothoEngine.ReplayInitialState"/>. Reconstruct is for verification, not viewing:
        /// read <c>session.Engine.ReplayTick0Reconstructed</c> and <c>ReplayTick0Hash</c> afterwards.
        /// </summary>
        public KlothoSession StartReplay(IReplayData replayData, ISimulationConfig simulationConfig,
            KlothoEngine.ReplayInitialState initialState)
        {
            if (replayData == null)       throw new ArgumentNullException(nameof(replayData));
            if (simulationConfig == null) throw new ArgumentNullException(nameof(simulationConfig));

            // Replay does not consult sessionCfg; the recorded engine state carries the snapshot.
            // CallbacksFactory receives sessionCfg = null — the game ignores it in replay mode.
            var callbacks = _setup.CallbacksFactory(simulationConfig, null);
            // Replay owns no connection: IsReplay skips network-service creation in Create, and
            // Transport is deliberately NOT passed — if the flag were ever lost, the host path
            // would fail fast (NRE on a null transport) instead of silently re-wiring a ghost
            // service onto the live main transport.
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                AssetRegistry = _setup.AssetRegistry,
                LifecycleObserver = _setup.LifecycleObserver,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                SimulationConfig = simulationConfig,
                IsReplay = true,
            });
            session.Engine.StartReplay(replayData, initialState);
            FireOnSessionCreated(session, SessionEntryKind.Replay);
            return session;
        }

        /// <summary>
        /// Loads a replay file, validates its metadata-derived SimulationConfig, and starts a replay session.
        /// Equivalent to <c>StartReplay(loader.CurrentReplayData, replayData.Metadata.ToSimulationConfig())</c>
        /// with internal validation. Throws <see cref="ReplayLoadException"/> on file-not-found,
        /// file-read I/O, malformed payload, or incompatible metadata. <see cref="ArgumentException"/>
        /// from <c>simConfig.Validate</c> is wrapped as <see cref="ReplayLoadException"/> so callers
        /// catch a single exception type across all replay-load failures.
        /// </summary>
        public KlothoSession StartReplayFromFile(string filePath)
            => StartReplayFromFile(filePath, KlothoEngine.ReplayInitialState.RestoreSnapshot);

        /// <summary>
        /// Loads a replay file and starts playback, choosing how tick 0 is obtained — see
        /// <see cref="KlothoEngine.ReplayInitialState"/>.
        /// </summary>
        public KlothoSession StartReplayFromFile(string filePath, KlothoEngine.ReplayInitialState initialState)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("Replay file path is required.", nameof(filePath));

            var loader = new ReplaySystem(new CommandFactory(), _setup.Logger);
            loader.LoadFromFile(filePath);
            var replayData = loader.CurrentReplayData
                ?? throw new ReplayLoadException($"Replay load produced null data: {filePath}");

            var simConfig = replayData.Metadata.ToSimulationConfig();
            try
            {
                simConfig.Validate();
            }
            catch (ArgumentException e)
            {
                throw new ReplayLoadException($"Replay metadata is invalid: {filePath}", e);
            }

            return StartReplay(replayData, simConfig, initialState);
        }

        // ── Guest (sync primitive — async wrappers in KlothoSessionFlowAsync) ──

        /// <summary>
        /// Builds a guest session from a completed <see cref="ConnectionResult"/>.
        /// <paramref name="sessionConfigSeed"/> is used only for Normal join (it is ignored by
        /// <see cref="KlothoSession.Create"/> on LateJoin / Reconnect, which read AcceptMessage
        /// payloads). Pass the local Inspector SessionConfig as seed.
        /// </summary>
        public KlothoSession CreateForConnection(
            ConnectionResult result, int roomId, ISessionConfig sessionConfigSeed)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var callbacks = _setup.CallbacksFactory(result.SimulationConfig, sessionConfigSeed);
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                Connection = result,
                AssetRegistry = _setup.AssetRegistry,
                LifecycleObserver = _setup.LifecycleObserver,
                ReplaySavePath = _setup.ReplaySavePath,
                ReplayDumpJson = _setup.ReplayDumpJson,
                EnableReplayRecording = _setup.EnableReplayRecording,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                RoomId = roomId,
                SessionConfig = sessionConfigSeed,
                CredentialsStore = _setup.CredentialsStore,
                AppVersion = _setup.AppVersion,
                DeviceIdProvider = _setup.DeviceIdProvider,
                // A P2P guest needs the re-verifier (auto-derived from the validator) and the entitlement
                // guard so it can independently re-verify propagated tickets and clamp configs locally. The
                // validator is never invoked for join-validation on a guest — that is host-only — so passing
                // it here is harmless and serves only as the re-verifier source in KlothoSession.Create.
                IdentityValidator = _setup.IdentityValidator,
                PlayerConfigEntitlementGuard = _setup.PlayerConfigEntitlementGuard,
            });
            FireOnSessionCreated(session, SessionEntryKind.Guest);
            return session;
        }

        // ── Spectator wiring (Core hook called from KlothoSessionFlowAsync.SpectateAsync) ──

        // Builds a SpectatorSessionSetup that delegates CallbacksFactory to KlothoFlowSetup
        // and pre-stamps the rest from _setup. The async wrapper drives the pump.
        internal SpectatorSessionSetup BuildSpectatorSetup(
            INetworkTransport spectatorTransport, string hostAddress, int port, int roomId)
        {
            return new SpectatorSessionSetup
            {
                Logger = _setup.Logger,
                AssetRegistry = _setup.AssetRegistry,
                Transport = spectatorTransport,
                HostAddress = hostAddress,
                Port = port,
                RoomId = roomId,
                LifecycleObserver = _setup.LifecycleObserver,
                CallbacksFactory = (simCfg, sessionCfg) =>
                {
                    var cb = _setup.CallbacksFactory(simCfg, sessionCfg);
                    return new SpectatorCallbacks(cb.Simulation, cb.View);
                },
            };
        }

        // Spectator path fires OnSessionCreated after FinishSpectatorBootstrap (Engine non-null).
        // KlothoSessionFlowAsync.SpectateAsync invokes this once the spectator session is ready.
        internal void FireOnSessionCreatedForSpectator(KlothoSession session)
            => FireOnSessionCreated(session, SessionEntryKind.Spectator);

        // Read accessors for the async wrapper (KlothoSessionFlowAsync). Public so cross-assembly
        // adapters (e.g. Godot) can forward them to connection helpers without re-requiring the caller
        // to pass them explicitly.
        public IKLogger Logger => _setup.Logger;
        public IDeviceIdProvider DeviceIdProvider => _setup.DeviceIdProvider;
        // Exposed so the async wrapper can forward these to KlothoConnection.Connect on guest join.
        public IPlayerIdentityProvider IdentityProvider => _setup.IdentityProvider;
        public string ClaimedDisplayName => _setup.ClaimedDisplayName;
        internal Func<INetworkTransport> SpectatorTransportFactory => _setup.SpectatorTransportFactory;

        // True while a main-transport connect handshake (Join / Reconnect) is in flight. The driver
        // reads this to suppress idle-disconnect routing for a handshake's own failure. Set/cleared by
        // the async entry points (KlothoSessionFlowAsync) across the whole handshake including session
        // creation, so it stays true until the session is attached or the failure has propagated.
        internal bool IsConnecting { get; set; }

        // ── Connect-attempt ownership ──
        // The flow owns the single in-flight connect attempt's CTS so callers do not hand-juggle
        // Cancel/Dispose/new per connect. BeginConnectAttempt publishes the new attempt before cancelling
        // the prior one; EndConnectAttempt is identity-guarded so a superseded attempt's finally is a no-op
        // regardless of whether the cancel propagates synchronously or deferred.
        private CancellationTokenSource _connectCts;

        // Entry-point try-block start. Supersedes any prior in-flight attempt and links the new token to an
        // external (e.g. MonoBehaviour destroy) token. Returns the CTS; the caller awaits on cts.Token and
        // passes the same cts back to EndConnectAttempt for the identity guard.
        internal CancellationTokenSource BeginConnectAttempt(CancellationToken external)
        {
            var old = _connectCts;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _connectCts = cts;          // publish the new identity before cancelling the old one
            IsConnecting = true;        // set before the handshake await so the driver gates idle-disconnect
            old?.Cancel();              // superseded attempt's End sees _connectCts != old -> no-op
            old?.Dispose();             // disposed once, after Cancel returns (not re-entrant)
            return cts;
        }

        // Entry-point finally. Only the still-current attempt clears state + disposes, so a superseded
        // attempt's finally cannot clobber the newer attempt's IsConnecting / _connectCts.
        internal void EndConnectAttempt(CancellationTokenSource cts)
        {
            if (_connectCts != cts) return;
            IsConnecting = false;
            _connectCts.Dispose();      // dispose on success/failure releases the external link immediately
            _connectCts = null;
        }

        // Cancels an in-flight connect without starting a new one (stop button / UI reset). No-op if none.
        public void CancelConnect() => _connectCts?.Cancel();

        // Flow teardown: release any residual attempt (no-op if EndConnectAttempt already cleared it).
        // Public so games (separate assemblies) can call it from their teardown.
        public void DisposeConnect()
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
        }

        // ── Shared ──

        // Fires IKlothoSessionObserver.OnSessionCreated(session, kind) after the session is fully
        // constructed and auto-wiring (INetworkServiceReceiver / auto-PlayerConfig) completes.
        // Exception policy (lifecycle transition, mirrors KlothoSessionDriver.Stopping): on observer
        // throw the framework logs, calls session.Stop(), and rethrows — a half-wired session is torn
        // down before the exception propagates.
        private void FireOnSessionCreated(KlothoSession session, SessionEntryKind kind)
        {
            try
            {
                // Auto-inject network service to opt-in callbacks on host/guest entry. Replay/spectator
                // paths skip via the kind gate (replay's NetworkService is non-null but inputless; the
                // null check is a defensive second gate).
                if ((kind == SessionEntryKind.Host || kind == SessionEntryKind.Guest || kind == SessionEntryKind.Local)
                    && session.NetworkService != null
                    && session.SimulationCallbacks is INetworkServiceReceiver recv)
                {
                    recv.SetNetworkService(session.NetworkService);
                }

                // Auto-send PlayerConfig on guest entry paths (Normal / LateJoin / Reconnect through
                // CreateForConnection) and on Local. Host is excluded for an ordering reason, not a
                // policy one: it calls this before HostGame -> CreateRoom assigns IsHost /
                // LocalPlayerId, so there is no authoritative identity to send under yet. StartLocal
                // owns that ordering itself and fires this AFTER HostGame (and before SetReady), so
                // the constraint does not apply and a solo player's selection reaches tick 0.
                // Replay / Spectator have no NetworkService.
                if ((kind == SessionEntryKind.Guest || kind == SessionEntryKind.Local)
                    && _setup.InitialPlayerConfigFactory != null
                    && session.NetworkService != null)
                {
                    var cfg = _setup.InitialPlayerConfigFactory.Invoke();
                    if (cfg != null)
                        session.SendPlayerConfig(cfg);
                }

                // Single role-bearing observer callback. kind is supplied only by the four internal
                // callers (Host / Guest / Replay / Spectator), covering all enum values.
                _setup.LifecycleObserver?.OnSessionCreated(session, kind);
            }
            catch (Exception e)
            {
                _setup.Logger?.KError(e, $"[KlothoSessionFlow] OnSessionCreated observer threw — stopping session");
                session.Stop();
                throw;
            }
        }
    }

    /// <summary>How a <see cref="KlothoSession"/> was created. Carried by
    /// <see cref="IKlothoSessionObserver.OnSessionCreated"/> so the game can branch on role
    /// from a single callback (no per-role subscription).
    ///
    /// <para><c>Local</c> is a single-player session from <see cref="KlothoSessionFlow.StartLocal"/>:
    /// authoritative like a host, but with no socket and no one who can ever join. It is a separate
    /// value rather than <c>Host</c> because the two need different UI and different result handling —
    /// "a room others can join" versus "alone for the whole match". Appended last so existing values
    /// keep their ordinals; a game that branches on <c>Host</c> will not see a local session.</para></summary>
    public enum SessionEntryKind { Host, Guest, Replay, Spectator, Local }
}
