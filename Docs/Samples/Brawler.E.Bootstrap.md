# Brawler Appendix E — Bootstrap Order

> Related: [Brawler.md](Brawler.md) §11 (Phase 8 — Callbacks & Session Wiring)
> Target: `BrawlerGameController` Awake → Start → HostGame / JoinGame flow + field-injection mapping
>
> ⚠️ **Note**: The code in this appendix is a condensed view of the actual `BrawlerGameController` structure. Refer to the real source for cancellation-token handling, async exception paths, and Ready-transition details. Method names, signatures, and event names match the actual source.

---

## E-1. End-to-End Initialization Flow

```
┌──────────────────────────────────────────────────────────────┐
│ [Unity scene loads]                                          │
│                                                              │
│ BrawlerGameController.Awake()                                │
│   • DontDestroyOnLoad                                        │
│   • CreateLogger()                                           │
│                                                              │
│ BrawlerGameController.Start()                                │
│   • FPStaticColliderSerializer.Load(_staticCollidersAsset)   │
│   • FPNavMeshSerializer.Deserialize(_navMeshAsset)           │
│   • DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset) │
│   • DataAssetRegistry.Build()                                │
│   • new LiteNetLibTransport(_logger)                         │
│   • new BrawlerInputCapture() + Enable()                     │
│                                                              │
│ [Wait — GameMenu button input]                               │
│                                                              │
│ ┌─ StartHost() ────┐          ┌─ JoinGame() ──────┐          │
│ │ Build SessionSetup          │ JoinGameAsync     │          │
│ │ KlothoSession.Create        │   (async connect / handshake)│
│ │ SendPlayerConfig │          │ Build SessionSetup│          │
│ │ Transport.Listen │          │ KlothoSession.Create         │
│ │ HostGame(name)   │          │ SendPlayerConfig  │          │
│ └──────────────────┘          │ session.JoinGame  │          │
│                               └───────────────────┘          │
│                                                              │
│ [Game loop starts]                                           │
│   • UKlothoBehaviour.Update → session.Update(dt)             │
│   • ISimulationCallbacks.OnInitializeWorld (once, in Start)  │
│   • IViewCallbacks.OnGameStart (once, on game start)         │
│   • Per tick: OnPollInput → Simulation.Tick → OnTickExecuted │
└──────────────────────────────────────────────────────────────┘
```

---

## E-2. BrawlerGameController Field Layout

```csharp
public class BrawlerGameController : MonoBehaviour
{
    [Header("Assets")]
    [SerializeField] private TextAsset _staticCollidersAsset;   // StaticColliders.bytes
    [SerializeField] private TextAsset _navMeshAsset;           // NavMeshData.bytes
    [SerializeField] private TextAsset _dataAsset;              // DataAssets.bytes (bundle of 9)
    [SerializeField] private USimulationConfig _simulationConfig;

    [Header("Runtime Components")]
    [SerializeField] private UKlothoBehaviour _uKlotho;
    [SerializeField] private BrawlerViewSync _viewSync;
    [SerializeField] private EntityViewUpdater _viewUpdater;
    [SerializeField] private GameMenu _gameMenu;

    [Header("Game Settings")]
    [SerializeField] private BrawlerSettings _brawlerSettings;

    // Runtime state
    private List<FPStaticCollider> _staticColliders;
    private FPNavMesh _navMesh;
    private List<IDataAsset> _dataAssets;
    private IDataAssetRegistry _assetRegistry;

    private LiteNetLibTransport _transport;
    private BrawlerInputCapture _input;
    private ILogger _logger;

    private BrawlerSimulationCallbacks _simCallbacks;
    private BrawlerViewCallbacks _viewCallbacks;
    private KlothoSession _session;
}

[Serializable]
public class BrawlerSettings
{
    public bool _isHost = true;
    public NetworkMode _mode = NetworkMode.P2P;   // P2P | ServerDriven
    public int _maxPlayers = 2;
    public int _botCount = 0;
    public int _characterClass = 0;               // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
    public string _hostAddress = "0.0.0.0";
    public int _port = 7777;
    public int _roomId = -1;                      // For ServerDriven multi-room (-1: single room)
}
```

