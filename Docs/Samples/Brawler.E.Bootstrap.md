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
│   • CreateLogger()  → builds LoggerFactory, attaches rolling │
│                       file + UnityDebug sinks                │
│                                                              │
│ BrawlerGameController.Start()                                │
│   • Pre-load: StaticColliders, NavMesh, DataAssets, Registry │
│   • new PlayerPrefsReconnectCredentialsStore()               │
│   • new LiteNetLibTransport(_logger, …)                      │
│   • new BrawlerInputCapture() + Enable()                     │
│   • Mode-specific roomId reset (P2P → -1)                    │
│   • TryAutoReconnect() — cold-start ReconnectAsync if creds  │
│   • GameMenu.SetActionType(CreateRoom / JoinRoom)            │
│   • [KLOTHO_FAULT_INJECTION] ApplyFaultInjection()           │
│                                                              │
│ [Wait — GameMenu button input]                               │
│                                                              │
│ ┌─ StartHost() ────┐  ┌─ JoinGame() ─────────────┐           │
│ │ Build P2P SimCfg │  │ JoinGameAsync (UniTask)  │           │
│ │ new SimCallbacks │  │  KlothoConnectionAsync   │           │
│ │ new ViewCallbacks│  │    .ConnectAsync(...)    │           │
│ │ KlothoSession    │  │  new SimCallbacks        │           │
│ │   .Create        │  │  new ViewCallbacks       │           │
│ │ HostGame +       │  │  KlothoSession.Create    │           │
│ │   Transport.Listen  │    (Connection = result) │           │
│ │ SendPlayerConfig │  │  Subscribe Reconnect /   │           │
│ │ InitializeViewSync  │   Catchup / Resync evts  │           │
│ └──────────────────┘  │  SendPlayerConfig        │           │
│                       │  InitializeViewSync      │           │
│                       └──────────────────────────┘           │
│                                                              │
│ [Game loop starts]                                           │
│   • Update() → session.Update(dt) (direct call)              │
│   • ISimulationCallbacks.OnInitializeWorld (once)            │
│   • Engine.OnGameStart → InjectInitialStateSnapshot          │
│   • IViewCallbacks.OnGameStart (once, on game start)         │
│   • Per tick: OnPollInput → Simulation.Tick → OnTickExecuted │
└──────────────────────────────────────────────────────────────┘
```

---

## E-2. BrawlerGameController Field Layout

```csharp
[DefaultExecutionOrder(-100)]
public class BrawlerGameController : MonoBehaviour
{
    const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

    [Header("Debug")]
    [SerializeField] private LogLevel _logLevel = LogLevel.Information;

    [Header("Settings")]
    [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
    [SerializeField] private USimulationConfig _simulationConfig;

    [Header("Scene References")]
    [SerializeField] private GameMenu _gameMenu;
    [SerializeField] private BrawlerViewSync _viewSync;
    // EVU reference. If the prefab has an EntityView, EVU auto-spawns it.
    // Inspector-null → EVU hook is skipped.
    [SerializeField] private xpTURN.Klotho.EntityViewUpdater _entityViewUpdater;

    [Header("Static Colliders")]
    [SerializeField] private TextAsset _staticCollidersAsset;
    [Header("NavMesh")]
    [SerializeField] private TextAsset _navMeshAsset;
    [Header("DataAssets")]
    [SerializeField] private TextAsset _dataAsset;

    // Runtime state
    private ILogger _logger;
    private List<FPStaticCollider> _staticColliders;
    private FPNavMesh _navMesh;
    private List<IDataAsset> _dataAssets;
    private IDataAssetRegistry _assetRegistry;

    private KlothoSession _session;
    private LiteNetLibTransport _transport;
    private Camera _mainCamera;
    private CancellationTokenSource _connectCts;          // cancels in-flight JoinGameAsync / ReconnectAsync
    private IReconnectCredentialsStore _credentialsStore; // PlayerPrefs-backed cold-start credentials

    private BrawlerInputCapture _input;
    private BrawlerSimulationCallbacks _simCallbacks;
    private BrawlerViewCallbacks _viewCallbacks;

    // Spectator mode (no KlothoSession — engine/sim built directly)
    private SpectatorService _spectatorService;
    private KlothoEngine _spectatorEngine;
    private EcsSimulation _spectatorSimulation;
    private CommandFactory _spectatorCommandFactory;
    private ISimulationConfig _pendingSpectatorSimConfig;
    private ISessionConfig _pendingSpectatorSessionConfig;

    private long _lastTicks;
    private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

#if KLOTHO_FAULT_INJECTION
    // RTT spike schedule anchor — set on the first frame Phase enters Playing.
    // Per-client drift = each client's GameStartMessage receive jitter.
    private float _rttScheduleAnchorTime = -1f;
    private int   _rttScheduleNextIdx;
#endif

    public bool IsHost => _brawlerSettings._isHost;
    public SessionPhase Phase => _session?.NetworkService?.Phase ?? SessionPhase.None;
    private KlothoEngine    ActiveEngine     => _session?.Engine     ?? _spectatorEngine;
    private EcsSimulation   ActiveSimulation => _session?.Simulation ?? _spectatorSimulation;
}

[Serializable]
public class BrawlerSettings
{
    [Header("ServerSettings")]
    [SerializeField] public NetworkMode _mode = NetworkMode.ServerDriven;
    [SerializeField] public string _hostAddress = "localhost";
    [SerializeField] public int _port = 777;

