# Quick Start — Unity

A minimal end-to-end path to a running Klotho session in Unity. Steps 1–3 (component / system / callbacks) are the **engine-agnostic core** and are identical to the [Godot quick start](QuickStart.Godot.md); steps 4–5 (session driving + view) use the Unity adapter (`MonoBehaviour`, `ScriptableObject`, UniTask).

> Install first — see [Installation — Unity](Installation.Unity.md).

---

## 1) Define a Component

Components are `partial struct`s; the source generator emits serialization from the `[KlothoComponent]` id.

```csharp
using xpTURN.Klotho.Deterministic.Math;   // FP64, FPVector3
using xpTURN.Klotho.ECS;                   // IComponent, [KlothoComponent]

[KlothoComponent(100)]  // 1–99: framework-reserved, 100+: your game
public partial struct PaddleComponent : IComponent
{
    public FP64    Speed;       // fixed-point — never float in simulation state
    public int     OwnerId;
    public sbyte   MoveDir;     // -1 / 0 / +1 from input
}
```

## 2) Implement a System

Systems run over `Frame` each tick. Use `FP64` math only — no `float`, `Mathf`, `Random`, or `DateTime`.

```csharp
public sealed class PaddleSystem : ISystem, ICommandSystem
{
    // Commands arrive deterministically — stash the latest direction onto the owning paddle.
    public void OnCommand(ref Frame frame, ICommand command)
    {
        if (command is not MoveCommand move) return;     // MoveCommand: your own command (: CommandBase, pooled via CommandPool)
        var filter = frame.Filter<PaddleComponent>();
        while (filter.Next(out var entity))
        {
            ref var paddle = ref frame.Get<PaddleComponent>(entity);
            if (paddle.OwnerId != move.PlayerId) continue;
            paddle.MoveDir = move.Dir;
            return;
        }
    }

    public void Update(ref Frame frame)
    {
        var dt = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);   // ms → seconds (fixed tick)
        var filter = frame.Filter<PaddleComponent, TransformComponent>();
        while (filter.Next(out var entity))
        {
            ref var paddle = ref frame.Get<PaddleComponent>(entity);
            ref var tf     = ref frame.Get<TransformComponent>(entity);
            tf.Position += new FPVector3(FP64.Zero, FP64.Zero,
                                         paddle.Speed * dt * FP64.FromInt(paddle.MoveDir));
        }
    }
}
```

## 3) Implement Callbacks (determinism / view separation)

`ISimulationCallbacks` is deterministic (systems, world spawn, input); `IViewCallbacks` is the non-deterministic render side.

```csharp
public sealed class PongSimulationCallbacks : ISimulationCallbacks
{
    private readonly PongInput _input;
    public PongSimulationCallbacks(PongInput input) => _input = input;   // injected from the controller (step 4)

    public void RegisterSystems(EcsSimulation sim) => sim.AddSystem(new PaddleSystem(), SystemPhase.PreUpdate);

    public void OnInitializeWorld(IKlothoEngine engine)
    {
        // spawn paddles, ball, walls into the initial frame
    }

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        var cmd = CommandPool.Get<MoveCommand>();
        cmd.Dir = _input.AxisZ;                          // captured in PreSessionUpdate (step 4)
        sender.Send(cmd);
    }

    public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }  // late-join world seed; empty when no per-join state
}

public sealed class PongViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine)        { /* show HUD */ }
    public void OnTickExecuted(int tick)                 { /* per-tick view hook */ }
    public void OnLateJoinActivated(IKlothoEngine engine){ }
}
```

## 4) Create & drive a Session (Unity)

Build a `KlothoSessionFlow` once, and drive it with **`KlothoSessionDriver`** — a `MonoBehaviour` that owns the `Update` / `Stop` loop. Attach it as a `[SerializeField]` on your game-controller prefab.

