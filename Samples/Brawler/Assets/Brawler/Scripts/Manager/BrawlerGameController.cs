using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using System.Threading;

using UnityEngine;
using UnityEngine.SceneManagement;

using Cysharp.Threading.Tasks;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Unity;
using xpTURN.Klotho.Samples.Identity.Sd; // SD dev lobby: SdDevIdentity, SdLobbyIssueProvider, LiteNetLibLobbyIssueClient, IssueResult

using xpTURN.Klotho.Diagnostics;

namespace Brawler
{
    [Serializable]
    public class BrawlerSettings
    {
        [field: Header("ServerSettings")]
        [SerializeField] public string _hostAddress = "localhost";
        [SerializeField] public int _port = 777;

        [field: Header("ServerDriven")]
        [SerializeField] public int _roomId = 0;

        [field: Header("P2P")]
        [SerializeField] public bool _isHost = true;
        [SerializeField] public int _botCount = 0;

        [field: Header("PlayerSettings")]
        [SerializeField] public int _characterClass = 0; // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
    }

    /// <summary>
    /// One multi-stage entry: the baked deterministic geometry (colliders + navmesh, shared by server and
    /// client) plus the additive view scene loaded on the client. The stage is selected by the received
    /// SimulationConfig.StageId (server-stamped for SD rooms, host-authored for P2P).
    /// </summary>
    [Serializable]
    public class StageResource
    {
        [SerializeField] public int _stageId;
        [SerializeField] public TextAsset _colliders;
        [SerializeField] public TextAsset _navMesh;
        [SerializeField] public string _sceneName; // additive view scene (Build Settings name); empty = no view
    }

    /// <summary>
    /// Dev lobby identity settings. The single <see cref="_lobbyEnabled"/> toggle drives On/Off at RUNTIME
    /// (no compile define); the path (P2P in-process stub vs SD lobby-issued ticket) follows the
    /// SimulationConfig Mode. Off → no lobby hooks → current behaviour (no-regression). ⚠ The sample dev
    /// keys (BrawlerDevIdentity) compile into every build — a real game replaces them with a real lobby.
    /// </summary>
    [Serializable]
    public class BrawlerLobbySettings
    {
        [Tooltip("Enable dev lobby identity at runtime. For SD, also start the dedicated server with --lobby host:port.")]
        [SerializeField] public bool _lobbyEnabled = false;
        [SerializeField] public string _account = ""; // blank → a dev-NNNN id is generated per process

        [field: Header("ServerDriven lobby")]
        [SerializeField] public string _matchId = SdDevIdentity.DevMatchId; // SD only; P2P session id is a BrawlerDevIdentity constant
        [SerializeField] public string _lobbyAddress = "localhost";         // SD only — DevLobbyServer Issue endpoint
        [SerializeField] public int _lobbyPort = 9999;
    }