    [Header("ServerDriven")]
    [SerializeField] public int _roomId = 0;          // SD: 0 = single room / N = multi-room slot. P2P: forced to -1 at Start()

    [Header("P2P")]
    [SerializeField] public bool _isHost = true;
    [SerializeField] public int _maxPlayers = 2;
    [SerializeField] public int _botCount = 0;

    [Header("PlayerSettings")]
    [SerializeField] public int _characterClass = 0;  // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
}
```

Notes:
- The Unity-side update driver is **not** `UKlothoBehaviour` — `BrawlerGameController.Update()` calls `_session.Update(dt)` directly (or `_spectatorEngine.Update(dt)` in spectator mode). `UKlothoBehaviour` still exists in `Assets/Klotho/Unity/ULockstepBehaviour.cs` but is unused by this sample.
- `_entityViewUpdater` is the EntityViewUpdater field name (renamed from the older `_viewUpdater`); its setup runs via `InitializeViewSync(engine, simulation)` rather than a direct `Initialize(engine)` call.
- `_credentialsStore` underpins cold-start auto-reconnect (`Start()` → `TryAutoReconnect()` → `ReconnectAsync(ct)`).

---

## E-3. Awake / Start

### Awake()

```csharp
private void Awake()
{
    DontDestroyOnLoad(gameObject);
    CreateLogger();   // factory + UnityDebug + rolling file sinks
}

private void CreateLogger()
{
    var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(_logLevel);
        b.AddZLoggerUnityDebug();
        b.AddZLoggerRollingFile(opt =>
        {
            opt.FilePathSelector = (dt, idx) =>
                $"Logs/Client_{dt:yyyy-MM-dd-HH-mm-ss-fff}_{idx:000}.log";
            opt.RollingInterval  = RollingInterval.Day;
            opt.RollingSizeKB    = 1024 * 1024;
            opt.UsePlainTextFormatter(/* prefix + exception formatters */);
        });
    });
    _logger = loggerFactory.CreateLogger("Client");
}
```

### Start()

```csharp
private void Start()
{
    // 1) Pre-load static assets
    _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
    _navMesh         = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
    _dataAssets      = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(_dataAssets);
    _assetRegistry = registryBuilder.Build();

    _mainCamera       = Camera.main;
    _credentialsStore = new PlayerPrefsReconnectCredentialsStore();

    // 2) Transport — connectionKey gates non-Brawler clients at the LiteNetLib layer
    var logLevels = new[] { LiteNetLib.NetLogLevel.Warning, LiteNetLib.NetLogLevel.Error };
    _transport = new LiteNetLibTransport(_logger, logLevels, connectionKey: KLOTHO_CONNECTION_KEY);
    _transport.OnDisconnected += OnDisconnected;

    // 3) Input capture
    _input = new BrawlerInputCapture();
    _input.Enable();

    // 4) P2P uses _roomId = -1 by convention; SD keeps the Inspector value (0..N).
    if (_brawlerSettings._mode != NetworkMode.ServerDriven)
        _brawlerSettings._roomId = -1;

    _gameMenu.IsHost = IsHost;

    // 5) Cold-start auto-reconnect probe — SD clients and P2P guests only.
    //    P2P host's death ends the session, so it is never a reconnect target.
    bool isP2PHost = _brawlerSettings._mode == NetworkMode.P2P && _brawlerSettings._isHost;
    if (!isP2PHost && TryAutoReconnect())
        return;
    _gameMenu.SetActionType(_brawlerSettings._isHost
        ? GameMenu.ActionType.CreateRoom
        : GameMenu.ActionType.JoinRoom);

#if KLOTHO_FAULT_INJECTION
    ApplyFaultInjection();   // see E-10
#endif

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
```

GameMenu button wiring (`_btnHost / _btnGuest / _btnAction / _btnReplay / _btnSpectator`) is done in `OnEnable` (and torn down in `OnDisable`), not in `Start`. The action button dispatches to `StartHost()` / `JoinGame()` based on `_gameMenu.CurrentAction`.

---

## E-4. StartHost — Host Flow (P2P only)

```csharp
private void StartHost()
{
    _logger?.ZLogInformation($"[Brawler] Hosting game");

    // 1) Force the simulation-config Mode to P2P for the host path.
    //    Falls back to a default SimulationConfig if the Inspector field is null.
    ISimulationConfig simulationConfig;
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

    // 2) Build callbacks — dataAssets are NOT passed (registry is shared via AssetRegistry).
    _simCallbacks = new BrawlerSimulationCallbacks(
        _input, _logger, _staticColliders, _navMesh,
        _brawlerSettings._maxPlayers, _brawlerSettings._botCount);
    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

    // 3) Create the session
    _session = KlothoSession.Create(new KlothoSessionSetup
    {
        Transport           = _transport,
        Logger              = _logger,
        SimulationCallbacks = _simCallbacks,
        ViewCallbacks       = _viewCallbacks,
        AssetRegistry       = _assetRegistry,
        SimulationConfig    = simulationConfig,
        MaxPlayers          = _brawlerSettings._maxPlayers,
    });
    _simCallbacks.SetNetworkService(_session.NetworkService);   // no-op in current SimCallbacks

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    ConnectPhysicsProvider();                                   // editor-only physics debug hook
#endif

    // 4) Start host + transport listen. Early-return on bind failure.
    _session.HostGame("Game", _brawlerSettings._maxPlayers);
    if (!_transport.Listen(_brawlerSettings._hostAddress,
                           _brawlerSettings._port,
                           _brawlerSettings._maxPlayers))
    {
        _logger?.ZLogError($"[Brawler] Failed to host on port {_brawlerSettings._port}");
        StopGame();
        return;
    }

    // 5) Event wiring — host needs OnPlayerDisconnected/Reconnected + state-snapshot inject.
    _session.NetworkService.OnPlayerDisconnected += OnPlayerDisconnected;
    _session.NetworkService.OnPlayerReconnected  += OnPlayerReconnected;
    _session.Engine.OnGameStart += InjectInitialStateSnapshot;

    // 6) Broadcast the local player's character selection.
    _session.SendPlayerConfig(new BrawlerPlayerConfig
    {
        SelectedCharacterClass = _brawlerSettings._characterClass,
    });

    // 7) Wire the EntityViewUpdater + BrawlerViewSync to the freshly-created engine + sim.
    InitializeViewSync(_session.Engine, _session.Simulation);

    _gameMenu.SetActionType(GameMenu.ActionType.Ready);
}
```

Notes:
- The host path is P2P-only. SD does not use `StartHost()` — the dedicated server (Appendix H) is the SD authority.
- `BrawlerSimulationCallbacks` constructor's optional `dataAssets` parameter is intentionally left default (the asset registry is already shared via `AssetRegistry`).
- No `UKlothoBehaviour.Bind(...)` call: `BrawlerGameController.Update()` drives `_session.Update(dt)` directly (see E-1).

---

## E-5. JoinGame — Guest Flow (async)

`JoinGame()` (synchronous entry) cancels any in-flight token and dispatches `JoinGameAsync(ct).Forget()`. Both P2P and SD clients reach the server through the same `KlothoConnectionAsync` path; the only divergence is the `preJoin` message for SD multi-room routing.

```csharp
private void JoinGame()
{
    // Both P2P and SD (single / multi room) use the async path through KlothoConnection.
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
        // 1) SD path needs RoomHandshakeMessage before PlayerJoinMessage.
        //    RoomId = 0 for single-room, N for multi-room slot. -1 means P2P.
        NetworkMessageBase preJoin = null;
        int roomId = -1;
        if (_brawlerSettings._mode == NetworkMode.ServerDriven)
        {
            roomId  = _brawlerSettings._roomId;
            preJoin = new RoomHandshakeMessage { RoomId = roomId };
        }

        // 2) Connect + handshake. Returns ConnectionResult with SimulationConfig payload.
        var result = await KlothoConnectionAsync.ConnectAsync(
            _transport,
            _brawlerSettings._hostAddress, _brawlerSettings._port,
            ct, _logger, preJoin,
            deviceIdProvider: new UnityDeviceIdProvider());

        // 3) Build callbacks (same shape as host; dataAssets not passed).
        _simCallbacks = new BrawlerSimulationCallbacks(
            _input, _logger, _staticColliders, _navMesh,
            _brawlerSettings._maxPlayers, _brawlerSettings._botCount);
        _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

        // 4) Create the session — when Connection is set, Transport/SimulationConfig come from Connection.
        _session = KlothoSession.Create(new KlothoSessionSetup
        {
            Connection          = result,
            Logger              = _logger,
            SimulationCallbacks = _simCallbacks,
            ViewCallbacks       = _viewCallbacks,
            AssetRegistry       = _assetRegistry,
            RoomId              = roomId,
        });
        _simCallbacks.SetNetworkService(_session.NetworkService);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        ConnectPhysicsProvider();
#endif
        InjectCredentialsStoreIntoSession();   // wires _credentialsStore for warm reconnect save/clear

        // 5) Guest-side event wiring.
        _session.NetworkService.OnReconnecting   += OnReconnecting;
        _session.NetworkService.OnReconnectFailed += OnReconnectFailed;
        _session.NetworkService.OnReconnected     += OnReconnected;
        _session.Engine.OnCatchupComplete         += OnLateJoinActive;
        _session.Engine.OnResyncCompleted         += OnResyncCompleted;
        _session.Engine.OnGameStart               += InjectInitialStateSnapshot;

        _session.SendPlayerConfig(new BrawlerPlayerConfig
        {
            SelectedCharacterClass = _brawlerSettings._characterClass,
        });

        InitializeViewSync(_session.Engine, _session.Simulation);

        _gameMenu.ReconnectStatus = null;
        _gameMenu.SetActionType(GameMenu.ActionType.Ready);
    }
    catch (OperationCanceledException) { /* canceled — cleanup + back to JoinRoom UI */ }
    catch (Exception e)                 { _logger?.ZLogError(e, $"[Brawler] JoinGame failed"); }
}
```

`ReconnectAsync(ct)` mirrors this shape but calls `KlothoConnectionAsync.ReconnectAsync(_transport, creds, ct, _logger)` and reads `roomId` from the persisted credentials (`creds.RoomId`). The event subscriptions and `InitializeViewSync` step are identical.

---

## E-6. BrawlerSimulationCallbacks — Fields, State, Reactive Hooks

The callbacks class has grown to host two reactive-escalation paths and a state-driven spawn loop on top of its core responsibilities. Subsections below mirror the actual source layout.

### E-6-1. Fields & Constructor

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

    private IKlothoEngine _engine;

    // Spawn lifecycle — state-driven (ECS Frame query). No more "Spawned" latch.
    private int _lastSpawnAttemptTick = -1;
    private const int SpawnRetryInterval = 20;   // ~500ms @ 40Hz

    // Spawn cmd extra lead. Escalates by SPAWN_DELAY_STEP on each PastTick reject; latched until
    // the match boundary (BrawlerGameController re-news _simCallbacks).
    private int _extraSpawnDelay = 0;
    private const int SPAWN_DELAY_STEP = 4;       // ~100ms @ 40Hz
    private const int SPAWN_DELAY_MAX  = 40;      // ~1s cap — triggers one-shot Error + post-cap latch
    private bool _capHitLogged = false;
    private int  _capHitRejectCount = 0;

    // (F-3) Client-reactive PastTick fallback — engine-tick-based sliding window + post-push grace.
    private int _lastServerPushTick     = int.MinValue;
    private int _reactiveWindowStartTick = int.MinValue;
    private int _reactiveRejectCount     = 0;
    private const int SERVER_PUSH_GRACE_TICKS    = 40;   // ~1s — ignore rejects within grace of last server push
    private const int REACTIVE_WINDOW_TICKS      = 80;   // ~2s — reject-count reset
    private const int REACTIVE_ESCALATE_THRESHOLD = 3;
    private const int REACTIVE_STEP               = 4;
    private const int REACTIVE_MAX                = 40;  // matches SPAWN_DELAY_MAX

    // (F-4) Rollback-amplitude reactive — primary fallback for P2P guests (no CommandRejectedMessage).
    // Counts rollback events in a fixed window; baseline matches measure 0, RTT-spike matches multiple per 5s.
    private int _lastRollbackBurstWindowStartTick = int.MinValue;
    private int _rollbackCountInWindow             = 0;
    private int _lastReactiveEscalateTick          = int.MinValue;
    private const int ROLLBACK_BURST_COUNT              = 3;
    private const int ROLLBACK_WINDOW_TICKS             = 200;  // ~5s @ 40Hz
    private const int REACTIVE_ESCALATE_COOLDOWN_TICKS  = 80;   // ~2s minimum gap between escalations

    public FPNavMesh     NavMesh      => _navMesh;
    public FPNavMeshQuery NavQuery    { get; private set; }
    public BotFSMSystem  BotFSMSystem { get; private set; }

    public BrawlerSimulationCallbacks(BrawlerInputCapture input, ILogger logger,
                                      List<FPStaticCollider> colliders, FPNavMesh navMesh,
                                      int maxPlayers, int botCount,
                                      List<IDataAsset> dataAssets = null)
    {
        _input = input; _logger = logger;
        _staticColliders = colliders; _navMesh = navMesh;
        _maxPlayers = maxPlayers; _botCount = botCount;
        _dataAssets = dataAssets;     // currently always passed as default (registry is shared elsewhere)
    }

    // Retained as a no-op for API symmetry; bot spawn is decided by _botCount.
    public void SetNetworkService(IKlothoNetworkService _) { }
}
```