---

## E-3. Awake / Start

### Awake()

```csharp
private void Awake()
{
    DontDestroyOnLoad(gameObject);
    _logger = LoggerFactory.Create(b => b
        .SetMinimumLevel(LogLevel.Information)
        .AddZLoggerUnityDebug())
        .CreateLogger("Klotho");
}
```

### Start()

```csharp
private void Start()
{
    // 1) Load static assets
    _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
    _navMesh = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
    _dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

    // 2) Build the DataAssetRegistry (lockAfterBuild=true is the default)
    IDataAssetRegistryBuilder builder = new DataAssetRegistry();
    builder.RegisterRange(_dataAssets);
    _assetRegistry = builder.Build();

    // 3) Network transport + input capture
    _transport = new LiteNetLibTransport(_logger);
    _transport.OnDisconnected += OnDisconnected;

    _input = new BrawlerInputCapture();
    _input.Enable();

    // 4) Register GameMenu callbacks
    _gameMenu.OnHostClicked  += StartHost;
    _gameMenu.OnGuestClicked += StartGuest;
    _gameMenu.OnReplayClicked += StartReplay;
}
```

---

## E-4. StartHost — Host Flow

```csharp
private void StartHost()
{
    // 1) Create the two callback objects (SimulationCallbacks holds internal state)
    _simCallbacks = new BrawlerSimulationCallbacks(
        _input,
        _logger,
        _staticColliders,
        _navMesh,
        _brawlerSettings._maxPlayers,
        _brawlerSettings._botCount,
        _dataAssets);     // copy used to rebuild AssetRegistry

    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

    // 2) Create the session — assemble KlothoSessionSetup
    _session = KlothoSession.Create(new KlothoSessionSetup {
        Transport           = _transport,
        Logger              = _logger,
        SimulationCallbacks = _simCallbacks,
        ViewCallbacks       = _viewCallbacks,
        AssetRegistry       = _assetRegistry,
        SimulationConfig    = _simulationConfig,   // USimulationConfig (SO)
        MaxPlayers          = _brawlerSettings._maxPlayers,
        AllowLateJoin       = true,
    });

    // 3) Inject back-reference into SimulationCallbacks (OnPollInput needs NetworkService)
    _simCallbacks.SetNetworkService(_session.NetworkService);

    // 4) Bind the session to UKlothoBehaviour → per-frame Update is automatic
    _uKlotho.Bind(_session);

    // 5) Initialize EntityViewUpdater (scene tool injects Engine)
    _viewUpdater.Initialize(_session.Engine);

    // 6) Subscribe to network-service events
    _session.NetworkService.OnPlayerDisconnected += OnPlayerDisconnected;
    _session.NetworkService.OnPlayerReconnected  += OnPlayerReconnected;

    // 7) Start the host game + transport listen
    _session.HostGame("Game", _brawlerSettings._maxPlayers);
    _transport.Listen(
        _brawlerSettings._hostAddress,
        _brawlerSettings._port,
        _brawlerSettings._maxPlayers);

    // 8) Send the local player's config
    _session.SendPlayerConfig(new BrawlerPlayerConfig {
        SelectedCharacterClass = _brawlerSettings._characterClass,
    });
}
```

---

## E-5. JoinGame — Guest Flow (async)

The guest entry is structured so that `JoinGame()` (the synchronous entry point) calls `JoinGameAsync(ct).Forget()`. A `CancellationTokenSource` (`_connectCts`) allows mid-flight cancellation.

