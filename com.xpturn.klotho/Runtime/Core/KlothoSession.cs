using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho session implementation.
    /// A factory and facade responsible for creating/composing engine core objects.
    /// Operates as pure C# with no MonoBehaviour dependency.
    /// </summary>
    public sealed class KlothoSession : IKlothoSession
    {
        public KlothoEngine Engine { get; private set; }
        IKlothoEngine IKlothoSession.Engine => Engine;
        public EcsSimulation Simulation { get; private set; }
        public IKlothoNetworkService NetworkService { get; private set; }
        public CommandFactory CommandFactory { get; private set; }

        public int LocalPlayerId => Engine?.LocalPlayerId ?? -1;
        public KlothoState State => Engine?.State ?? KlothoState.Idle;

        // Unified getter — NetworkService surface for host/guest/SD-client/reconnect, falls back
        // to SpectatorService for spectator path. Pairs with IKlothoSessionObserver.OnPlayerCountChanged
        // so callers can use a single mode-agnostic surface.
        public int PlayerCount =>
            NetworkService?.PlayerCount ?? _spectatorService?.PlayerCount ?? 0;

        // Full roster — mode-agnostic (mirrors PlayerCount): player session reads NetworkService,
        // spectator reads SpectatorService.Players (SpectatorPlayerInfo : IPlayerInfo).
        public System.Collections.Generic.IReadOnlyList<Network.IPlayerInfo> Players =>
            NetworkService?.Players ?? _spectatorService?.Players ?? System.Array.Empty<Network.IPlayerInfo>();

        // Network lifecycle reads — lifted onto the facade so callers depend on IKlothoSession,
        // not the low-level NetworkService surface. Null-safe across the create/teardown window.
        public SessionPhase Phase => NetworkService?.Phase ?? SessionPhase.None;
        public bool AllPlayersReady => NetworkService?.AllPlayersReady ?? false;

        /// <summary>True after Stop() has been called. Exposed for external loop guards.</summary>
        public bool IsStopped => _stopped;

        /// <summary>Optional replay output path. When set, Stop() writes the engine's replay here
        /// (unless saveReplay: false or the session is in replay-playback mode). Configured via
        /// KlothoFlowSetupBuilder.WithReplaySave (preferred) or <see cref="ConfigureReplaySave"/>
        /// (post-create override — e.g. a dynamic per-match path).</summary>
        public string ReplaySavePath { get; private set; }
        /// <summary>Whether the replay save also dumps a JSON sidecar.</summary>
        public bool ReplayDumpJson { get; private set; }

        /// <summary>Set the replay output path so Stop() saves the replay automatically. The framework
        /// owns the save (after Engine.Stop, replay-mode guarded), so the game no longer orchestrates it.</summary>
        public void ConfigureReplaySave(string path, bool dumpJson = false)
        {
            ReplaySavePath = path;
            ReplayDumpJson = dumpJson;
        }

        private IKlothoSessionObserver _lifecycleObserver;
        // Auto-shutdown after match-end grace. 0 = not scheduled, otherwise wall-clock target ms.
        private long _clientShutdownEndMs;
        private bool _stopped;
        private IKLogger _logger;

        // ── Spectator-mode fields ──
        // Concrete type — SpectatorService API (SetLogger / Initialize / SetEngine / Connect /
        // Disconnect / Update) is not all surfaced on ISpectatorService; direct reference keeps wiring simple.
        private SpectatorService _spectatorService;
        private INetworkTransport _spectatorTransport;
        private bool _isSpectatorMode;

        // Bootstrap-in-progress state — all 5 cleared in FinishSpectatorBootstrap (success) or Stop (cancel).
        private SpectatorSessionSetup _pendingSetup;
        private ISimulationConfig _pendingSimConfig;
        private ISessionConfig _pendingSessionConfig;
        private Action<KlothoSession> _spectatorReadyCallback;
        private Action<Exception> _spectatorFailedCallback;

        // Fires immediately after a KlothoSession is fully constructed and lifecycle observer subscribed.
        // Spectator path fires after FinishSpectatorBootstrap completes (Engine / Simulation non-null).
        // This is a notification surface only — no UnityEngine dependency is introduced and subscribers
        // may live in any assembly. Default subscribers are editor diagnostics; do not chain game logic here.
        public static event Action<KlothoSession> OnSessionCreated;

        // Forward Engine / NetworkService state transitions to the lifecycle observer.
        // SubscribeStateForwarders wires these to Engine / NetworkService; _lifecycleObserver is
        // nulled before Engine.Stop in Stop(), so post-teardown transitions never reach the observer.
        private void RaiseStateChanged(KlothoState s)    => _lifecycleObserver?.OnStateChanged(s);
        private void RaisePhaseChanged(SessionPhase p)   => _lifecycleObserver?.OnPhaseChanged(p);
        private void RaisePlayerCountChanged(int n)      => _lifecycleObserver?.OnPlayerCountChanged(n);
        private void RaiseAllPlayersReadyChanged(bool v) => _lifecycleObserver?.OnAllPlayersReadyChanged(v);

        private void SubscribeStateForwarders()
        {
            if (Engine != null)
                Engine.OnStateChanged += RaiseStateChanged;
            if (NetworkService != null)
            {
                NetworkService.OnPhaseChanged += RaisePhaseChanged;
                NetworkService.OnPlayerCountChanged += RaisePlayerCountChanged;
                NetworkService.OnAllPlayersReadyChanged += RaiseAllPlayersReadyChanged;
            }
            // Spectator path: NetworkService is null, so PlayerCountChanged would otherwise never
            // fire. SpectatorService maintains its own _playerIds list (seeded from
            // SpectatorAcceptMessage, mutated by LateJoinNotificationMessage).
            if (_spectatorService != null)
                _spectatorService.OnPlayerCountChanged += RaisePlayerCountChanged;
        }

        private void UnsubscribeStateForwarders()
        {
            if (Engine != null)
                Engine.OnStateChanged -= RaiseStateChanged;
            if (NetworkService != null)
            {
                NetworkService.OnPhaseChanged -= RaisePhaseChanged;
                NetworkService.OnPlayerCountChanged -= RaisePlayerCountChanged;
                NetworkService.OnAllPlayersReadyChanged -= RaiseAllPlayersReadyChanged;
            }
            if (_spectatorService != null)
                _spectatorService.OnPlayerCountChanged -= RaisePlayerCountChanged;
        }

        // Populated in Create (host/guest/replay) and FinishSpectatorBootstrap (spectator) so
        // OnSessionCreated subscribers can resolve callback-side optional interfaces.
        private ISimulationCallbacks _simCallbacks;

        // Escape hatch — not part of the recommended public API. Editor diagnostics use this to
        // discover optional callback-side interfaces after OnSessionCreated.
        public ISimulationCallbacks SimulationCallbacks => _simCallbacks;

        private KlothoSession() { }

        public void Update(float deltaTime)
        {
            if (_stopped) return;

            if (_isSpectatorMode)
            {
                // Always pump spectator transport — required during bootstrap (Engine == null) for
                // SpectatorAcceptMessage / FullStateResponse to arrive, and after bootstrap for
                // confirmed-input streaming.
                _spectatorService?.Update();
                if (Engine != null)
                    Engine.Update(deltaTime);
            }
            else
            {
                Engine.Update(deltaTime);
            }

            // Client shutdown grace check (Update-tick driven — main thread safety guaranteed).
            if (_clientShutdownEndMs > 0)
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs >= _clientShutdownEndMs)
                {
                    _clientShutdownEndMs = 0;
                    _logger?.KInformation($"[KlothoSession] Auto-shutdown grace expired — invoking Stop()");
                    Stop();
                }
            }
        }

        public void InputCommand(ICommand command)
        {
            if (_isSpectatorMode)
                throw new InvalidOperationException("InputCommand is not allowed in spectator mode.");
            Engine.InputCommand(command);
        }

        public void Stop(bool keepReconnectCredentials = false, bool saveReplay = true)
        {
            if (_stopped) return;
            _stopped = true;

            // Cancel pending client shutdown (no-op if not scheduled).
            _clientShutdownEndMs = 0;

            // Capture observer reference before UnsubscribeLifecycleObserver nulls the field —
            // OnSessionStopped fires AFTER framework cleanup so game can safely tear down.
            var obs = _lifecycleObserver;

            // Pre-stop notification — Engine / Simulation are still alive here, so game-side cleanup
            // that needs a live engine (view teardown) runs before Engine.Stop below.
            obs?.OnSessionStopping();

            // Unsubscribe scheduler handler before Engine.Stop to avoid late fire during deinit.
            if (Engine != null)
                Engine.OnMatchEnded -= HandleMatchEndedForShutdown;

            // MUST unsubscribe lifecycle observer before Engine.Stop() — Engine deinit may fire
            // cleanup events (OnMatchReset etc.) that observers should not receive after teardown.
            UnsubscribeLifecycleObserver();

            // Detach state forwarders before Engine.Stop / NetworkService.LeaveRoom — teardown
            // emits Phase transitions whose forwarded fire would arrive after game-side unsubscribe.
            UnsubscribeStateForwarders();

            if (_isSpectatorMode)
            {
                // Spectator-mode teardown: Engine may be null if bootstrap never completed.
                Engine?.Stop();
                if (_spectatorService != null)
                {
                    _spectatorService.OnSimulationConfigReceived -= HandleSpectatorSimConfig;
                    _spectatorService.OnSessionConfigReceived -= HandleSpectatorSessionConfig;
                    _spectatorService.OnSpectatorStopped -= HandleSpectatorStopped;
                    // SpectatorService.Disconnect() handles _transport.Disconnect internally —
                    // calling _spectatorTransport.Disconnect again would double-disconnect the same transport.
                    _spectatorService.Disconnect();
                    _spectatorService = null;
                }
                else
                {
                    // Defensive — BeginSpectatorConnect failed before _spectatorService was wired
                    // (e.g., synchronous transport.Connect failure prior to assignment).
                    _spectatorTransport?.Disconnect();
                }

                // Clear bootstrap-incomplete state explicitly so cancel paths do not hold setup /
                // callback references until session GC.
                _pendingSetup = null;
                _pendingSimConfig = null;
                _pendingSessionConfig = null;
                _spectatorReadyCallback = null;
                _spectatorFailedCallback = null;
            }
            else
            {
                Engine.Stop();
                // Null for replay sessions — and replay always reaches here: playback
                // of a recorded MatchEnd event drives the auto-shutdown scheduler into Stop.
                NetworkService?.LeaveRoom(keepReconnectCredentials);
            }

            // Framework-owned replay save — after Engine.Stop (both branches above), before the terminal
            // callback. Replay-playback mode never overwrites the source. saveReplay: false suppresses it
            // on process-exit teardown. Replaces the per-game capture-then-save orchestration.
            if (saveReplay && ReplaySavePath != null && Engine != null && !Engine.IsReplayMode)
                Engine.SaveReplayToFile(ReplaySavePath, ReplayDumpJson);

            // Notify game for terminal teardown (session reference null-out, UI reset). Transport
            // disconnect is not game-side here — the driver owns the main transport and disconnects it
            // on process exit, keeping it across sessions for reuse. Game-side re-entry into Stop() is
            // guarded by the _stopped flag above — idempotent.
            obs?.OnSessionStopped();
        }

        // ── Client shutdown grace scheduler ──

        private void HandleMatchEndedForShutdown(int tick, IMatchEndEvent endEvt)
        {
            if (_stopped) return;
            if (Engine.IsReplayMode) return;
            if (_clientShutdownEndMs > 0) return;

            int graceMs = Engine.SessionConfig.ClientShutdownGraceMs;
            if (graceMs <= 0)
            {
                // Defer Stop to next Update tick — avoid re-entrancy during OnMatchEnded dispatch.
                _clientShutdownEndMs = 1;
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                _logger?.KDebug($"[KlothoSession] Auto-shutdown scheduled in 0ms (deferred to next Update tick)");
#endif
                return;
            }

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _clientShutdownEndMs = nowMs + graceMs;
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            _logger?.KDebug($"[KlothoSession] Auto-shutdown scheduled in {graceMs}ms");
#endif
        }

        /// <summary>
        /// Request a deferred stop after a non-Playing transport drop. Reuses the client-shutdown grace
        /// scheduler so the actual Stop() runs at the end of the current/next Update (after Engine.Update
        /// returns) rather than re-entrantly from inside a transport event dispatch.
        /// </summary>
        public void RequestClientShutdown()
        {
            if (_stopped || Engine.IsReplayMode || _clientShutdownEndMs > 0) return;
            _clientShutdownEndMs = 1;
        }

        // ── Lifecycle observer wiring ──

        // internal — exposed for unit tests via InternalsVisibleTo on xpTURN.Klotho.Tests asmdef.
        internal void SubscribeLifecycleObserver(IKlothoSessionObserver obs)
        {
            if (obs == null) return;
            _lifecycleObserver = obs;

            // Spectator mode has no NetworkService — guard so spectator path can reuse this wiring.
            if (NetworkService != null)
            {
                NetworkService.OnPlayerDisconnected += obs.OnPlayerDisconnected;
                NetworkService.OnPlayerReconnected += obs.OnPlayerReconnected;
                NetworkService.OnReconnecting += obs.OnReconnecting;
                NetworkService.OnReconnectFailed += obs.OnReconnectFailed;
                NetworkService.OnReconnected += obs.OnReconnected;
            }

            if (Engine != null)
            {
                Engine.OnCatchupComplete += obs.OnCatchupComplete;
                Engine.OnResyncCompleted += obs.OnResyncCompleted;
                Engine.OnGameStart += obs.OnGameStart;
                Engine.OnMatchAborted += obs.OnMatchAborted;
                Engine.OnMatchEnded += obs.OnMatchEnded;
                Engine.OnMatchReset += obs.OnMatchReset;
            }
        }

        internal void UnsubscribeLifecycleObserver()
        {
            if (_lifecycleObserver == null) return;
            var obs = _lifecycleObserver;
            _lifecycleObserver = null;

            if (NetworkService != null)
            {
                NetworkService.OnPlayerDisconnected -= obs.OnPlayerDisconnected;
                NetworkService.OnPlayerReconnected -= obs.OnPlayerReconnected;
                NetworkService.OnReconnecting -= obs.OnReconnecting;
                NetworkService.OnReconnectFailed -= obs.OnReconnectFailed;
                NetworkService.OnReconnected -= obs.OnReconnected;
            }
            if (Engine != null)
            {
                Engine.OnCatchupComplete -= obs.OnCatchupComplete;
                Engine.OnResyncCompleted -= obs.OnResyncCompleted;
                Engine.OnGameStart -= obs.OnGameStart;
                Engine.OnMatchAborted -= obs.OnMatchAborted;
                Engine.OnMatchEnded -= obs.OnMatchEnded;
                Engine.OnMatchReset -= obs.OnMatchReset;
            }
        }

        // ── Factory ──

        /// <summary>
        /// Create a session (new Config-tier API).
        /// </summary>
        public static KlothoSession Create(KlothoSessionSetup setup)
        {
            bool isGuest = setup.Connection != null;
            var simConfig = isGuest
                ? setup.Connection.SimulationConfig
                : setup.SimulationConfig;
            var transport = isGuest
                ? setup.Connection.Transport
                : setup.Transport;

            // 1. Create EcsSimulation
            var simulation = new EcsSimulation(
                simConfig.MaxEntities,
                simConfig.GetSnapshotCapacity(), // ring capacity = MaxRollbackTicks + 2
                simConfig.TickIntervalMs,
                setup.Logger,
                assetRegistry: setup.AssetRegistry,
                maxCountOverrides: simConfig.ComponentMaxCountOverrides,
                prunedComponentTypeIds: simConfig.PrunedComponentTypeIds);

            // 2. Register systems via callback
            setup.SimulationCallbacks?.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            // 3. Create CommandFactory
            var commandFactory = new CommandFactory();

            // 4. Create SessionConfig
            // Guest: RandomSeed stays 0 — nothing here authors one, and the effective seed arrives on
            //        the service (GameStartMessage), not in this config
            // Host: the authored value is copied verbatim; 0 is resolved at match start by StartGame,
            //       so this field stays the authored request for the life of the session
            // Guest Late Join: overwritten with LateJoinAcceptMessage fields (replaces the GameStartMessage path)
            // Guest cold-start Reconnect: overwritten with ReconnectAcceptMessage fields
            JoinKind joinKind = isGuest ? setup.Connection.Kind : JoinKind.Normal;
            bool isLateJoin = (joinKind == JoinKind.LateJoin);
            bool isReconnect = (joinKind == JoinKind.Reconnect);
            SessionConfig sessionConfig;
            if (isLateJoin)
            {
                var accept = setup.Connection.LateJoinPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.KWarning($"[KlothoSession] MinPlayers clamped (LateJoin): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = accept.MaxSpectators,
                    AllowLateJoin = accept.AllowLateJoin,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelaySafety = accept.LateJoinDelaySafety,
                    RttSanityMaxMs = accept.RttSanityMaxMs,
                    MinStallAbortTicks = accept.MinStallAbortTicks,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    AbortGraceMs = accept.AbortGraceMs,
                    EndGracePolicy = (EndGracePolicy)accept.EndGracePolicy,
                    EndGraceMs = accept.EndGraceMs,
                    ClientShutdownGraceMs = accept.ClientShutdownGraceMs,
                };
            }
            else if (isReconnect)
            {
                var accept = setup.Connection.ReconnectPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.KWarning($"[KlothoSession] MinPlayers clamped (Reconnect): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = accept.MaxSpectators,
                    AllowLateJoin = accept.AllowLateJoin,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelaySafety = accept.LateJoinDelaySafety,
                    RttSanityMaxMs = accept.RttSanityMaxMs,
                    MinStallAbortTicks = accept.MinStallAbortTicks,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    AbortGraceMs = accept.AbortGraceMs,
                    EndGracePolicy = (EndGracePolicy)accept.EndGracePolicy,
                    EndGraceMs = accept.EndGraceMs,
                    ClientShutdownGraceMs = accept.ClientShutdownGraceMs,
                };
            }
            else
            {
                var src = setup.SessionConfig ?? new SessionConfig();
                int clampedMinPlayers = System.Math.Clamp(src.MinPlayers, 1, src.MaxPlayers);
                if (clampedMinPlayers != src.MinPlayers)
                {
                    setup.Logger?.KWarning($"[KlothoSession] MinPlayers clamped: {src.MinPlayers} -> {clampedMinPlayers} (range: 1..{src.MaxPlayers})");
                }
                // Server-internal (host-only, non-wire) async-validation window. Floor keeps it above the
                // drain's strict '>' compare (0/negative would reject every pending validation next tick);
                // ceiling keeps it below the client connect timeout so the server rejects with a reason first.
                const int ValidationFloorMs = 1000;
                int validationCeilMs = KlothoConnection.DEFAULT_CONNECT_TIMEOUT_MS - 1000;
                int clampedValidationTimeout = System.Math.Clamp(src.ValidationTimeoutMs, ValidationFloorMs, validationCeilMs);
                if (clampedValidationTimeout != src.ValidationTimeoutMs)
                {
                    setup.Logger?.KWarning($"[KlothoSession] ValidationTimeoutMs clamped: {src.ValidationTimeoutMs} -> {clampedValidationTimeout} (range: {ValidationFloorMs}..{validationCeilMs})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = isGuest ? 0 : src.RandomSeed,
                    MaxPlayers = src.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = src.MaxSpectators,
                    AllowLateJoin = src.AllowLateJoin,
                    LateJoinDelayTicks = src.LateJoinDelayTicks,
                    ReconnectTimeoutMs = src.ReconnectTimeoutMs,
                    ValidationTimeoutMs = clampedValidationTimeout,
                    ReconnectMaxRetries = src.ReconnectMaxRetries,
                    LateJoinDelaySafety = src.LateJoinDelaySafety,
                    RttSanityMaxMs = src.RttSanityMaxMs,
                    MinStallAbortTicks = src.MinStallAbortTicks,
                    CountdownDurationMs = src.CountdownDurationMs,
                    AbortGraceMs = src.AbortGraceMs,
                    EndGracePolicy = src.EndGracePolicy,
                    EndGraceMs = src.EndGraceMs,
                    ClientShutdownGraceMs = src.ClientShutdownGraceMs,
                };
            }

            // 5. Create + initialize NetworkService — guest (Connection) uses the skip-handshake path.
            //    Replay sessions create none: the replay metadata's Mode used to route
            //    this block down the host path, subscribing a ghost service to the live main
            //    transport (stale-pong RTT pollution, epoch SharedClock logs, and a live-message
            //    surface into the replay engine). NetworkService == null follows the spectator
            //    precedent — observers/forwarders/properties are already null-guarded.
            //    NOTE: the service is constructed here but Initialize/InitializeFromConnection is deferred to
            //    after the identity/entitlement wiring — a guest's InitializeFromConnection rebuilds the
            //    initial roster and re-verifies each propagated ticket, which must see the entitlement gate
            //    already enabled, else the initial roster's per-peer entitlement is skipped (gate off) and the
            //    P2P tick-0 loadout seed diverges from the host.
            IKlothoNetworkService networkService = null;
            if (!setup.IsReplay)
            {
                if (simConfig.Mode == NetworkMode.ServerDriven)
                    networkService = new ServerDrivenClientService();
                else
                    networkService = new KlothoNetworkService();
            }

            // 5.1 Reconnect credentials wire — optional. Both KlothoNetworkService and ServerDrivenClientService
            //     own the SetReconnectCredentialsStore API; route via cast so the game side does not have to
            //     know the concrete network service type.
            if (setup.CredentialsStore != null)
            {
                if (networkService is KlothoNetworkService p2pCreds)
                    p2pCreds.SetReconnectCredentialsStore(setup.CredentialsStore, setup.AppVersion, setup.DeviceIdProvider);
                else if (networkService is ServerDrivenClientService sdCreds)
                    sdCreds.SetReconnectCredentialsStore(setup.CredentialsStore, setup.AppVersion, setup.DeviceIdProvider);
            }

            // 5.2 Authority-side identity validator wire — P2P host only. The SD dedicated server
            //     (ServerNetworkService) is constructed in RoomManager, not here, and is injected there.
            //     A P2P guest's service never reaches CompletePeerSync, so setting it is harmless.
            if (setup.IdentityValidator != null && networkService is KlothoNetworkService p2pVal)
            {
                p2pVal.SetIdentityValidator(setup.IdentityValidator);
                p2pVal.SetLocalIdentityTicket(setup.LocalIdentityTicket); // host validates its own ticket at self-add
                // The re-verifier auto-derives from the validator, since the reference P2P validator also
                // implements IPropagatedTicketVerifier. The entitlement guard gates original-ticket
                // propagation and per-peer re-verification: when it is set, enable propagation
                // (SetOriginalTicketPropagation is fail-closed and refuses with a log if no re-verifier is
                // present). With no guard, propagation stays off and behaviour is unchanged.
                if (setup.IdentityValidator is Network.IPropagatedTicketVerifier verifier)
                    p2pVal.SetPropagatedTicketVerifier(verifier);
                if (setup.PlayerConfigEntitlementGuard != null)
                {
                    p2pVal.SetPlayerConfigEntitlementGuard(setup.PlayerConfigEntitlementGuard);
                    p2pVal.SetOriginalTicketPropagation(true);
                }
            }

            // Escape-hatch safety net: the builder's Build() throws on guard-without-validator, but the
            // object-initializer construction path (a documented escape hatch) bypasses Build(). A guard set
            // without a validator means the block above never wired it, so entitlement enforcement is silently
            // OFF. This is an independent check, NOT an else on the block above: the block's compound condition
            // is also false for SD-client / replay services, where this log would be spurious. Mirrors the
            // fail-closed + KError idiom of SetOriginalTicketPropagation.
            if (KlothoFlowSetupBuilder.GuardRequiresValidatorViolated(setup.PlayerConfigEntitlementGuard, setup.IdentityValidator))
                setup.Logger?.KError(
                    $"[KlothoSession] PlayerConfigEntitlementGuard set without an IdentityValidator — entitlement enforcement is OFF (the guard needs the validator as the P2P re-verifier).");

            // Initialize the service now that identity/entitlement wiring is in place. Deferred from
            // construction so a guest's InitializeFromConnection rebuilds its initial roster with the
            // entitlement gate already enabled — each propagated ticket is re-verified and its entitlement
            // extracted, so this peer seeds the same tick-0 loadout the host does. (The host uses
            // Initialize; its own roster entry is added later in CreateRoom, already after this wiring.)
            if (!setup.IsReplay)
            {
                if (networkService is ServerDrivenClientService sdInit)
                {
                    if (isGuest) sdInit.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger, setup.RoomId);
                    else         sdInit.Initialize(transport, commandFactory, setup.Logger);
                }
                else if (networkService is KlothoNetworkService p2pInit)
                {
                    if (isGuest) p2pInit.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger);
                    else         p2pInit.Initialize(transport, commandFactory, setup.Logger);
                }
            }

            // 5.5 Late Join / cold-start Reconnect seed — restore _players / _sessionMagic / _randomSeed.
            //     Must be done at this point so the engine.Initialize _activePlayerIds auto-copy loop ([L278-280])
            //     can populate correctly.
            if (isLateJoin)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
            }

            // 6. Create Engine: inject both SimulationConfig and SessionConfig
            var engine = new KlothoEngine(simConfig, sessionConfig);
            engine.AllowLayoutMismatch = setup.AllowLayoutMismatch;   // per-peer, not wire-borne (see setup doc)
            engine.EnableReplayRecording = setup.EnableReplayRecording; // same: local decision, never on the wire
            if (setup.IsReplay)
                engine.Initialize(simulation, setup.Logger, setup.SimulationCallbacks, setup.ViewCallbacks);
            else
                engine.Initialize(simulation, networkService, setup.Logger,
                    setup.SimulationCallbacks, setup.ViewCallbacks);
            engine.SetCommandFactory(commandFactory);
            if (networkService is KlothoNetworkService p2pNs)
                p2pNs.SubscribeEngine(engine);
            else if (networkService is ServerDrivenClientService sdNs)
                sdNs.SubscribeEngine(engine);

            // 7.5 Late Join injection: restore FullState + start Catchup + seed existing players' PlayerConfig.
            //     Since there is no HandleGameStart path, seed manually at this point.
            //     The extra-delay value from the accept message is applied by SDClientService.SubscribeEngine
            //     (drains a pending value buffered when the handshake handler fired before the engine existed).
            if (isLateJoin)
            {
                engine.SeedLateJoinFullState(setup.Connection.LateJoinPayload);
                SeedLateJoinPlayerConfigs(engine, setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                // cold-start Reconnect: FullState restore + Catchup. PlayerConfig is re-broadcast by the host
                // upon reconnect (the existing runtime path), so no PlayerConfig seed array on this message.
                engine.SeedReconnectFullState(setup.Connection.ReconnectPayload);
            }

            var session = new KlothoSession
            {
                Engine = engine,
                Simulation = simulation,
                NetworkService = networkService,
                CommandFactory = commandFactory,
                _logger = setup.Logger,
                _simCallbacks = setup.SimulationCallbacks,
            };

            session.SubscribeLifecycleObserver(setup.LifecycleObserver);
            // Stamped onto host / guest setups by KlothoSessionFlow (Replay leaves it null). The game
            // may still override via ConfigureReplaySave after this returns (last-write-wins).
            if (setup.ReplaySavePath != null)
                session.ConfigureReplaySave(setup.ReplaySavePath, setup.ReplayDumpJson);
            session.SubscribeStateForwarders();
            session.Engine.OnMatchEnded += session.HandleMatchEndedForShutdown;

            try { OnSessionCreated?.Invoke(session); }
            catch (Exception e) { setup.Logger?.KError(e, $"[KlothoSession] OnSessionCreated subscriber threw"); }

            return session;
        }

        /// <summary>
        /// Late Join path PlayerConfig injection.
        /// Sequentially deserializes LateJoinAcceptMessage.PlayerConfigData + PlayerConfigLengths and
        /// calls engine.HandlePlayerConfigReceived(playerId, configMsg). Same pattern as the regular runtime path.
        /// Since MessageSerializer._messageCache reuses singletons by type, HandlePlayerConfigReceived must be invoked
        /// immediately inside the loop (the engine copies/extracts into its internal store) — do not buffer into an intermediate array.
        /// </summary>
        private static void SeedLateJoinPlayerConfigs(KlothoEngine engine, LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            if (msg.PlayerConfigData == null || msg.PlayerConfigData.Length == 0) return;
            if (msg.PlayerConfigLengths == null || msg.PlayerConfigLengths.Count == 0) return;

            var serializer = new MessageSerializer();
            int offset = 0;
            int count = System.Math.Min(msg.PlayerConfigLengths.Count, msg.Roster.Count);
            for (int i = 0; i < count; i++)
            {
                int len = msg.PlayerConfigLengths[i];
                if (len <= 0) continue;

                var configMsg = serializer.Deserialize(msg.PlayerConfigData, len, offset) as PlayerConfigBase;
                if (configMsg != null)
                    engine.HandlePlayerConfigReceived(msg.Roster[i].PlayerId, configMsg);
                offset += len;
            }
        }

        // ── Convenience methods ──

        public void HostGame(string roomName, int maxPlayers)
        {
            NetworkService.CreateRoom(roomName, maxPlayers);
        }

        public void JoinGame(string roomName)
        {
            NetworkService.JoinRoom(roomName);
        }

        public void LeaveRoom()
        {
            NetworkService.LeaveRoom();
        }

        /// <summary>
        /// Sends the local player's PlayerConfig to the host.
        /// Upon receipt, the host broadcasts it to all peers.
        /// </summary>
        public void SendPlayerConfig(PlayerConfigBase playerConfig)
        {
            NetworkService.SendPlayerConfig(LocalPlayerId, playerConfig);
        }

        public void SetReady(bool ready)
        {
            NetworkService.SetReady(ready);
        }

        // ── Spectator factory ──

        /// <summary>
        /// Create a spectator session. Spectator setup is deferred — completes once both
        /// SimulationConfig and SessionConfig arrive from SpectatorAcceptMessage. Engine,
        /// Simulation, and SpectatorService wiring all performed internally. Reports completion
        /// via <paramref name="onReady"/> / <paramref name="onFailed"/>.
        ///
        /// Caller must invoke <see cref="Update"/> every frame on the returned session — it polls
        /// the spectator transport while configs are pending and drives Engine.Update after bootstrap.
        /// </summary>
        public static KlothoSession CreateSpectator(
            SpectatorSessionSetup setup,
            Action<KlothoSession> onReady = null,
            Action<Exception> onFailed = null)
        {
            var session = new KlothoSession
            {
                _logger = setup.Logger,
                _isSpectatorMode = true,
                _spectatorTransport = setup.Transport,
                _spectatorReadyCallback = onReady,
                _spectatorFailedCallback = onFailed,
                _pendingSetup = setup,
            };
            session.BeginSpectatorConnect();
            return session;
        }

        private void BeginSpectatorConnect()
        {
            var commandFactory = new CommandFactory();
            CommandFactory = commandFactory;

            var spectatorService = new SpectatorService();
            spectatorService.SetLogger(_logger);
            spectatorService.Initialize(_spectatorTransport, commandFactory, null, _logger);

            spectatorService.OnSimulationConfigReceived += HandleSpectatorSimConfig;
            spectatorService.OnSessionConfigReceived += HandleSpectatorSessionConfig;
            spectatorService.OnSpectatorStopped += HandleSpectatorStopped;

            _spectatorService = spectatorService;
            spectatorService.Connect(_pendingSetup.HostAddress, _pendingSetup.Port, _pendingSetup.RoomId);
        }

        private void HandleSpectatorSimConfig(ISimulationConfig cfg)
        {
            _pendingSimConfig = cfg;
            TryFinishSpectatorBootstrap();
        }

        private void HandleSpectatorSessionConfig(ISessionConfig cfg)
        {
            _pendingSessionConfig = cfg;
            TryFinishSpectatorBootstrap();
        }

        private void TryFinishSpectatorBootstrap()
        {
            if (_pendingSimConfig == null || _pendingSessionConfig == null) return;
            if (Engine != null) return;   // duplicate-guard
            FinishSpectatorBootstrap(_pendingSimConfig, _pendingSessionConfig);
        }

        private void FinishSpectatorBootstrap(ISimulationConfig simCfg, ISessionConfig sessionCfg)
        {
            // Build Sim/View callbacks against server-authoritative config.
            var callbacks = _pendingSetup.CallbacksFactory(simCfg, sessionCfg);
            _simCallbacks = callbacks.Simulation;

            var simulation = new EcsSimulation(
                simCfg.MaxEntities, simCfg.GetSnapshotCapacity(), simCfg.TickIntervalMs,
                _logger, assetRegistry: _pendingSetup.AssetRegistry,
                maxCountOverrides: simCfg.ComponentMaxCountOverrides,
                prunedComponentTypeIds: simCfg.PrunedComponentTypeIds);
            callbacks.Simulation?.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            var engine = new KlothoEngine(simCfg, sessionCfg);
            engine.AllowLayoutMismatch = _pendingSetup.AllowLayoutMismatch;
            engine.Initialize(simulation, _logger);
            engine.SetCommandFactory(CommandFactory);

            Engine = engine;
            Simulation = simulation;

            _spectatorService.SetEngine(engine);

            _spectatorService.OnSpectatorStarted     += info => engine.StartSpectator(info);
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => engine.ReceiveConfirmedCommand(cmd);
            _spectatorService.OnTickConfirmed        += tick => engine.ConfirmSpectatorTick(tick);
            _spectatorService.OnFullStateReceived    += (tick, stateData, _, _) =>
            {
                simulation.RestoreFromFullState(stateData);
                engine.ResetToTick(tick);
            };

            // Lifecycle observer subset — SubscribeLifecycleObserver guards on NetworkService != null,
            // so spectator path naturally subscribes only Engine-side events.
            SubscribeLifecycleObserver(_pendingSetup.LifecycleObserver);

            // State forwarders — NetworkService is null in spectator, so SubscribeStateForwarders only
            // wires Engine.OnStateChanged. UnsubscribeStateForwarders in Stop() mirrors this.
            SubscribeStateForwarders();

            // Reuse client-shutdown scheduler — fires on Engine.OnMatchEnded across all modes.
            engine.OnMatchEnded += HandleMatchEndedForShutdown;

            var readyCallback = _spectatorReadyCallback;
            _pendingSetup = null;
            _pendingSimConfig = null;
            _pendingSessionConfig = null;
            _spectatorReadyCallback = null;
            _spectatorFailedCallback = null;

            try { OnSessionCreated?.Invoke(this); }
            catch (Exception e) { _logger?.KError(e, $"[KlothoSession] OnSessionCreated subscriber threw"); }

            readyCallback?.Invoke(this);
        }

        private void HandleSpectatorStopped(string reason)
        {
            if (Engine == null)
            {
                // Pre-bootstrap failure — surface to the CreateSpectator caller.
                var failedCallback = _spectatorFailedCallback;
                _spectatorFailedCallback = null;
                _spectatorReadyCallback = null;
                failedCallback?.Invoke(new Exception($"Spectator stopped before bootstrap: {reason}"));
            }
            else if (!_stopped)
            {
                // Post-bootstrap transport drop — drive framework cleanup through Stop().
                // Stop() invokes the lifecycle observer's OnSessionStopped, letting the game tear
                // down UI / transport references without per-game disconnect detection. Stop() is
                // idempotent (_stopped guard), so re-entry from game-side StopGame() is safe.
                _logger?.KWarning($"[KlothoSession] Spectator transport stopped after bootstrap: {reason} — invoking Stop()");
                Stop();
            }
        }
    }
}