### E-6-2. Engine wiring — `SetEngine`

```csharp
public void SetEngine(IKlothoEngine engine)
{
    _engine = engine;
    engine.OnCommandRejected   += HandleCommandRejected;     // SD CommandRejectedMessage surface
    engine.OnExtraDelayChanged += HandleExtraDelayChanged;   // grace-window anchor refresh
    engine.OnRollbackExecuted  += HandleRollback;            // P2P guest rollback-burst fallback
}

public void OnInitializeWorld(IKlothoEngine engine)
{
    SetEngine(engine);
    BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
}

public void OnResyncCompleted(int _)
{
    // FullState resync reconstructs ECS — the previous spawn-attempt tick is no longer meaningful.
    _lastSpawnAttemptTick = -1;
}
```

### E-6-3. `OnPollInput` — state-driven spawn loop + input dispatch

```csharp
public void OnPollInput(int playerId, int tick, ICommandSender sender)
{
    if (_engine == null) return;

#if KLOTHO_FAULT_INJECTION
    // Force-retry path: bypass HasOwnCharacter so spawn cmd re-fires even after success.
    // Returns early so a Move/Attack send in the same poll does NOT overwrite the spawn cmd
    // in the InputBuffer (single command per (tick, playerId) slot).
    if (FaultInjection.ForceSpawnRetryPlayerIds.Contains(playerId)) { /* SendSpawnCommand + return */ }
#endif

    var frame = ((EcsSimulation)_engine.Simulation).Frame;
    if (!HasOwnCharacter(frame, playerId))
    {
        if (_lastSpawnAttemptTick < 0 || tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
            SendSpawnCommand(_engine);

        // Skip emptyMove for two ticks:
        //   (a) the spawn-send tick itself
        //   (b) the tick whose emptyMove target tick equals the spawn cmd's target tick —
        //       collision would last-write-wins overwrite the spawn cmd in the server's InputBuffer.
        if (_lastSpawnAttemptTick >= 0
            && tick > _lastSpawnAttemptTick
            && tick != _lastSpawnAttemptTick + _extraSpawnDelay)
        {
            // emit a no-op MoveInputCommand so the tick advances on the server side
        }
        return;
    }

    // Normal poll path — capture input and dispatch Move/Attack/Skill commands.
    // _input.CaptureInput()/ConsumeOneShot() book-end the dispatch as before.
}

private static bool HasOwnCharacter(Frame frame, int playerId)
{
    var filter = frame.Filter<OwnerComponent, CharacterComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
        if (owner.OwnerId == playerId) return true;
    }
    return false;
}
```