```csharp
private void JoinGame()
{
    _connectCts = new CancellationTokenSource();
    JoinGameAsync(_connectCts.Token).Forget();
}

private async UniTaskVoid JoinGameAsync(CancellationToken ct)
{
    try
    {
        // 1) Transport connect + handshake → ConnectionResult
        _transport.Connect(_brawlerSettings._hostAddress, _brawlerSettings._port);
        var connection = await /* handshake await logic */; // SyncRequest/SyncReply/SyncComplete

        ct.ThrowIfCancellationRequested();

        // 2) Create callbacks (same pattern as the host)
        _simCallbacks = new BrawlerSimulationCallbacks(
            _input, _logger, _staticColliders, _navMesh,
            _brawlerSettings._maxPlayers, 0, _dataAssets);
        _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

        // 3) Create the session — when Connection is set, Transport/SimulationConfig are taken from Connection
        _session = KlothoSession.Create(new KlothoSessionSetup {
            Logger              = _logger,
            SimulationCallbacks = _simCallbacks,
            ViewCallbacks       = _viewCallbacks,
            Connection          = connection,          // ← guest path
            AssetRegistry       = _assetRegistry,
        });

        _simCallbacks.SetNetworkService(_session.NetworkService);
        _uKlotho.Bind(_session);
        _viewUpdater.Initialize(_session.Engine);

        // 4) Send the local player's config
        _session.SendPlayerConfig(new BrawlerPlayerConfig {
            SelectedCharacterClass = _brawlerSettings._characterClass,
        });

        // 5) Join the room
        _session.JoinGame("Game");
    }
    catch (OperationCanceledException) { /* canceled — cleanup */ }
    catch (Exception e) {
        _logger?.ZLogError(e, $"[Brawler] JoinGame failed");
    }
}
```

---

## E-6. BrawlerSimulationCallbacks — Fields & Constructor

```csharp
public class BrawlerSimulationCallbacks : ISimulationCallbacks
{
    private readonly BrawlerInputCapture _input;
    private readonly ILogger _logger;
    private readonly List<FPStaticCollider> _staticColliders;
    private readonly FPNavMesh _navMesh;
    private readonly int _maxPlayers;
    private readonly int _botCount;
    private readonly List<IDataAsset> _dataAssets;

    private IKlothoNetworkService _networkService;
    private IKlothoEngine _engine;

    public BotFSMSystem BotFSMSystem { get; private set; }
    public FPNavMeshQuery NavQuery  { get; private set; }

    public BrawlerSimulationCallbacks(
        BrawlerInputCapture input, ILogger logger,
        List<FPStaticCollider> colliders, FPNavMesh navMesh,
        int maxPlayers, int botCount,
        List<IDataAsset> dataAssets = null)
    {
        _input = input; _logger = logger;
        _staticColliders = colliders; _navMesh = navMesh;
        _maxPlayers = maxPlayers; _botCount = botCount;
        _dataAssets = dataAssets;
    }

    public void SetNetworkService(IKlothoNetworkService svc) => _networkService = svc;
    public void SetEngine(IKlothoEngine engine) => _engine = engine;

    public void RegisterSystems(EcsSimulation simulation)
    {
        // 1) Wire NavMesh queries / bot system
        var query       = new FPNavMeshQuery(_navMesh, null);
        var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, null);
        var funnel      = new FPNavMeshFunnel(_navMesh, query, null);
        var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, _logger);
        agentSystem.SetAvoidance(new FPNavAvoidance());

        BotFSMSystem = new BotFSMSystem(agentSystem);
        BotFSMSystem.SetQuery(query);
        NavQuery = query;

        // 2) Build the HFSM (DataAsset references)
        var behavior = GetAsset<BotBehaviorAsset>(1600);
        var diff     = new[] { GetAsset<BotDifficultyAsset>(1700), GetAsset<BotDifficultyAsset>(1701), GetAsset<BotDifficultyAsset>(1702) };
        var attack   = GetAsset<BasicAttackConfigAsset>(1301);
        var skills   = LoadSkillMatrix();   // [class][slot]
        BotHFSMRoot.Build(behavior, diff, attack, skills);

        // 3) Register sample systems
        BrawlerSimSetup.RegisterSystems(simulation, _logger, _dataAssets, _staticColliders, BotFSMSystem);
    }

    public void OnInitializeWorld(IKlothoEngine engine)
    {
        SetEngine(engine);
        BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
    }

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        if (playerId != _engine.LocalPlayerId) return;  // local player only

        _input.CaptureInput();
        var moveCmd = CommandPool.Get<MoveInputCommand>();
        moveCmd.HorizontalAxis = _input.H;
        moveCmd.VerticalAxis   = _input.V;
        moveCmd.JumpPressed    = _input.Jump;
        moveCmd.JumpHeld       = _input.JumpHeld;
        sender.Send(moveCmd);

        if (_input.Attack) {
            var atk = CommandPool.Get<AttackCommand>();
            atk.AimDirection = _input.AimDirection;
            sender.Send(atk);
        }

        if (_input.SkillSlot >= 0) {
            var sk = CommandPool.Get<UseSkillCommand>();
            sk.SkillSlot = _input.SkillSlot;
            sk.AimDirection = _input.AimDirection;
            sender.Send(sk);
        }

        _input.ConsumeOneShot();
    }

    public void SendSpawnCommand(IKlothoEngine engine)
    {
        var cmd = CommandPool.Get<SpawnCharacterCommand>();
        cmd.CharacterClass = /* read from PlayerConfig */ 0;
        cmd.SpawnPosition  = /* BrawlerGameRulesAsset.SpawnPositions[LocalPlayerId] */;
        engine.InputCommand(cmd);
    }
}
```