    /// <summary>
    /// Brawler sample game controller.
    ///
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class BrawlerGameController : MonoBehaviour, IKlothoSessionObserver
    {
        const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

        [field: Header("Debug")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [field: Header("Settings")]
        [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
        [SerializeField] private BrawlerLobbySettings _brawlerLobby = new BrawlerLobbySettings();
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;

        [field: Header("Scene References")]
        [SerializeField] private GameMenu _gameMenu;
        [SerializeField] private BrawlerViewSync _viewSync;

        // EVU reference. If the prefab has an EntityView, EVU automatically spawns it.
        // If null in the Inspector, the EVU hook is skipped.
        [SerializeField] private EntityViewUpdater _entityViewUpdater;

        // Drives KlothoSession.Update / Stop teardown through Unity Update lifecycle.
        [SerializeField] private KlothoSessionDriver _sessionDriver;

        // Diagnostic F12 chain-stall hotkey. Surface kept compile-on regardless of KLOTHO_FAULT_INJECTION
        // to keep prefab serialization stable; Attach is the only call gated by the define.
        [SerializeField] private xpTURN.Klotho.Diagnostics.FaultInjectionHotkeyDriver _faultInjectionHotkey;

        [field: Header("Static Colliders")]
        [SerializeField] private TextAsset _staticCollidersAsset;

        [field: Header("NavMesh")]
        [SerializeField] private TextAsset _navMeshAsset;

        // Multi-stage: stageId → baked geometry + additive view scene. When populated the received
        // StageId selects the entry; when empty the single _staticCollidersAsset/_navMeshAsset above
        // are used (default/back-compat, = the default stage).
        [field: Header("Stages (stageId → geometry + additive view scene)")]
        [SerializeField] private StageResource[] _stageResources;

        [field: Header("DataAssets")]
        [SerializeField] private TextAsset _dataAsset;

        private IKLogger _logger;
        List<FPStaticCollider> _staticColliders;
        FPNavMesh _navMesh;
        List<IDataAsset> _dataAssets;

        // Stage resolved from the received SimulationConfig.StageId at BuildCallbacks. The additive view
        // scene (visual only) is driven toward a desired target and reconciled through SceneManager async
        // ops so a stop/start straddling an in-flight load/unload can't leak a scene or drop the view.
        private int _resolvedStageId;
        private string _desiredStageScene;   // scene we want loaded (null = none)
        private string _currentStageScene;   // scene actually loaded (set on load-complete, cleared on unload-complete)
        private bool _stageSceneBusy;         // an async load/unload is in flight
        private IDataAssetRegistry _assetRegistry;

        private IKlothoSession _session;
        private KlothoSessionFlow _flow;
        private LiteNetLibTransport _transport;

#if KLOTHO_FAULT_INJECTION
        // Per-process-unique device id for fault-injection smoke runs: stable across this process's
        // join+reconnect (so the host's credential matching still recognizes it), but distinct
        // between co-located instances (so they don't collide on the shared machine id).
        private sealed class FaultInjectionDeviceIdProvider : IDeviceIdProvider
        {
            private static readonly string _id =
                $"{SystemInfo.deviceUniqueIdentifier}-fi-{Guid.NewGuid():N}";
            public string GetDeviceId() => _id;
        }
#endif

        private Camera _mainCamera;
        private IReconnectCredentialsStore _credentialsStore;
        private IKlothoModeStrategy _modeStrategy;

        // Effective local role, re-derived from the current host preference on every read so a
        // pre-connect Host/Guest toggle is reflected immediately. Valid once _modeStrategy is resolved.
        private KlothoRole Role => _modeStrategy.ResolveRole(_brawlerSettings._isHost);

        private BrawlerInputCapture _input;
        private BrawlerSimulationCallbacks _simCallbacks;
        private BrawlerViewCallbacks _viewCallbacks;

        // Local player display name (random nickname). Promoted to a field so the SD lobby Issue request
        // (JoinGameAsync) can reuse the same name the builder's WithDisplayName carries.
        private string _displayName;
        // SD dev lobby: mutable carry-only provider. The ticket is fetched on Join (TryFetchLobbyAsync) and
        // stored here before connecting; GetTicket() returns it synchronously during the handshake. Created
        // only when SD lobby is enabled at runtime (see builder block); null otherwise.
        private SdLobbyIssueProvider _identityProvider;

        private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

        private void CreateLogger()
        {
            _logger = KlothoLogger.CreateDefault(
                level: _logLevel,
                filePrefix: "Client",
                categoryName: "Client",
                timestampFormat: "HH:mm:ss.fff"); // date dropped (it's in the filename); hour kept

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            CommandPool.SetDiagnosticLogger(_logger);
            EventPool.SetDiagnosticLogger(_logger);
#endif

            _logger?.KInformation($"Brawler logging started : LogLevel={_logLevel}");
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            CreateLogger();

            // Driver per-frame hook wired here so it is live even when Start() returns early via
            // cold-start reconnect. Session lifecycle (create / state / stopping / stopped) is observed
            // through IKlothoSessionObserver, not the driver Stopping event. Idle transport pumping and
            // idle-disconnect routing are owned by the driver (bound via BindTransport in Start).
            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
        }

        private void OnPreSessionUpdate(KlothoSession session, float dt)
        {
            if (session.State == KlothoState.Running)
            {
                _input.CaptureInput();
                _input.AimDirection = GetFacingAimDirection();
            }
        }

        // Pre-stop hook — fires inside session.Stop() while Engine is still alive (replaces the
        // KlothoSessionDriver.Stopping subscription). State-event unsubscription is no longer needed:
        // the framework manages observer lifetime, nulling it before Engine.Stop.
        void IKlothoSessionObserver.OnSessionStopping()
        {
            // Diagnostic hotkey detach first — null-out only, no exception surface.
            // Kept synchronous so it always runs whether or not later cleanup throws.
            _faultInjectionHotkey?.Detach();

            // Cleanup that requires Engine to be alive — fires before Engine.Stop.
            _viewSync.OnLocalCharacterSpawned   -= OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned -= OnLocalCharacterDespawned;
            _viewSync.Cleanup();
            _entityViewUpdater?.Cleanup();
            // Replay is saved by the framework inside session.Stop (after Engine.Stop) via
            // ConfigureReplaySave — no longer orchestrated here.
        }

        private void Start()
        {
            List<string> nicknames = new List<string>
            {
                "ShadowFrost", "NyxVortex", "PixelRogue", "CrimsonWraith", "VoidSpecter", "EchoBlade",
                "LunarFang", "ZephyrCrash", "EmberQuill", "FrostByte", "StormGale", "AshenVeil",
                "NeonDrift", "IronClad", "DuskHollow", "GildedHex", "CobaltSurge", "RavenPulse",
                "MysticCoil", "BlazeWarden", "SilentDune", "ArcReactor", "GloomFang", "ViperStrike",
                "OnyxBloom", "TitanRoar", "WispCharm", "RogueSpark", "ChaosKite", "VividScar",
                "JadeRift", "HollowMoon", "QuartzEdge", "ThornLace", "PrismShade", "AzureFlux"
            };
            _displayName = nicknames[UnityEngine.Random.Range(0, nicknames.Count)];

            // Pre-load data
            _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
            _navMesh = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
            _dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

            IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
            registryBuilder.RegisterRange(_dataAssets);
            _assetRegistry = registryBuilder.Build();

            _mainCamera = Camera.main;

            _credentialsStore = new PlayerPrefsReconnectCredentialsStore();

            var logLevels = new LiteNetLib.NetLogLevel[] { LiteNetLib.NetLogLevel.Warning, LiteNetLib.NetLogLevel.Error };
            _transport = new LiteNetLibTransport(_logger, logLevels, connectionKey: KLOTHO_CONNECTION_KEY);

            _input = new BrawlerInputCapture();
            _input.Enable();

            _modeStrategy = KlothoModeStrategy.Resolve(_simulationConfig);
            _brawlerSettings._roomId = _modeStrategy.NormalizeRoomId(_brawlerSettings._roomId);

            var builder = new KlothoFlowSetupBuilder(BuildCallbacks)
                .WithLogger(_logger)
                .WithTransport(_transport)
                .WithAssetRegistry(_assetRegistry)
                .WithLifecycleObserver(this)
                .WithReplaySave(_replayPath, dumpJson: true)
                .WithUnityDefaults()
#if KLOTHO_FAULT_INJECTION
                // Co-located fault-injection clients share SystemInfo.deviceUniqueIdentifier (same
                // machine), which collides on the host's reconnect credential matching → reconnect
                // rejected. Override with a per-process-unique id (stable across this process's
                // join+reconnect, distinct between instances) so the reconnect smoke is meaningful.
                .WithHandshake(Application.version, new FaultInjectionDeviceIdProvider())
#endif
                .WithReconnect(_credentialsStore)
                .WithDisplayName(_displayName)
                .WithAutoPlayerConfig(() => new BrawlerPlayerConfig { SelectedCharacterClass = _brawlerSettings._characterClass })
                .WithSpectator(() => new LiteNetLibTransport(_logger, connectionKey: KLOTHO_CONNECTION_KEY));

            // Dev lobby identity wiring — opt-in via _brawlerLobby._lobbyEnabled, mode-aware (the single
            // RUNTIME toggle picks the path from the resolved Mode; no compile define). Off → no hooks →
            // current behaviour (no-regression).
            if (_brawlerLobby._lobbyEnabled)
            {
                if (_modeStrategy.Mode == NetworkMode.P2P)
                {
                    // P2P: in-process signed-ticket stub. Provider (carry) + validator (host offline verify)
                    // are both set on the single shared builder; a guest never consults the validator.
                    builder = builder
                        .WithLobbyIdentity(BrawlerDevIdentity.CreateProvider(_brawlerLobby._account, _displayName, _logger))
                        .WithIdentityValidator(BrawlerDevIdentity.CreateValidator())
                        // The entitlement guard makes every peer re-verify the propagated lobby-signed
                        // entitlement and clamp the selected character to the owned set, deterministically on
                        // each peer. Setting it also enables original-ticket propagation. This is the same
                        // guard the dedicated server uses — a mode-agnostic pure function.
                        .WithPlayerConfigEntitlementGuard(new BrawlerPlayerConfigEntitlementGuard());
                }
                else if (_modeStrategy.Mode == NetworkMode.ServerDriven)
                {
                    // SD: client carries a lobby-issued ticket fetched on Join (TryFetchLobbyAsync); the
                    // dedicated server validates it via redeem (enable with --lobby on the server).
                    _identityProvider = new SdLobbyIssueProvider();
                    builder = builder.WithLobbyIdentity(_identityProvider);
                }
            }

            _flow = new KlothoSessionFlow(builder.Build());

            // Hand the main transport to the driver (before any session is created): the driver pumps it
            // while idle and routes idle disconnects to OnIdleDisconnected. Subscribing here keeps the
            // driver ahead of NetworkService so it observes a disconnect's pre-transition Phase.
            _sessionDriver.BindTransport(_transport, this, _flow);

            // Session creation is observed via IKlothoSessionObserver.OnSessionCreated(session, kind) —
            // no per-role Flow event subscription needed.

            // Populate static fault-injection schedule before any path can short-return.
            // OnSessionCreated (host/guest branch) AttachToSession reads this state, so it must be
            // loaded regardless of which entry path (cold-start reconnect / host / guest) runs.
            ApplyFaultInjection();

            _gameMenu.IsHost = Role.IsLocalHost();

            // Feed the lobby roster (DisplayName + Ready per player) to the menu; reads the current
            // session each frame, so it is empty before connect and after stop.
            _gameMenu.RosterProvider = () => _session?.Players;
            
            // AutoReconnect eligibility — role-decided.
            // P2P host is excluded (host death ends the session); SD client / P2P guest are eligible.
            if (Role.IsReconnectEligible())
            {
                bool started = KlothoAutoReconnect.TryStart(
                    _credentialsStore,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Application.version,
                    ct => ReconnectAsync(ct).Forget(),
                    destroyCancellationToken);
                if (started)
                {
                    _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
                    return;
                }
            }
            _gameMenu.SetActionType(Role.IsLocalHost() ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        private void ApplyFaultInjection()
        {
            var path = System.IO.Path.Combine(Application.streamingAssetsPath, "faultinjectionconfig.json");
            FaultInjectionLoader.TryLoadAndApply(path, _logger);

#if KLOTHO_FAULT_INJECTION
            // The positive control. The config arms the WHEN and WHO; the mutation itself has to
            // come from here, because core has no idea what a Brawler component is.
            //
            // This is the counterpart to ForceClientDesync, and the difference matters: that one only
            // salts the total hash, so every layer still agrees and the diagnostic funnel is SUPPOSED to
            // answer "cannot localize" (the negative control). This one moves real component state, so the
            // funnel must name TransformComponent — if it names something else, or nothing, the diagnosis
            // is lying.
            if (FaultInjection.StateCorruptionTick >= 0)
                FaultInjection.StateCorruptionMutator = CorruptFirstTransform;
#endif
        }

#if KLOTHO_FAULT_INJECTION
        // Fixed sentinel, SET not accumulated: the corrupted tick is re-executed on every verified resim
        // and rollback, and a mutation that drifted per execution would wash the divergence out.
        //
        // Corrupts Transform.Scale (NOT Position). No Brawler/core system writes Scale, so the
        // injected divergence PERSISTS across ticks instead of being overwritten by the movement system on
        // the next tick. That makes the desync recur at every sync check and escalate the P2P desync ladder
        // (count N/3) to a FullStateResync (ApplyReason.ResyncRequest) — the path where the replay is truncated
        // on a corrective reset. (A Position SET washes out in a single rollback: desync peak 1, never escalates,
        // and the replay records full-length.) It is still a TransformComponent mutation,
        // so the diagnostic funnel still localizes the divergence to TransformComponent.
        private static readonly FPVector3 CorruptedScale =
            new FPVector3(FP64.FromInt(7), FP64.FromInt(7), FP64.FromInt(7));

        private static void CorruptFirstTransform(Frame frame)
        {
            var filter = frame.Filter<TransformComponent>();
            if (!filter.Next(out var entity)) return;   // nothing to corrupt — leave the state alone

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Scale = CorruptedScale;
        }
#endif

        private void OnEnable()
        {
            _gameMenu.IpAddress = _brawlerSettings._hostAddress;

            _gameMenu._btnHost.onClick.AddListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.AddListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.AddListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.AddListener(StartReplay);
            _gameMenu._btnSpectator.onClick.AddListener(StartSpectator);
            _gameMenu.IpAddressInput.onValueChanged.AddListener(OnIpAddressInputChanged);
        }

        private void OnDisable()
        {
            _gameMenu._btnHost.onClick.RemoveListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.RemoveListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.RemoveListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.RemoveListener(StartReplay);
            _gameMenu._btnSpectator.onClick.RemoveListener(StartSpectator);
            _gameMenu.IpAddressInput.onValueChanged.RemoveListener(OnIpAddressInputChanged);
        }

        // Single-shot guard — both OnDestroy and OnApplicationQuit fire on normal app exit;
        // the second invocation must be a no-op so _input.Dispose / event unsubscription are
        // not invoked twice on the same instance.
        private bool _teardownInvoked;

        private void OnDestroy() => TeardownAll();
        private void OnApplicationQuit() => TeardownAll();

        private void TeardownAll()
        {
            if (_teardownInvoked) return;
            _teardownInvoked = true;

            // Order matters:
            //  1. DetachAndStop fires the Stopping hook while subscriptions are still live
            //     (Driver.OnDestroy may run first — DetachAndStop is idempotent via Session==null).
            //  2. Unsubscribe the Driver hook afterward to block post-teardown firing.
            //  3. Cancel async work, dispose input — terminal cleanup. The main transport is owned by
            //     the driver and disconnected in its OnDestroy, not here.
            // Process-exit teardown — preserve persisted Reconnect credentials so a relaunch can Reconnect.
            // saveReplay: false — process-exit must not write a replay (matches the prior guarded behavior).
            _sessionDriver?.DetachAndStop(keepReconnectCredentials: true, saveReplay: false);

            if (_sessionDriver != null)
                _sessionDriver.PreSessionUpdate -= OnPreSessionUpdate;

            _flow?.DisposeConnect();
            _flow = null;

            _input?.Dispose();
        }

        // ────────────────────────────────────────────
        // Game flow
        // ────────────────────────────────────────────

        private void OnBtnHost()
        {
            // Host button is meaningful only when the mode supports a local host affordance.
            if (!_modeStrategy.SupportsLocalHost) return;

            _brawlerSettings._isHost = true;
            _gameMenu.IsHost = true;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                // Cancel in-flight + clear credentials. SetActionType is left to the ReconnectAsync
                // OCE catch (→ ResetToInitialUi) so it runs once, race-safe.
                _flow?.CancelConnect();
                _credentialsStore.Clear();
                return;
            }
            if (_gameMenu.CurrentAction == GameMenu.ActionType.JoinRoom)
                _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
        }

        private void OnBtnGuest()
        {
            // The Host/Guest toggle is meaningful only when the mode exposes a local host
            // affordance to flip away from. SD mode has no local host, so the toggle is a no-op.
            if (!_modeStrategy.SupportsLocalHost) return;

            _brawlerSettings._isHost = false;
            _gameMenu.IsHost = false;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                _flow?.CancelConnect();
                _credentialsStore.Clear();
                return;
            }
            if (_gameMenu.CurrentAction == GameMenu.ActionType.CreateRoom)
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
        }

        private void OnBtnAction()
        {
            switch(_gameMenu.CurrentAction)
            {
            case GameMenu.ActionType.CreateRoom:
                StartHost();
                break;
            case GameMenu.ActionType.JoinRoom:
                JoinGame();
                // Both P2P / SD are async — transition to Ready when JoinGameAsync completes
                break;
            case GameMenu.ActionType.Ready:
                SetReady();
                break;
            case GameMenu.ActionType.Playing:
                StopGame();
                break;
            case GameMenu.ActionType.Reconnect:
                // Cancel — credentials kept; ReconnectAsync.catch (OperationCanceledException) → ResetToInitialUi.
                _flow?.CancelConnect();
                break;
            }
        }

        private void StartHost()
        {
            if (_sessionConfig == null)
            {
                _logger?.KError($"[Brawler] SessionConfig is required for host");
                _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
                return;
            }

            _logger?.KInformation($"[Brawler] Hosting game");

            // StartHost requires a mode that supports a local host. Reject incompatible Inspector
            // setting up front rather than silently mutating the USimulationConfig ScriptableObject
            // (Editor play mode persists such mutations back to the .asset file, corrupting the
            // user's saved setting).
            // P2P host authors the per-match dynamic config (botCount) into MatchConfigData so it propagates
            // to the guest. MatchConfigData is runtime-only (not a serialized field), so this in-memory set
            // does NOT persist to the .asset — unlike StageId (SerializeField), no clone is needed.
            byte[] matchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = _brawlerSettings._botCount });
            ISimulationConfig simulationConfig;
            if (_simulationConfig != null)
            {
                // Defensive backstop: the UI routes here only when the resolved role is P2P host,
                // so this guard does not fire in normal flow — it rejects a stale P2P-only config.
                if (Role != KlothoRole.P2PHost)
                {
                    _logger?.KError($"[Brawler] StartHost requires the P2P host role (got {Role}) — set Mode = P2P in the Inspector or use a separate P2P-host SimulationConfig asset");
                    _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
                    return;
                }
                _simulationConfig.MatchConfigData = matchConfigData;
                simulationConfig = _simulationConfig;
            }
            else
            {
                var sc = new SimulationConfig();
                sc.Mode = NetworkMode.P2P;
                sc.MatchConfigData = matchConfigData;
                simulationConfig = sc;
            }

            // Reservation-pruning denylist — always applied (mirrors the dedicated server). This P2P
            // host is the layout authority; the denylist is wire-propagated to the guest via
            // SimulationConfigMessage, so setting it here alone keeps both peers' layouts identical. Uniform
            // via ISimulationConfig (runtime override — does NOT persist to the authored asset). Fail-safe:
            // the denylist prunes only what it lists — currently the single MovementComponent.
            simulationConfig.SetRuntimePrunedComponentTypeIds(BrawlerPrunedComponents.ResolveTypeIds());

            _session = _flow.StartHostAndListen(simulationConfig, _sessionConfig, "Game",
                _brawlerSettings._hostAddress, _brawlerSettings._port);
            if (_session == null)
            {
                // listen bind failed — the framework already tore the session down via OnSessionStopped,
                // which restored the menu. Just abort.
                _logger?.KError($"[Brawler] Failed to host on port {_brawlerSettings._port} — aborting StartHost.");
                return;
            }

            // Broadcast the local player's character selection via PlayerConfig
            _session.SendPlayerConfig(new BrawlerPlayerConfig
            {
                SelectedCharacterClass = _brawlerSettings._characterClass,
            });

            _gameMenu.SetActionType(GameMenu.ActionType.Ready);
        }

        private void JoinGame()
        {
            // Both P2P / SD (single / multi room) use the async path through KlothoConnection
            JoinGameAsync(destroyCancellationToken).Forget();
        }

        private async UniTaskVoid JoinGameAsync(CancellationToken ct)
        {
            _logger?.KInformation($"[Brawler] Joining game");
            _gameMenu.ReconnectStatus = "Connecting...";

            string host = _brawlerSettings._hostAddress;
            int port = _brawlerSettings._port;
            int roomId = _brawlerSettings._roomId;

            try
            {
                // SD + lobby on: fetch a signed ticket + room assignment BEFORE connecting; the lobby-assigned
                // endpoint/roomId win over the Inspector values. Abort on Full / decline / timeout — never
                // connect with an empty ticket (the server validator would reject it with IdentityRequired).
                if (_brawlerLobby._lobbyEnabled && _modeStrategy.Mode == NetworkMode.ServerDriven)
                {
                    var issue = await TryFetchLobbyAsync(ct);
                    if (!issue.HasValue)
                    {
                        _gameMenu.ReconnectStatus = null;
                        _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
                        return;
                    }
                    host = issue.Value.Host; port = issue.Value.Port; roomId = issue.Value.RoomId;
                }
                _session = await _flow.JoinAsync(
                    _modeStrategy, _transport,
                    host, port, roomId, _sessionConfig, ct);

                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.Ready);
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] Join canceled");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (JoinFailedException jfe)
            {
                _logger?.KWarning($"[Brawler] Join rejected: {jfe.Reason.ToName()}");
                _gameMenu.ReconnectStatus = jfe.Reason.ToDefaultMessage();
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (Exception e)
            {
                _logger?.KError(e, $"[Brawler] JoinGame failed");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
        }

        // SD only. Fetches a lobby ticket + room assignment over LiteNetLib and stores the ticket in the
        // provider, then returns the assignment (Ok) — or null (abort join) on Full / timeout / decline.
        // Start DevLobbyServer first. Mirrors SdSample's SdGameController.TryFetchLobbyAsync.
        private async UniTask<IssueResult?> TryFetchLobbyAsync(CancellationToken ct)
        {
            using var issueClient = new LiteNetLibLobbyIssueClient(_logger, _brawlerLobby._lobbyAddress, _brawlerLobby._lobbyPort);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            try
            {
                string account = string.IsNullOrEmpty(_brawlerLobby._account)
                    ? "dev-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    : _brawlerLobby._account;
                string matchId = string.IsNullOrWhiteSpace(_brawlerLobby._matchId)
                    ? SdDevIdentity.DevMatchId : _brawlerLobby._matchId.Trim();
                IssueResult issue = await issueClient.IssueAsync(account, _displayName, matchId, cts.Token);
                if (issue.Full)
                {
                    _logger?.KWarning($"[Brawler] lobby FULL (all rooms occupied) — retry later.");
                    return null;
                }
                if (!issue.Ok || string.IsNullOrEmpty(issue.Ticket))
                {
                    _logger?.KWarning($"[Brawler] lobby declined (empty ticket) — aborting join.");
                    return null;
                }
                _identityProvider?.SetTicket(issue.Ticket);
                _logger?.KInformation($"[Brawler] lobby assigned {issue.Host}:{issue.Port} room={issue.RoomId} ({account}).");
                return issue;
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] lobby fetch timed out — is DevLobbyServer running?");
                return null;
            }
        }

        private async UniTaskVoid ReconnectAsync(CancellationToken ct)
        {
            _logger?.KInformation($"[Brawler] Cold-start reconnect");
            _gameMenu.ReconnectStatus = "Reconnecting...";

            try
            {
                var creds = _credentialsStore.Load();
                _session = await _flow.ReconnectAsync(_transport, creds, _sessionConfig, ct);

                _gameMenu.ReconnectStatus = null;
                // ActionType transition is delegated to OnLateJoinActive (catchup completion callback).
                // The "Cancel" label remains visible during catchup; OnLateJoinActive switches to Playing.
            }
            catch (OperationCanceledException)
            {
                // Cancel keeps credentials — next boot can auto-retry.
                _logger?.KWarning($"[Brawler] Reconnect canceled");
                _gameMenu.ReconnectStatus = null;
                // Status already cleared above; keep the connect CTS (old FallbackToInitial behavior).
                ResetToInitialUi(cancelConnect: false, clearStatus: false);
            }
            catch (ReconnectFailedException e)
            {
                _logger?.KError(e, $"[Brawler] Reconnect rejected: {e.Reason.ToName()}");
                HandleReconnectFailure(e.Reason);
            }
            catch (Exception e)
            {
                // Fallback — transport / serialization / unexpected failure.
                _logger?.KError(e, $"[Brawler] Reconnect attempt failed (non-rejected)");
                HandleReconnectFailure(ReconnectRejectReason.Unknown);
            }
        }

        private void SetReady()
        {
            _logger?.KInformation($"[Brawler] Ready");
            _session?.SetReady(true);

            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        private void StartReplay()
        {
            if (_session != null
                && _session.Phase != SessionPhase.None
                && _session.Phase != SessionPhase.Disconnected)
                return;

            _logger?.KInformation($"[Brawler] Replay started");

            try
            {
                _session = _flow.StartReplayFromFile(_replayPath);
                _gameMenu.SetActionType(GameMenu.ActionType.Playing);
            }
            catch (xpTURN.Klotho.Replay.ReplayLoadException e)
            {
                _logger?.KError(e, $"[Brawler] Replay load failed: {_replayPath}");
            }
        }

        // ── Spectator entry ──
        //
        // StartSpectator delegates to _flow.SpectateAsync — framework handles the
        // SpectatorService / two-Config await / Engine/Simulation construction internally.
        // The game side supplies a CallbacksFactory (BuildCallbacks) that fires after
        // SpectatorAcceptMessage delivers server-authoritative SimulationConfig + SessionConfig.
        private void StartSpectator()
        {
            if (_session != null
                && _session.Phase != SessionPhase.None
                && _session.Phase != SessionPhase.Disconnected)
                return;

            _logger?.KInformation($"[Brawler] Spectator connecting to {_brawlerSettings._hostAddress}:{_brawlerSettings._port}");

            _flow?.CancelConnect();
            StartSpectatorAsync(destroyCancellationToken).Forget();
        }

        private async UniTaskVoid StartSpectatorAsync(CancellationToken ct)
        {
            try
            {
                _session = await _flow.SpectateAsync(
                    _brawlerSettings._hostAddress, _brawlerSettings._port,
                    _brawlerSettings._roomId, ct);

                _gameMenu.SetActionType(GameMenu.ActionType.Playing);
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] Spectator canceled");
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (Exception e)
            {
                _logger?.KError(e, $"[Brawler] Spectator connect failed");
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
        }

        private SessionCallbacks BuildCallbacks(ISimulationConfig simCfg, ISessionConfig sessionCfg)
        {
            int maxPlayers = sessionCfg?.MaxPlayers ?? InitialMaxPlayersGuess();
            // The framework invokes this factory with the resolved SimulationConfig — server-stamped for
            // an SD guest, host-authored for P2P — so StageId selects the same stage geometry the server
            // built (fp match). StageId 0 / an empty table falls back to the default stage.
            _resolvedStageId = simCfg?.StageId ?? 0;
            var (colliders, navMesh) = ResolveStageGeometry(_resolvedStageId);
            // botCount is a per-match dynamic knob carried in MatchConfigData (authority-set, propagated).
            // No propagated config (null) → fall back to the Inspector value (lobbyless/pure-client, no-regression).
            // Effective consumer is the authority (P2P host / SD server); SD guests get bots via FullState.
            byte[] matchCfg = simCfg?.MatchConfigData;
            int botCount = matchCfg != null ? BrawlerMatchConfig.Decode(matchCfg).BotCount : _brawlerSettings._botCount;
            _simCallbacks = new BrawlerSimulationCallbacks(
                _input, colliders, navMesh,
                maxPlayers, botCount, stageId: _resolvedStageId);
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks);
            return new SessionCallbacks(_simCallbacks, _viewCallbacks);
        }

        // Finds the configured stage entry for stageId; stageId 0 (core default / no source) maps to the
        // default stage (stageId 1). Returns null when no table entry matches.
        private StageResource ResolveStage(int stageId)
        {
            if (_stageResources == null) return null;
            foreach (var s in _stageResources)
                if (s != null && s._stageId == stageId) return s;
            if (stageId == 0)
                foreach (var s in _stageResources)
                    if (s != null && s._stageId == 1) return s;
            return null;
        }

        // Baked deterministic geometry for the stage. Uses the stage table when populated; otherwise the
        // single default assets loaded in Start (= the default stage, back-compat).
        private (List<FPStaticCollider> colliders, FPNavMesh navMesh) ResolveStageGeometry(int stageId)
        {
            var s = ResolveStage(stageId);
            if (s != null && s._colliders != null && s._navMesh != null)
                return (FPStaticColliderSerializer.Load(s._colliders.bytes),
                        FPNavMeshSerializer.Deserialize(s._navMesh.bytes));
            return (_staticColliders, _navMesh);
        }

        // Requests the stage's additive view scene (visual only — no sim state; determinism lives in the
        // baked geometry). Sets the desired target and reconciles; the target is null when the stage has no
        // view scene configured (e.g. headless).
        private void LoadStageView(int stageId)
        {
            var s = ResolveStage(stageId);
            _desiredStageScene = (s != null && !string.IsNullOrEmpty(s._sceneName)) ? s._sceneName : null;
            ReconcileStageView();
        }

        private void UnloadStageView()
        {
            _desiredStageScene = null;
            ReconcileStageView();
        }

        // Drives _currentStageScene toward _desiredStageScene one SceneManager async op at a time, gating on
        // our own state rather than Scene.isLoaded — that flag is false mid-load and true mid-unload, so
        // reading it races the in-flight op (stop-during-load leaks the scene; rejoin-during-unload drops the
        // view). Re-invoked from each op's completed callback so a target change during a load/unload settles
        // correctly. Main-thread only (session observer callbacks + AsyncOperation.completed), so no locking.
        private void ReconcileStageView()
        {
            if (_stageSceneBusy) return;                          // in flight — the completed callback re-invokes
            if (_desiredStageScene == _currentStageScene) return; // settled

            if (_currentStageScene != null)
            {
                // Unload the current scene first (target is a different scene or none).
                _stageSceneBusy = true;
                var op = SceneManager.UnloadSceneAsync(_currentStageScene);
                if (op != null)
                    op.completed += _ => { _currentStageScene = null; _stageSceneBusy = false; ReconcileStageView(); };
                else { _currentStageScene = null; _stageSceneBusy = false; ReconcileStageView(); } // not loaded → settled
            }
            else
            {
                // Nothing loaded — load the desired scene additively.
                _stageSceneBusy = true;
                string toLoad = _desiredStageScene;
                var op = SceneManager.LoadSceneAsync(toLoad, LoadSceneMode.Additive);
                if (op != null)
                    op.completed += _ => { _currentStageScene = toLoad; _stageSceneBusy = false; ReconcileStageView(); };
                else { _stageSceneBusy = false; _desiredStageScene = null; _logger?.KError($"[Brawler] Stage view scene '{toLoad}' failed to load (in Build Settings?)"); } // don't retry-loop
            }
        }

        // Single role-bearing creation callback — was OnAnyFlowSessionCreated (common) +
        // OnHostOrGuestSessionCreated + OnReplayOrSpectatorSessionCreated, merged via the kind arg.
        public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
        {
            // Replay output is declared at flow-build time via WithReplaySave; KlothoSessionFlow stamps
            // it onto host / guest sessions only, so the game no longer configures it per session here.
            _sessionDriver.Attach(session);

            // Initial push — state callbacks fire only on transition, so seed GameMenu from current state.
            _gameMenu.State      = session.State;
            _gameMenu.Phase      = session.Phase;
            _gameMenu.Players    = session.PlayerCount;
            _gameMenu.IsAllReady = session.AllPlayersReady;

            if (kind == SessionEntryKind.Host || kind == SessionEntryKind.Guest)
            {
                // Host/Guest path — FaultInjection attach (main _transport is live).
                // roleLabel comes from the resolved role, not raw _isHost: SD collapses to client
                // so a stale Inspector _isHost = true cannot leak into the diagnostic label.
                string roleLabel = Role.IsLocalHost() ? "host" : "guest";
                xpTURN.Klotho.Diagnostics.FaultInjectionRuntime.AttachToSession(
                    session, _transport, _logger,
                    roleLabel,
                    _sessionDriver);
                _faultInjectionHotkey?.Attach(session, _logger);
            }
            // Replay/Spectator skip FaultInjection (main _transport is idle for those modes).

            // Load the stage's additive view scene (visual only) for the resolved stage. All entry kinds
            // resolve a stage in BuildCallbacks (host/guest/spectator/replay), so this is NOT gated by kind —
            // otherwise a stage-2 spectator/replay would render the default environment over stage-2 geometry.
            LoadStageView(_resolvedStageId);

            InitializeViewSync(session.Engine, session.Simulation);
        }

        private void InitializeViewSync(IKlothoEngine engine, EcsSimulation simulation)
        {
            // EVU.Initialize creates a fresh PlayerViewRegistry — must run before ViewSync.Initialize
            // so the registry is non-null when ViewSync subscribes to its events.
            // Must be called after engine.Start / StartSpectator / StartReplay has completed.
            _entityViewUpdater?.Initialize(engine);

            _viewSync.Initialize(engine, simulation, _entityViewUpdater);
            _viewSync.OnLocalCharacterSpawned += OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned += OnLocalCharacterDespawned;
        }

        private void OnLocalCharacterSpawned()
        {
            _logger?.KInformation($"[Brawler] Local Character Spawned");
        }

        private void OnLocalCharacterDespawned()
        {
            _logger?.KInformation($"[Brawler] Local Character Despawned");
        }

        // Stop intent — thin router, no teardown. With a live session the framework drives
        // session.Stop → OnSessionStopped (terminal teardown); with no session it is just a UI reset
        // (idle/cancel paths). No re-entry guard: the framework owns idempotency.
        private void StopGame()
        {
            if (_session != null)
                _sessionDriver.DetachAndStop();
            else
                ResetToInitialUi();
        }

        // Session-independent return-to-initial-UI — terminal teardown (OnSessionStopped), no-session
        // stop intent, idle-disconnect, and reconnect cancel/fail all route here. Transport is no longer
        // disconnected (the driver owns it). cancelConnect/clearStatus absorb the two prior variants:
        // the old FallbackToInitial path left the connect CTS and the reconnect-reject message untouched.
        private void ResetToInitialUi(bool cancelConnect = true, bool clearStatus = true)
        {
            if (cancelConnect)
                _flow?.CancelConnect();

            if (clearStatus)
                _gameMenu.ReconnectStatus = null;

            _gameMenu.SetActionType(Role.IsLocalHost() ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        // ────────────────────────────────────────────
        // Reconnection
        // ────────────────────────────────────────────

        private void HandleReconnectFailure(ReconnectRejectReason reason)
        {
            _credentialsStore.Clear();

            if (reason == ReconnectRejectReason.AlreadyConnected)
            {
                _logger?.KWarning($"[Brawler] Reconnect rejected: AlreadyConnected — another device holds this PlayerId");
            }

            _gameMenu.ReconnectStatus = reason.ToDefaultMessage();
            // Preserve the reject message and the connect CTS (old FallbackToInitial behavior).
            ResetToInitialUi(cancelConnect: false, clearStatus: false);
        }

        public void OnPlayerDisconnected(IPlayerInfo player)
        {
            _logger?.KWarning($"[Brawler] Player {player.PlayerId} disconnected, waiting for reconnection...");
            _gameMenu.ReconnectStatus = $"P{player.PlayerId} disconnected";
        }

        public void OnPlayerReconnected(IPlayerInfo player)
        {
            _logger?.KInformation($"[Brawler] Player {player.PlayerId} reconnected");
            _gameMenu.ReconnectStatus = null;
        }

        public void OnReconnecting()
        {
            // Suppress reconnect UX when match-end is in progress — host disconnect after match end is not a network error.
            if (_session?.Engine?.IsMatchEnded == true) return;

            _logger?.KWarning($"[Brawler] Disconnected, reconnecting...");
            _gameMenu.ReconnectStatus = "Reconnecting...";
        }

        public void OnReconnectFailed(ReconnectRejectReason reason)
        {
            _logger?.KError($"[Brawler] Reconnection failed: {reason.ToName()}");
            _gameMenu.ReconnectStatus = null;
            StopGame();
        }

        public void OnMatchAborted(AbortReason reason)
        {
            _logger?.KWarning($"[Brawler] Match aborted: {reason}");
            _gameMenu.ReconnectStatus = reason switch
            {
                AbortReason.ChainStallTimeout => "Match ended: communication timeout",
                AbortReason.StateDivergence => "Match ended: state divergence",
                AbortReason.ReconnectFailed => "Match ended: reconnection failed",
                _ => "Match ended",
            };
            StopGame();
        }

        public void OnMatchEnded(int tick, IMatchEndEvent endEvt)
        {
            _logger?.KInformation(
                $"[Brawler] Match ended: tick={tick}, winner={endEvt.WinnerPlayerId}, reason={endEvt.Reason}");

            // KlothoSession path: the scheduler runs inside Session.Update — game side no-op.
            // Spectator path: the same Session-internal scheduler also fires.
        }

        public void OnMatchReset(ResetReason reason)
        {
            _logger?.KWarning($"[Brawler] Match reset: {reason} — state recovered, match continues");
            _gameMenu.ReconnectStatus = "State recovered — match continues";
        }

        public void OnReconnected()
        {
            _logger?.KInformation($"[Brawler] Reconnected successfully");
            _gameMenu.ReconnectStatus = null;
        }

        private void OnLateJoinActive()
        {
            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        // Explicit forwarder — KlothoEngine.OnCatchupComplete event → existing sample handler name.
        // Avoids renaming the sample's semantic handler while satisfying IKlothoSessionObserver.
        void IKlothoSessionObserver.OnCatchupComplete() => OnLateJoinActive();

        // Terminal teardown — the framework calls this exactly once at the end of session.Stop(),
        // on both game-initiated and framework-internal stops. UI/transport only; replay is saved
        // by the framework (ConfigureReplaySave). No re-entry guard needed.
        public void OnSessionStopped()
        {
            // Process-exit: TeardownAll already owns terminal cleanup and _gameMenu may have been
            // destroyed first in OnDestroy ordering — skip UI to avoid MissingReferenceException.
            if (_teardownInvoked) { _session = null; return; }

            _logger?.KInformation($"[Brawler] Game stopped");
            _session = null;
            UnloadStageView();
            ResetToInitialUi();
        }

        // Idle transport drop (no session, no connect in flight) — the driver routes it here.
        void IKlothoSessionObserver.OnIdleDisconnected(DisconnectReason reason) => ResetToInitialUi();

        public void OnResyncCompleted(int tick)
        {
            _logger?.KInformation($"[Brawler] Resync completed at tick={tick}");
        }

        // Local MaxPlayers guess for non-host paths where the server-authoritative SessionConfig
        // has not yet been received. The session is reseeded by GameStartMessage /
        // ReconnectAcceptMessage / FullState restore shortly after — this value only sizes
        // BrawlerSimulationCallbacks prior to that. Default 4 matches SessionConfig.MaxPlayers.
        private int InitialMaxPlayersGuess() => _sessionConfig != null ? _sessionConfig.MaxPlayers : 4;

        // ────────────────────────────────────────────
        // Input
        // ────────────────────────────────────────────

        private FPVector2 GetFacingAimDirection()
        {
            // Direction the character is facing (based on TransformComponent.Rotation)
            // Since Rotation = Atan2(aimDir.x, aimDir.y), invert: sin(rot)=x, cos(rot)=y
            var frame = _session?.Simulation?.Frame;
            if (frame != null)
            {
                int localId = _session.Engine.LocalPlayerId;
                var filter = frame.Filter<TransformComponent, OwnerComponent>();
                while (filter.Next(out var entity))
                {
                    ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                    if (owner.OwnerId != localId) continue;
                    ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                    FP64 rot = tr.Rotation;
                    return new FPVector2(FP64.Sin(rot), FP64.Cos(rot));
                }
            }
            return FPVector2.Right;
        }

        // ────────────────────────────────────────────
        // GUI state
        // ────────────────────────────────────────────

        public void OnStateChanged(KlothoState s)         => _gameMenu.State = s;
        public void OnPhaseChanged(SessionPhase p)        => _gameMenu.Phase = p;
        public void OnPlayerCountChanged(int n)           => _gameMenu.Players = n;
        public void OnAllPlayersReadyChanged(bool v)      => _gameMenu.IsAllReady = v;

        private void OnIpAddressInputChanged(string addr) => _brawlerSettings._hostAddress = addr;

        public void OnBtnSlot1()
        {
            _simCallbacks?.SendUseConsumableCommand(_session?.Engine);
        }

        /// <summary>
        /// Test entry (wire to a UI button or key in the editor): places a BOX in
        /// front of the local character, at a random one of its 16 orientations, via the reliable
        /// channel — server-routed, so a single client trigger stays deterministic across all peers.
        /// </summary>
        public void OnBtnPlaceBuilding()
        {
            _simCallbacks?.SendPlaceBuildingCommand(_session?.Engine);
        }

        /// <summary>
        /// Test entry (wire to a second UI button): places a HEXAGON, via its own command.
        /// The split is in the input, not the state — a hexagon has no orientation to send, and the
        /// stored BuildingComponent is the same either way.
        /// </summary>
        public void OnBtnPlaceHexBuilding()
        {
            _simCallbacks?.SendPlaceHexBuildingCommand(_session?.Engine);
        }
    }
}