### E-6-4. Rejection / rollback hooks

```csharp
private void HandleExtraDelayChanged(int newDelay)
    => _lastServerPushTick = _engine.CurrentTick;   // refresh grace anchor on every server push

private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
{
    // Non-spawn PastTick → reactive fallback (F-3). Avoids double-bump with spawn-only escalation.
    if (cmdTypeId != SpawnCharacterCommand.TYPE_ID)
    {
        if (reason == RejectionReason.PastTick) HandleReactivePastTick(tick);
        return;
    }

    // Spawn-cmd specific responses:
    if (reason == RejectionReason.Duplicate) { _lastSpawnAttemptTick = -1; return; }
    if (reason == RejectionReason.PastTick)
    {
        _lastSpawnAttemptTick = -1;
        if (_extraSpawnDelay < SPAWN_DELAY_MAX) _extraSpawnDelay += SPAWN_DELAY_STEP;
        else /* one-shot Error log + post-cap reject counter */;
    }
}

private void HandleReactivePastTick(int tick) { /* sliding window + grace check → EscalateExtraDelay */ }

private void HandleRollback(int fromTick, int toTick)
{
    if (_engine.IsHost) return;                                   // host has direct push authority
    if (_engine.RecommendedExtraDelay >= REACTIVE_MAX) return;    // cap reached
    // Fixed-window rollback count + grace + cooldown → EscalateExtraDelay
}
```