```csharp
using Cysharp.Threading.Tasks;
using UnityEngine;
using xpTURN.Klotho;               // EntityViewFactory, USimulationConfig (Unity adapter)
using xpTURN.Klotho.Core;          // KlothoSessionFlow, KlothoFlowSetup, IKlothoSessionObserver, KlothoState
using xpTURN.Klotho.ECS;           // IDataAssetRegistry
using xpTURN.Klotho.LiteNetLib;    // LiteNetLibTransport
using xpTURN.Klotho.Network;       // INetworkTransport
using xpTURN.Klotho.Unity;         // KlothoSessionDriver, UnityDeviceIdProvider, KlothoLogger

public sealed class PongController : MonoBehaviour, IKlothoSessionObserver
{
    [SerializeField] private KlothoSessionDriver _driver;
    [SerializeField] private USimulationConfig   _simConfig;   // ScriptableObject
    [SerializeField] private USessionConfig      _sessionConfig;

    private KlothoSessionFlow  _flow;
    private INetworkTransport  _transport;
    private IDataAssetRegistry _registry;
    private readonly PongInput _input = new();   // your input capture — reads InputSystem in PreSessionUpdate

    void Awake()
    {
        _transport = new LiteNetLibTransport(KlothoLogger.CreateDefault(), connectionKey: "pong");
        // Capture input just before each Session.Update.
        _driver.PreSessionUpdate += (s, dt) => { if (s.State == KlothoState.Running) _input.Capture(); };
    }

    void Start()
    {
        _registry = BuildAssetRegistry();   // your DataAssetRegistry (e.g. built from a .bytes TextAsset)
        _flow = new KlothoSessionFlow(new KlothoFlowSetup
        {
            Logger            = KlothoLogger.CreateDefault(),
            Transport         = _transport,
            AssetRegistry     = _registry,
            AppVersion        = Application.version,
            DeviceIdProvider  = new UnityDeviceIdProvider(),
            LifecycleObserver = this,
            CallbacksFactory  = (sim, sess) =>
                new SessionCallbacks(new PongSimulationCallbacks(_input), new PongViewCallbacks()),
        });
        _driver.BindTransport(_transport, this, _flow);   // driver pumps the transport while idle
    }

    // Host a P2P match (synchronous; null = listen-bind failed, already torn down).
    public void Host()
    {
        var session = _flow.StartHostAndListen(_simConfig, _sessionConfig,
                                               roomName: "Pong", address: "0.0.0.0", port: 9050);
        if (session != null) _driver.Attach(session);
    }

    // Join as a guest (UniTask on Unity).
    public async UniTask Join(string host, int port)
    {
        var result = await _flow.JoinP2PAsync(_transport, host, port, _sessionConfig,
                                              KlothoLogger.CreateDefault());
        _driver.Attach(result);
    }

    // IKlothoSessionObserver — single surface for session lifecycle / state.
    public void OnSessionCreated(KlothoSession session, SessionEntryKind kind) { /* wire HUD */ }
    public void OnSessionStopping() { /* view / EVU teardown while engine still alive */ }
    public void OnSessionStopped()  { /* terminal teardown */ }
    // ... + OnStateChanged / OnPhaseChanged / OnPlayerCountChanged / OnAllPlayersReadyChanged /
    //     OnIdleDisconnected and the NetworkService hooks — see GameDevAPI.md §3.1 for the full set.
}
```

> `PongInput`, `MoveCommand`, and `BuildAssetRegistry()` are **your** game types (input capture, a `CommandPool`-pooled `CommandBase`, and a built `IDataAssetRegistry`) — shown abbreviated here. `IKlothoSessionObserver` has more members than the three above; implement the full set (or derive from a base that no-ops them).

## 5) View Sync (Unity)

Render entities through **`EntityViewFactory` (ScriptableObject)** + **`EntityViewUpdater` (one MonoBehaviour in the scene)** + **`EntityView` (prefab MonoBehaviour)**. The factory decides `BindBehaviour` / `ViewFlags` and creates the view with `async UniTask CreateAsync`; the updater Reconciles every tick.

```csharp
[CreateAssetMenu(menuName = "Pong/PaddleViewFactory")]
public sealed class PaddleViewFactory : EntityViewFactory
{
    [SerializeField] private GameObject _paddlePrefab;

    protected override bool ShouldRender(Frame frame, EntityRef e) => frame.Has<PaddleComponent>(e);

    public override async UniTask<EntityView> CreateAsync(
        Frame frame, EntityRef e, BindBehaviour b, ViewFlags f)
    {
        if (Pool != null) return await Pool.Rent(_paddlePrefab);
        return Object.Instantiate(_paddlePrefab).GetComponent<EntityView>();
    }
}
```

Wire the factory + (optional) `DefaultEntityViewPool` onto the scene's `EntityViewUpdater` and call `evu.Initialize(session.Engine)` at bootstrap — or hand either in from code with `evu.Initialize(session.Engine, factory, pool)` (`null` = keep what the Inspector holds), which is also how a custom `IEntityViewPool` is supplied, since Unity cannot serialize an interface. Full walkthrough: [GameDevWorkflow.md → Step 7](GameDevWorkflow.md).

---

## Session entry points

`KlothoSessionFlow` exposes these (engine-agnostic; Unity wraps the async ones in `UniTask`) — pick one per mode:

- `StartLocal(simCfg, sessionCfg, roomName?)` — single player (sync, no socket).
- `StartHostAndListen(simCfg, sessionCfg, roomName, address, port)` — P2P host (sync).
- `JoinP2PAsync(transport, host, port, sessionCfg, ct)` — P2P guest.
- `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` — Server-Driven client.
- `ReconnectAsync(transport, creds, sessionConfigSeed, ct)` — cold-start reconnect (`creds` = `PersistedReconnectCredentials`).
- `SpectateAsync(host, port, roomId, ct)` — spectator (transport from `SpectatorTransportFactory`).
- `StartReplayFromFile(path)` — file → session replay.

### Single player

One call, no socket, no port:

```csharp
var setup = new KlothoFlowSetupBuilder(BuildCallbacks)
    .WithLogger(_logger)
    .WithTransport(new NullTransport(_logger))   // no bind, no firewall prompt
    .Build();
var session = new KlothoSessionFlow(setup).StartLocal(_simConfig, _sessionConfig);
```

`_sessionConfig` is **copied** — `MinPlayers` is forced to 1 on the copy, so a shared inspector config is never written to. Author `CountdownDurationMs = 0` unless you want the default 3-second countdown before the match starts. Observers get `SessionEntryKind.Local`, not `Host`.

Session creation / state is observed through the single `IKlothoSessionObserver` — branch on `OnSessionCreated`'s `SessionEntryKind`, not `simCfg.Mode`.

---

Detailed guides: [GameDevWorkflow.md](GameDevWorkflow.md) · [GameDevAPI.md](GameDevAPI.md) · Godot equivalent: [QuickStart.Godot.md](QuickStart.Godot.md)
