using System;
using System.Collections.Generic;
using System.Threading;

using UnityEngine;
using UnityEngine.InputSystem;

using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;
using ZLogger.Unity;
using ZLogger.Providers;
using Utf8StringInterpolation;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Samples.Brawler;
using xpTURN.Klotho.Unity;

namespace Brawler
{
    [Serializable]
    public class BrawlerSettings
    {
        [Header("ServerSettings")]
        [SerializeField] public NetworkMode _mode = NetworkMode.ServerDriven;
        [SerializeField] public string _hostAddress = "localhost";
        [SerializeField] public int _port = 777;

        [Header("ServerDriven")]
        [SerializeField] public int _roomId = 0;

        [Header("P2P")]
        [SerializeField] public bool _isHost = true;
        [SerializeField] public int _maxPlayers = 2;
        [SerializeField] public int _botCount = 0;

        [Header("PlayerSettings")]
        [SerializeField] public int _characterClass = 0; // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
    }

    /// <summary>
    /// Brawler sample game controller.
    ///
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class BrawlerGameController : MonoBehaviour
    {
        [Header("Debug")]
        [SerializeField] private LogLevel _logLevel = LogLevel.Information;

        [Header("Settings")]
        [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
        [SerializeField] private USimulationConfig _simulationConfig;

        [Header("Scene References")]
        [SerializeField] private GameMenu _gameMenu;
        [SerializeField] private BrawlerViewSync _viewSync;

        // EVU reference. If the prefab has an EntityView, EVU automatically spawns it.
        // If null in the Inspector, the EVU hook is skipped.
        [SerializeField] private xpTURN.Klotho.EntityViewUpdater _entityViewUpdater;

        [Header("Static Colliders")]
        [SerializeField] private TextAsset _staticCollidersAsset;

        [Header("NavMesh")]
        [SerializeField] private TextAsset _navMeshAsset;

        [Header("DataAssets")]
        [SerializeField] private TextAsset _dataAsset;

        private ILogger _logger;
        List<FPStaticCollider> _staticColliders;
        FPNavMesh _navMesh;
        List<IDataAsset> _dataAssets;
        private IDataAssetRegistry _assetRegistry;

        private KlothoSession _session;
        private LiteNetLibTransport _transport;
        private Camera _mainCamera;
        private CancellationTokenSource _connectCts;  // For canceling JoinGameAsync
        private IReconnectCredentialsStore _credentialsStore;

        private BrawlerInputCapture _input;
        private BrawlerSimulationCallbacks _simCallbacks;
        private BrawlerViewCallbacks _viewCallbacks;

        // Spectator mode (no session)
        private SpectatorService _spectatorService;
        private KlothoEngine _spectatorEngine;
        private EcsSimulation _spectatorSimulation;
        private CommandFactory _spectatorCommandFactory;
        private ISimulationConfig _pendingSpectatorSimConfig;
        private ISessionConfig _pendingSpectatorSessionConfig;

        private long _lastTicks;
        private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

        public bool IsHost => _brawlerSettings._isHost;
        public KlothoState State => _session?.State ?? (_spectatorEngine != null ? KlothoState.Running : KlothoState.Idle);
        public int CurrentTick => (_session?.Engine ?? _spectatorEngine)?.CurrentTick ?? 0;
        public int Players => _session?.NetworkService?.PlayerCount ?? 0;
        public int Entities => (_session?.Simulation ?? _spectatorSimulation)?.Frame.Entities.Count ?? 0;
        public bool AllPlayersReady => _session?.NetworkService?.AllPlayersReady ?? false;
        public SessionPhase Phase => _session?.NetworkService?.Phase ?? SessionPhase.None;

        private KlothoEngine ActiveEngine => _session?.Engine ?? _spectatorEngine;
        private EcsSimulation ActiveSimulation => _session?.Simulation ?? _spectatorSimulation;

        private void Construct(ILogger logger)
        {
            _logger = logger;
        }

        private void CreateLogger()
        {
            // Logger factory setup
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_logLevel);
                builder.AddZLoggerUnityDebug(); // Output logs to UnityDebug

                // Output to yyyy-MM-dd_*.log; roll over when exceeding 1MB or when the date changes
                builder.AddZLoggerRollingFile(options =>
                {
                    options.FilePathSelector = (dt, index) => $"Logs/Client_{dt:yyyy-MM-dd-HH-mm-ss}_{index:000}.log";
                    options.RollingInterval = RollingInterval.Day;
                    options.RollingSizeKB = 1024 * 1024;
                    options.UsePlainTextFormatter(formatter =>
                    {
                        formatter.SetPrefixFormatter($"{0}|{1:short}|", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel));
                        formatter.SetExceptionFormatter((writer, ex) => Utf8String.Format(writer, $"{ex.Message}\n{ex.StackTrace}"));
                    });
                });
            });

            // Register the logger instance
            _logger = loggerFactory.CreateLogger("Client");
            _logger?.ZLogInformation($"ZLogger logging started!");
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            CreateLogger();
        }

        private void Start()
        {
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
            _transport = new LiteNetLibTransport(_logger, logLevels);
            _transport.OnDisconnected += OnDisconnected;

            _input = new BrawlerInputCapture();
            _input.Enable();

            if (_brawlerSettings._mode != NetworkMode.ServerDriven)
            {
                _brawlerSettings._roomId = -1;
            }

            _gameMenu.IsHost = IsHost;
            // P2P host is not a cold-start target — host death ends the session.
            // SD client / P2P guest are eligible for auto-reconnect.
            bool isP2PHost = _brawlerSettings._mode == NetworkMode.P2P && _brawlerSettings._isHost;
            if (!isP2PHost && TryAutoReconnect())
                return;
            _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);

            bool TryAutoReconnect()
            {
                var creds = _credentialsStore.Load();
                if (creds == null) return false;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!_credentialsStore.IsValid(creds, now, Application.version))
                {
                    _credentialsStore.Clear();
                    return false;
                }

                _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
                _connectCts = new CancellationTokenSource();
                ReconnectAsync(_connectCts.Token).Forget();
                return true;
            }
        }

        private void OnEnable()
        {
            _gameMenu.IpAddress = _brawlerSettings._hostAddress;
            
            _gameMenu._btnHost.onClick.AddListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.AddListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.AddListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.AddListener(StartReplay);
            _gameMenu._btnSpectator.onClick.AddListener(StartSpectator);
        }

        private void OnDisable()
        {
            _gameMenu._btnHost.onClick.RemoveListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.RemoveListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.RemoveListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.RemoveListener(StartReplay);
            _gameMenu._btnSpectator.onClick.RemoveListener(StartSpectator);
        }

        private void Update()
        {
            UpdateStatus();

            var engine = ActiveEngine;

            if (engine == null)
            {
                _transport?.PollEvents();
                // In spectator mode the Engine is created after receiving SpectatorAcceptMessage,
                // so the spectator transport must be polled even while engine is still null in order for the handshake to proceed.
                _spectatorService?.Update();
                return;
            }

            // Real-time clock based deltaTime
            var nowTick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float deltaTime = (_lastTicks > 0) ? (nowTick - _lastTicks) * 0.001f : 0f;
            _lastTicks = nowTick;

            if (State == KlothoState.Running)
            {
                _input.CaptureInput();
                _input.AimDirection = GetFacingAimDirection();
            }

            if (_spectatorService != null)
            {
                _spectatorService.Update();
                _spectatorEngine?.Update(deltaTime);
            }
            else
            {
                _session?.Update(deltaTime);
            }
        }

        private void OnDestroy()
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
            _transport?.Disconnect();
            _input?.Dispose();
        }

        private void OnApplicationQuit()
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
            _transport?.Disconnect();
            _input?.Dispose();
        }

        // ────────────────────────────────────────────
        // Game flow
        // ────────────────────────────────────────────

        private void OnBtnHost()
        {
            _brawlerSettings._isHost = true;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                // Cancel in-flight + clear credentials. SetActionType is left to FallbackToInitial (race-safe).
                _connectCts?.Cancel();
                _credentialsStore.Clear();
                return;
            }
            if (_gameMenu.CurrentAction == GameMenu.ActionType.JoinRoom)
                _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
        }

        private void OnBtnGuest()
        {
            _brawlerSettings._isHost = false;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                _connectCts?.Cancel();
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
                _gameMenu.SetActionType(GameMenu.ActionType.Ready);
                break;
            case GameMenu.ActionType.JoinRoom:
                JoinGame();
                // Both P2P / SD are async — transition to Ready when JoinGameAsync completes
                break;
            case GameMenu.ActionType.Ready:
                SetReady();
                _gameMenu.SetActionType(GameMenu.ActionType.Playing);
                break;
            case GameMenu.ActionType.Playing:
                StopGame();
                _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
                break;
            case GameMenu.ActionType.Reconnect:
                // Cancel — credentials kept; ReconnectAsync.catch (OperationCanceledException) → FallbackToInitial.
                _connectCts?.Cancel();
                break;
            }
        }

        private void OnDisconnected()
        {
            // While Playing, NetworkService will attempt automatic reconnection, so do not end the game
            if (Phase == SessionPhase.Playing)
                return;

            StopGame();
            _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        private void StartHost()
        {
            _logger?.ZLogInformation($"[Brawler] Hosting game");

            ISimulationConfig simulationConfig = null;
            if (_simulationConfig != null)
            {
                simulationConfig = _simulationConfig;
                _simulationConfig.Mode = NetworkMode.P2P;
            }
            else
            {
                simulationConfig = new SimulationConfig();
                ((SimulationConfig)simulationConfig).Mode = NetworkMode.P2P;
            }

            _simCallbacks = new BrawlerSimulationCallbacks(
                _input,
                _logger,
                _staticColliders,
                _navMesh,
                _brawlerSettings._maxPlayers,
                _brawlerSettings._botCount
            );
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
            _session = KlothoSession.Create(new KlothoSessionSetup
            {
                Transport = _transport,
                Logger = _logger,
                SimulationCallbacks = _simCallbacks,
                ViewCallbacks = _viewCallbacks,
                AssetRegistry = _assetRegistry,
                SimulationConfig = simulationConfig,
                MaxPlayers = _brawlerSettings._maxPlayers,
            });
            _simCallbacks.SetNetworkService(_session.NetworkService);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ConnectPhysicsProvider();
#endif
            _session.HostGame("Game", _brawlerSettings._maxPlayers);
            _transport.Listen(_brawlerSettings._hostAddress, _brawlerSettings._port, _brawlerSettings._maxPlayers);

            _session.NetworkService.OnPlayerDisconnected += OnPlayerDisconnected;
            _session.NetworkService.OnPlayerReconnected += OnPlayerReconnected;
            _session.Engine.OnGameStart += InjectInitialStateSnapshot;

            // Broadcast the local player's character selection via PlayerConfig
            _session.SendPlayerConfig(new BrawlerPlayerConfig
            {
                SelectedCharacterClass = _brawlerSettings._characterClass,
            });

            InitializeViewSync(_session.Engine, _session.Simulation);
        }

        private void JoinGame()
        {
            // Both P2P / SD (single / multi room) use the async path through KlothoConnection
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            JoinGameAsync(_connectCts.Token).Forget();
        }

        private async UniTaskVoid JoinGameAsync(CancellationToken ct)
        {
            _logger?.ZLogInformation($"[Brawler] Joining game");
            _gameMenu.ReconnectStatus = "Connecting...";

            try
            {
                // For SD multi-room, send RoomHandshake before PlayerJoinMessage
                NetworkMessageBase preJoin = null;
                int roomId = -1;
                if (_brawlerSettings._mode == NetworkMode.ServerDriven &&
                     _brawlerSettings._roomId != -1)
                {
                    roomId = _brawlerSettings._roomId;
                    preJoin = new RoomHandshakeMessage { RoomId = roomId };
                }

                // Perform connection, handshake, and SimulationConfig reception.
                var result = await KlothoConnectionAsync.ConnectAsync(
                    _transport,
                    _brawlerSettings._hostAddress, _brawlerSettings._port,
                    ct, _logger, preJoin);

                // Create the session using the received Config.
                _simCallbacks = new BrawlerSimulationCallbacks(
                    _input,
                    _logger,
                    _staticColliders,
                    _navMesh,
                    _brawlerSettings._maxPlayers,
                    _brawlerSettings._botCount
                );
                _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
                _session = KlothoSession.Create(new KlothoSessionSetup
                {
                    Connection = result,
                    Logger = _logger,
                    SimulationCallbacks = _simCallbacks,
                    ViewCallbacks = _viewCallbacks,
                    AssetRegistry = _assetRegistry,
                    RoomId = roomId,  // SD multi-room identifier (-1 for single room)
                    // Transport / SimulationConfig are acquired automatically from Connection
                });
                _simCallbacks.SetNetworkService(_session.NetworkService);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ConnectPhysicsProvider();
#endif
                InjectCredentialsStoreIntoSession();

                _session.NetworkService.OnReconnecting += OnReconnecting;
                _session.NetworkService.OnReconnectFailed += OnReconnectFailed;
                _session.NetworkService.OnReconnected += OnReconnected;
                _session.Engine.OnCatchupComplete += OnLateJoinActive;
                _session.Engine.OnResyncCompleted += OnResyncCompleted;
                _session.Engine.OnGameStart += InjectInitialStateSnapshot;

                // Broadcast the local player's character selection via PlayerConfig
                _session.SendPlayerConfig(new BrawlerPlayerConfig
                {
                    SelectedCharacterClass = _brawlerSettings._characterClass,
                });

                InitializeViewSync(_session.Engine, _session.Simulation);

                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.Ready);
            }
            catch (OperationCanceledException)
            {
                _logger?.ZLogWarning($"[Brawler] Join canceled");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
                _transport?.Disconnect();
            }
            catch (Exception e)
            {
                _logger?.ZLogError(e, $"[Brawler] JoinGame failed");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
                _transport?.Disconnect();
            }
        }

        private async UniTaskVoid ReconnectAsync(CancellationToken ct)
        {
            _logger?.ZLogInformation($"[Brawler] Cold-start reconnect");
            _gameMenu.ReconnectStatus = "Reconnecting...";

            try
            {
                var creds = _credentialsStore.Load();
                var result = await KlothoConnectionAsync.ReconnectAsync(
                    _transport, creds, ct, _logger);

                _simCallbacks = new BrawlerSimulationCallbacks(
                    _input,
                    _logger,
                    _staticColliders,
                    _navMesh,
                    _brawlerSettings._maxPlayers,
                    _brawlerSettings._botCount
                );
                _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
                _session = KlothoSession.Create(new KlothoSessionSetup
                {
                    Connection = result,            // Kind == Reconnect → Create branches automatically
                    Logger = _logger,
                    SimulationCallbacks = _simCallbacks,
                    ViewCallbacks = _viewCallbacks,
                    AssetRegistry = _assetRegistry,
                    RoomId = creds.RoomId,          // SD multi-room restore; -1 for P2P / SD single room
                });
                _simCallbacks.SetNetworkService(_session.NetworkService);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ConnectPhysicsProvider();
#endif
                InjectCredentialsStoreIntoSession();

                _session.NetworkService.OnReconnecting += OnReconnecting;
                _session.NetworkService.OnReconnectFailed += OnReconnectFailed;
                _session.NetworkService.OnReconnected += OnReconnected;
                _session.Engine.OnCatchupComplete += OnLateJoinActive;
                _session.Engine.OnResyncCompleted += OnResyncCompleted;
                _session.Engine.OnGameStart += InjectInitialStateSnapshot;

                // Re-send PlayerConfig on reconnect to force-sync the last selection (intent).
                // Even if the host/server preserves the previous PlayerConfig, the client is authoritative — idempotency assumed.
                _session.SendPlayerConfig(new BrawlerPlayerConfig
                {
                    SelectedCharacterClass = _brawlerSettings._characterClass,
                });

                InitializeViewSync(_session.Engine, _session.Simulation);

                _gameMenu.ReconnectStatus = null;
                // ActionType transition is delegated to OnLateJoinActive (catchup completion callback).
                // The "Cancel" label remains visible during catchup; OnLateJoinActive switches to Playing.
            }
            catch (OperationCanceledException)
            {
                // Cancel keeps credentials — next boot can auto-retry.
                _logger?.ZLogWarning($"[Brawler] Reconnect canceled");
                _gameMenu.ReconnectStatus = null;
                FallbackToInitial();
            }
            catch (Exception e)
            {
                _logger?.ZLogError(e, $"[Brawler] Reconnect failed");
                HandleReconnectFailure(e);
            }
        }

        private void SetReady()
        {
            _logger?.ZLogInformation($"[Brawler] Ready");
            _session?.SetReady(true);
        }

        private void StartReplay()
        {
            if (Phase != SessionPhase.None && Phase != SessionPhase.Disconnected)
                return;

            _logger?.ZLogInformation($"[Brawler] Replay started");

            // (1) Load the file using a temporary ReplaySystem (without Engine, just pre-read metadata)
            var loader = new xpTURN.Klotho.Replay.ReplaySystem(new CommandFactory(), _logger);
            loader.LoadFromFile(_replayPath);
            var replayData = loader.CurrentReplayData;
            if (replayData == null)
            {
                _logger?.ZLogError($"[Brawler] Replay load failed: {_replayPath}");
                return;
            }

            // Restore SimulationConfig.
            var simConfig = replayData.Metadata.ToSimulationConfig();

            // For the replay path, maxPlayers/botCount are meaningless to inject into BrawlerSimulationCallbacks
            // because bot entities are already included via InitialStateSnapshot restoration.
            // Use the Inspector values for consistency (same source as the live path).
            // (2) Create a Session with the restored SimulationConfig
            _simCallbacks = new BrawlerSimulationCallbacks(
                _input,
                _logger,
                _staticColliders,
                _navMesh,
                _brawlerSettings._maxPlayers,
                _brawlerSettings._botCount
            );
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
            _session = KlothoSession.Create(new KlothoSessionSetup
            {
                Transport = _transport,
                Logger = _logger,
                SimulationCallbacks = _simCallbacks,
                ViewCallbacks = _viewCallbacks,
                AssetRegistry = _assetRegistry,
                SimulationConfig = simConfig,
            });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ConnectPhysicsProvider();
#endif
            // (3) Inject the loaded replayData into the Engine — internally _randomSeed = metadata.RandomSeed is set automatically
            _session.Engine.StartReplay(replayData);

            int maxPlayers = replayData.Metadata.PlayerCount;
            InitializeViewSync(_session.Engine, _session.Simulation, maxPlayers);
            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        // ── Spectator entry (deferred Engine creation pattern) ──
        //
        // StartSpectator only prepares SpectatorService and transport. Engine/Simulation
        // is created in CreateSpectatorRuntime using the server config values once
        // SpectatorAcceptMessage arrives and both Config events have been received.
        // Thanks to this structure, the spectator engine is initialized with server-authoritative values rather than the default config.
        private void StartSpectator()
        {
            if (Phase != SessionPhase.None && Phase != SessionPhase.Disconnected)
                return;

            _logger?.ZLogInformation($"[Brawler] Spectator connecting to {_brawlerSettings._hostAddress}:{_brawlerSettings._port}");

            // Engine/Simulation are created from the server config after SpectatorAcceptMessage is received.
            _spectatorCommandFactory = new CommandFactory();
            _pendingSpectatorSimConfig = null;
            _pendingSpectatorSessionConfig = null;

            var spectatorTransport = new LiteNetLibTransport(_logger);
            var spectatorService = new SpectatorService();
            spectatorService.SetLogger(_logger);
            spectatorService.Initialize(spectatorTransport, _spectatorCommandFactory, null, _logger);

            // Create the Engine once both Config events have arrived.
            spectatorService.OnSimulationConfigReceived += OnSpectatorSimConfigReceived;
            spectatorService.OnSessionConfigReceived += OnSpectatorSessionConfigReceived;

            int spectatorRoomId = _brawlerSettings._roomId;
            spectatorService.Connect(_brawlerSettings._hostAddress, _brawlerSettings._port, spectatorRoomId);

            _spectatorService = spectatorService;
        }

        private void OnSpectatorSimConfigReceived(ISimulationConfig cfg)
        {
            _pendingSpectatorSimConfig = cfg;
            TryCreateSpectatorRuntime();
        }

        private void OnSpectatorSessionConfigReceived(ISessionConfig cfg)
        {
            _pendingSpectatorSessionConfig = cfg;
            TryCreateSpectatorRuntime();
        }

        private void TryCreateSpectatorRuntime()
        {
            // The Engine is created only when both Configs have arrived. They fire concurrently
            // from the Accept message, but an explicit guard on the subscriber side avoids dependence on firing order.
            if (_pendingSpectatorSimConfig == null || _pendingSpectatorSessionConfig == null)
                return;
            if (_spectatorEngine != null)
                return; // Guard against duplicate creation

            CreateSpectatorRuntime(_pendingSpectatorSimConfig, _pendingSpectatorSessionConfig);
        }

        private void CreateSpectatorRuntime(ISimulationConfig simCfg, ISessionConfig sessionCfg)
        {
            // For maxPlayers, prefer the server-authoritative SessionConfig.MaxPlayers.
            _simCallbacks = new BrawlerSimulationCallbacks(
                _input,
                _logger,
                _staticColliders,
                _navMesh,
                sessionCfg.MaxPlayers,
                _brawlerSettings._botCount
            );
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

            var simulation = new EcsSimulation(
                maxEntities: simCfg.MaxEntities,
                maxRollbackTicks: simCfg.MaxRollbackTicks,
                deltaTimeMs: simCfg.TickIntervalMs,
                assetRegistry: _assetRegistry);
            _simCallbacks.RegisterSystems(simulation);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ConnectPhysicsProvider();
#endif

            var engine = new KlothoEngine(simCfg, sessionCfg);
            engine.Initialize(simulation, _logger);
            engine.SetCommandFactory(_spectatorCommandFactory);

            _spectatorService.SetEngine(engine);

            _spectatorService.OnSpectatorStarted += info => engine.StartSpectator(info);
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => engine.ReceiveConfirmedCommand(cmd);
            _spectatorService.OnTickConfirmed += tick => engine.ConfirmSpectatorTick(tick);
            _spectatorService.OnFullStateReceived += (tick, stateData, stateHash) =>
            {
                simulation.RestoreFromFullState(stateData);
                engine.ResetToTick(tick);
            };

            _spectatorEngine = engine;
            _spectatorSimulation = simulation;

            InitializeViewSync(engine, simulation, sessionCfg.MaxPlayers);
            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        private void InitializeViewSync(KlothoEngine engine, EcsSimulation simulation, int maxPlayers = -1)
        {
            if (maxPlayers < 0) maxPlayers = _brawlerSettings._maxPlayers;
            _viewSync.Initialize(engine, simulation, maxPlayers, _logger);
            _viewSync.OnLocalCharacterSpawned += OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned += OnLocalCharacterDespawned;

            // Initialize EVU. If the Factory fails to find an EntityView in the prefab, it becomes a no-op.
            // Must be called after engine.Start / StartSpectator / StartReplay has completed.
            _entityViewUpdater?.Initialize(engine);

#if UNITY_EDITOR
            if (xpTURN.Klotho.Unity.EcsDebugBridge.Instance != null)
            {
                xpTURN.Klotho.Unity.EcsDebugBridge.Instance.Register(simulation);
                xpTURN.Klotho.Unity.EcsDebugBridge.Instance.RegisterNavMesh(_simCallbacks.NavMesh, _simCallbacks.NavQuery);
                xpTURN.Klotho.Unity.EcsDebugBridge.Instance.RegisterNavAgentProvider(_simCallbacks.BotFSMSystem);
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void ConnectPhysicsProvider()
        {
            var visualizer = UnityEngine.Object.FindAnyObjectByType<xpTURN.Klotho.Unity.Physics.FPPhysicsWorldVisualizer>();
            if (visualizer != null)
                visualizer.Provider = BrawlerSimSetup.PhysicsSystem;
        }
#endif

        private void OnLocalCharacterSpawned()
        {
            _logger?.ZLogInformation($"[Brawler] Local Character Spawned");

            if (_simCallbacks != null)
                _simCallbacks.Spawned = true;
        }

        private void OnLocalCharacterDespawned()
        {
            _logger?.ZLogInformation($"[Brawler] Local Character Despawned");

            if (_simCallbacks == null || _session?.Engine == null) return;
            _simCallbacks.Spawned = false;
            _viewCallbacks.Respawn(_session.Engine);
        }

        private void StopGame()
        {
            _logger?.ZLogInformation($"[Brawler] Game stopped");

            // Cancel any in-progress JoinGameAsync
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;

            var engine = ActiveEngine;

            _viewSync.OnLocalCharacterSpawned -= OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned -= OnLocalCharacterDespawned;
            _viewSync.Cleanup();

            // Unsubscribe EVU's engine subscription and clean up active views.
            _entityViewUpdater?.Cleanup();

            if (_simCallbacks != null)
                _simCallbacks.Spawned = false;

            if (_session != null)
            {
                var net = _session.NetworkService;
                net.OnPlayerDisconnected -= OnPlayerDisconnected;
                net.OnPlayerReconnected -= OnPlayerReconnected;
                net.OnReconnecting -= OnReconnecting;
                net.OnReconnectFailed -= OnReconnectFailed;
                net.OnReconnected -= OnReconnected;
            }

            if (_session?.Engine != null)
            {
                _session.Engine.OnCatchupComplete -= OnLateJoinActive;
                _session.Engine.OnResyncCompleted -= OnResyncCompleted;
                _session.Engine.OnGameStart -= InjectInitialStateSnapshot;
            }

            _gameMenu.ReconnectStatus = null;

            _session?.Stop();
            _session = null;

            // Do not save during replay playback — playback does not record commands, so overwriting could corrupt the original file
            if (engine != null && !engine.IsReplayMode)
                engine.SaveReplayToFile(_replayPath, true);

            _spectatorEngine?.Stop();
            _spectatorEngine = null;
            _spectatorSimulation = null;
            if (_spectatorService != null)
            {
                _spectatorService.OnSimulationConfigReceived -= OnSpectatorSimConfigReceived;
                _spectatorService.OnSessionConfigReceived -= OnSpectatorSessionConfigReceived;
                _spectatorService = null;
            }
            _spectatorCommandFactory = null;
            _pendingSpectatorSimConfig = null;
            _pendingSpectatorSessionConfig = null;

            _lastTicks = 0;

            _transport?.Disconnect();
        }

        // ────────────────────────────────────────────
        // Reconnection
        // ────────────────────────────────────────────

        private void InjectCredentialsStoreIntoSession()
        {
            if (_session.NetworkService is KlothoNetworkService p2p)
                p2p.SetReconnectCredentialsStore(_credentialsStore, Application.version);
            else if (_session.NetworkService is ServerDrivenClientService sd)
                sd.SetReconnectCredentialsStore(_credentialsStore, Application.version);
        }

        // Generalized fallback used by Cancel / failure / mode-toggle paths.
        // Same pattern as OnDisconnected — pick CreateRoom or JoinRoom by current _isHost.
        private void FallbackToInitial()
        {
            _gameMenu.SetActionType(_brawlerSettings._isHost
                ? GameMenu.ActionType.CreateRoom
                : GameMenu.ActionType.JoinRoom);
            _transport?.Disconnect();
        }

        private void HandleReconnectFailure(System.Exception e)
        {
            _credentialsStore.Clear();
            byte reason = ParseRejectReason(e.Message);

            if (reason == ReconnectRejectReason.AlreadyConnected)
            {
                _logger?.ZLogWarning($"[Brawler] Reconnect rejected: AlreadyConnected — another device holds this PlayerId");
            }

            _gameMenu.ReconnectStatus = ToUserMessage(reason);
            FallbackToInitial();
        }

        private static byte ParseRejectReason(string msg)
        {
            const string PREFIX = "Reconnect rejected: ";
            if (msg == null || !msg.StartsWith(PREFIX)) return 0;
            string name = msg.Substring(PREFIX.Length);
            return name switch
            {
                "InvalidMagic"     => ReconnectRejectReason.InvalidMagic,
                "InvalidPlayer"    => ReconnectRejectReason.InvalidPlayer,
                "TimedOut"         => ReconnectRejectReason.TimedOut,
                "AlreadyConnected" => ReconnectRejectReason.AlreadyConnected,
                _                  => (byte)0,
            };
        }

        private static string ToUserMessage(byte reason) => reason switch
        {
            ReconnectRejectReason.InvalidMagic     => "Previous session has ended",
            ReconnectRejectReason.InvalidPlayer
            or ReconnectRejectReason.TimedOut      => "Reconnect timed out",
            ReconnectRejectReason.AlreadyConnected => "Already connected on another device",
            _                                       => "Reconnect failed",
        };

        private void OnPlayerDisconnected(IPlayerInfo player)
        {
            _logger?.ZLogWarning($"[Brawler] Player {player.PlayerId} disconnected, waiting for reconnection...");
            _gameMenu.ReconnectStatus = $"P{player.PlayerId} disconnected";
        }

        private void OnPlayerReconnected(IPlayerInfo player)
        {
            _logger?.ZLogInformation($"[Brawler] Player {player.PlayerId} reconnected");
            _gameMenu.ReconnectStatus = null;
        }

        private void OnReconnecting()
        {
            _logger?.ZLogWarning($"[Brawler] Disconnected, reconnecting...");
            _gameMenu.ReconnectStatus = "Reconnecting...";
        }

        private void OnReconnectFailed(string reason)
        {
            _logger?.ZLogError($"[Brawler] Reconnection failed: {reason}");
            _gameMenu.ReconnectStatus = null;
            StopGame();
            _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        private void OnReconnected()
        {
            _logger?.ZLogInformation($"[Brawler] Reconnected successfully");
            _gameMenu.ReconnectStatus = null;
        }

        private void OnLateJoinActive()
        {
            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        private void OnResyncCompleted(int tick)
        {
            _logger?.ZLogInformation($"[Brawler] Resync completed at tick={tick}");
        }

        // Inject the initial state snapshot for replay recording — StartRecording is already complete by OnGameStart.
        // P2P Host/Guest + SD Server: call local SerializeFullStateWithHash
        // SD Client: skipped here because the library injects directly via the server broadcast receive path (⑤-16)
        private void InjectInitialStateSnapshot()
        {
            var engine = _session?.Engine;
            if (engine != null && engine.IsReplayMode) return;   // No need to capture a new snapshot during replay playback (silently skip)

            var replaySystem = engine?.ReplaySystem;
            if (replaySystem == null || !replaySystem.IsRecording)
            {
                _logger?.ZLogWarning($"[Brawler] InjectInitialStateSnapshot skipped (replaySystem={replaySystem != null}, IsRecording={replaySystem?.IsRecording})");
                return;
            }

            // For SD Client, the library calls SetInitialStateSnapshot directly when it receives the server broadcast
            // Do not call locally in advance — this prevents double calls / unnecessary serialization
            bool isSdClient = engine.SimulationConfig.Mode == NetworkMode.ServerDriven && !engine.IsServer;
            if (isSdClient)
            {
                _logger?.ZLogInformation($"[Brawler] SD Client — InitialStateSnapshot skipped (server broadcast will provide)");
                return;
            }

            var sim = (EcsSimulation)_session.Simulation;
            var (data, hash) = sim.SerializeFullStateWithHash();
            replaySystem.SetInitialStateSnapshot(data, hash);
            _logger?.ZLogInformation($"[Brawler] Replay InitialStateSnapshot injected: size={data.Length}, hash=0x{hash:X16}");
        }

        // ────────────────────────────────────────────
        // Input
        // ────────────────────────────────────────────

        private FPVector2 GetFacingAimDirection()
        {
            // Direction the character is facing (based on TransformComponent.Rotation)
            // Since Rotation = Atan2(aimDir.x, aimDir.y), invert: sin(rot)=x, cos(rot)=y
            var frame = ActiveSimulation?.Frame;
            if (frame != null)
            {
                int localId = ActiveEngine.LocalPlayerId;
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

        private void UpdateStatus()
        {
            if (_brawlerSettings._hostAddress != _gameMenu.IpAddress)
            {
                _brawlerSettings._hostAddress = _gameMenu.IpAddress;
            }

            _gameMenu.IsHost = _brawlerSettings._isHost;
            _gameMenu.State = State;
            _gameMenu.Tick = CurrentTick;
            _gameMenu.Players = Players;
            _gameMenu.Entities = Entities;
            _gameMenu.IsAllReady = AllPlayersReady;
            _gameMenu.Phase = Phase;
        }
    }
}