### E-6-5. `SendSpawnCommand` — uses `extraDelay` parameter

```csharp
public void SendSpawnCommand(IKlothoEngine engine)
{
    int playerId = engine.LocalPlayerId;
#if KLOTHO_FAULT_INJECTION
    if (FaultInjection.DropSpawnCommandPlayerIds.Contains(playerId))
    { _lastSpawnAttemptTick = engine.CurrentTick; return; }   // exercise self-heal path
#endif
    var rules    = ((EcsSimulation)engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
    int spawnIdx = playerId % rules.SpawnPositions.Length;
    FPVector3 pos = rules.SpawnPositions[spawnIdx];

    var playerConfig = engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);
    var cmd = CommandPool.Get<SpawnCharacterCommand>();
    cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
    cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);

    // Engine fills PlayerId/Tick; pass extra lead so retries overshoot far enough to pass server check.
    _lastSpawnAttemptTick = engine.CurrentTick;
    engine.InputCommand(cmd, extraDelay: _extraSpawnDelay);
}
```

`RegisterSystems(EcsSimulation)` retains the prior shape — NavMesh query / bot HFSM build / `BrawlerSimSetup.RegisterSystems`.

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

    public void OnTickExecuted(int tick) { }      // HUD updates: EVU + GameHUD subscribe directly

    public void OnLateJoinActivated(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        _sim.SendSpawnCommand(engine);
    }
    // Note: A previous `Respawn(IKlothoEngine)` helper was removed — respawn is now driven by
    // the state-driven spawn loop in OnPollInput (see E-6-3).
}
```

---

## E-8. Cross-Reference Caveats

- `BrawlerSimulationCallbacks._engine` is null until `SetEngine` runs (called from `OnInitializeWorld`). `OnPollInput` guards on `_engine == null` to stay safe before that point.
- `SetNetworkService` is currently a **no-op** on `BrawlerSimulationCallbacks` — bot spawn is decided by `_botCount` and the engine reference (from `SetEngine`) covers everything else. The setter is kept for API symmetry only.
- `BrawlerViewCallbacks` only obtains `engine` at `OnGameStart`. Don't use it before that.
- View wiring goes through `InitializeViewSync(engine, simulation)` (an internal helper of `BrawlerGameController`) rather than a direct `EntityViewUpdater.Initialize(engine)` call. Call sites: every successful StartHost / JoinGameAsync / ReconnectAsync / StartReplay path.
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

    InitializeViewSync(_session.Engine, _session.Simulation);

    _session.Engine.StartReplay(replayData);   // LZ4 decompression is handled automatically on load
}
```