---

## E-7. BrawlerViewCallbacks — Fields & Constructor

```csharp
public class BrawlerViewCallbacks : IViewCallbacks
{
    private readonly BrawlerSimulationCallbacks _sim;
    private readonly ILogger _logger;

    public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim, ILogger logger)
    {
        _sim = sim;
        _logger = logger;
    }

    public void OnGameStart(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        if (!engine.IsReplayMode)
            _sim.SendSpawnCommand(engine);
    }

    public void OnTickExecuted(int tick) { /* HUD updates: EVU + GameHUD subscribe directly */ }

    public void OnLateJoinActivated(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        _sim.SendSpawnCommand(engine);
    }
}
```

---

## E-8. Cross-Reference Caveats

- Before `KlothoSession.Create`, `BrawlerSimulationCallbacks._networkService` and `_engine` are null → `OnPollInput` needs a safety check.
- `BrawlerViewCallbacks` only obtains `engine` at `OnGameStart`. Don't use it before that.
- Call `EntityViewUpdater.Initialize(engine)` only after `_session.Engine` has reached the `Start()` state (right after Session.Create the engine is `Idle`, which is safe; but for a `StartReplay`-like branch, defer the call to after `StartReplay()`).
- After the registry is built, `DataAsset` is **immutable** — runtime additions are not allowed.

---

## E-9. Replay Bootstrap

Replay re-runs locally without networking. Use the same `KlothoSession.Create` static factory as the host path, but configure it without a Transport, then call `session.Engine.StartReplay(replayData)` to start playback.

```csharp
private void StartReplay()
{
    var replayBytes = File.ReadAllBytes(_brawlerSettings._replayPath);
    var replayData  = /* ReplayData.Deserialize(replayBytes) or ReplaySystem.LoadFromFile */;

    _simCallbacks  = new BrawlerSimulationCallbacks(_input, _logger, _staticColliders, _navMesh, 0, 0, _dataAssets);
    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

    _session = KlothoSession.Create(new KlothoSessionSetup {
        Logger              = _logger,
        SimulationCallbacks = _simCallbacks,
        ViewCallbacks       = _viewCallbacks,
        AssetRegistry       = _assetRegistry,
        SimulationConfig    = _simulationConfig,
        // Transport / Connection omitted — replay is local-only
    });

    _uKlotho.Bind(_session);
    _viewUpdater.Initialize(_session.Engine);

    _session.Engine.StartReplay(replayData);   // LZ4 decompression is handled automatically on load
}
```

> **Correction**: `KlothoSession.CreateForReplay` / `StartReplayFromFile` do not exist. Replay is launched via the combination `KlothoSession.Create` + `Engine.StartReplay(IReplayData)`.