> **Correction**: `KlothoSession.CreateForReplay` / `StartReplayFromFile` do not exist. Replay is launched via the combination `KlothoSession.Create` + `Engine.StartReplay(IReplayData)`.

---

## E-10. FaultInjection / RTT Spike Schedule (development-only)

Compiled in only when the `KLOTHO_FAULT_INJECTION` define is set. Disabled and stripped in production builds.

### E-10-1. Bootstrap hook — `ApplyFaultInjection()`

Called at the end of `Start()` (after the auto-reconnect probe). Loads `Assets/StreamingAssets/faultinjectionconfig.json` via `FaultInjectionLoader.TryLoadAndApply`. Missing file is silently ignored — fault injection stays off.

```csharp
private void Start()
{
    // ... (data preload, transport init, GameMenu wiring, auto-reconnect probe)

#if KLOTHO_FAULT_INJECTION
    ApplyFaultInjection();
#endif
}

#if KLOTHO_FAULT_INJECTION
private void ApplyFaultInjection()
{
    var path = Path.Combine(Application.streamingAssetsPath, "faultinjectionconfig.json");
    FaultInjectionLoader.TryLoadAndApply(path, _logger);
}
#endif
```

Schema fields (see `FaultInjectionLoader.cs`): `EmulatedRttMs`, `EmulatedRttSchedule[(atSec, rttMs)]`, `ServerGcPauseMs`, `ServerGcPauseAtTick`, `DropSpawnCommandPlayerIds`, `SuppressBootstrapAckPlayerIds`, `ForceTickOffsetDelta`.

### E-10-2. Update hook — `UpdateRttSchedule()`

Called every frame from `Update()`. Drives the timed `EmulatedRttSchedule` once `Phase == Playing` and emits a match-end metrics line on exit.

```csharp
private void Update()
{
    UpdateStatus();

#if KLOTHO_FAULT_INJECTION
    UpdateRttSchedule();
#endif

    // ... (engine update, replay, etc.)
}

#if KLOTHO_FAULT_INJECTION
private void UpdateRttSchedule()
{
    if (Phase != SessionPhase.Playing)
    {
        if (_rttScheduleAnchorTime >= 0f)
        {
            // Leaving Playing → flush match-end summary.
            RttSpikeMetricsCollector.EmitSummary(_logger);
            _rttScheduleAnchorTime = -1f;
            _rttScheduleNextIdx = 0;
        }
        return;
    }

    if (_rttScheduleAnchorTime < 0f)
    {
        // First frame after entering Playing — anchor the schedule clock.
        _rttScheduleAnchorTime = Time.unscaledTime;
        _rttScheduleNextIdx = 0;
        int localId = ActiveEngine?.LocalPlayerId ?? -1;
        RttSpikeMetricsCollector.OnMatchStart(IsHost ? "host" : "guest", localId);
    }

    if (FaultInjection.EmulatedRttSchedule.Count == 0)
        return;

    float elapsedSec = Time.unscaledTime - _rttScheduleAnchorTime;
    var schedule = FaultInjection.EmulatedRttSchedule;
    while (_rttScheduleNextIdx < schedule.Count && elapsedSec >= schedule[_rttScheduleNextIdx].atSec)
    {
        var entry = schedule[_rttScheduleNextIdx];
        FaultInjection.EmulatedRttMs = entry.rttMs;          // overwrite live RTT
        RttSpikeMetricsCollector.OnSpike(entry.atSec, entry.rttMs);
        _rttScheduleNextIdx++;
    }
}
#endif
```

### E-10-3. Anchor clock and per-client drift

`_rttScheduleAnchorTime` is captured the first frame `Phase` enters `Playing` — that is, after `GameStartMessage` arrives. Each client anchors against its own receive time, so spike timings drift across clients by the `GameStartMessage` jitter (typically a few ms to tens of ms). Acceptable for measurement; not deterministic.

### E-10-4. Metrics emit

`RttSpikeMetricsCollector.EmitSummary` writes a one-line `[Metrics][RttSpike]` log at match end with spike list, chain-break counts windowed around each spike, rollback depth mean/p95, and chain-resume latency per spike. Used by the RTT spike measurement scripts.
