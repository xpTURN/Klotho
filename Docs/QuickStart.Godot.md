# Quick Start — Godot (.NET)

A minimal end-to-end path to a running Klotho session in Godot 4.4+ (mono / .NET). Steps 1–3 (component / system / callbacks) are the **engine-agnostic core** and are identical to the [Unity quick start](QuickStart.Unity.md); steps 4–5 (session driving + view) use the Godot adapter (`Node`, `Resource`, standard `Task` — no UniTask).

> Install first — see [Installation — Godot](Installation.Godot.md). The adapter ships as `res://addons/klotho/` + a one-line `Klotho.props` import.
>
> **Optional — enable the editor plugin**: after building once, turn on **Klotho** under *Project ▸ Project Settings ▸ Plugins* to get the *Klotho: Convert DataAsset JSON → bytes* tool (Project menu + right-click a `.json` in the FileSystem dock) — handy for producing the `.bytes` asset in step 4. The runtime works without it; the plugin only adds editor tooling, and you must build **before** enabling (it references `[GlobalClass]` C# types). Steps: [`Godot~/README.md` → Enable the plugin](../com.xpturn.klotho/Godot~/README.md#enable-the-plugin).

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

Systems run over `Frame` each tick. Use `FP64` math only — no `float`, `Godot.Mathf`, `GD.Randi`, or `Time`.

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

`ISimulationCallbacks` is deterministic (systems, world spawn, input); `IViewCallbacks` is the non-deterministic render side. These types are core — the same on both engines.

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

## 4) Create & drive a Session (Godot)

Build a `KlothoSessionFlow` once, and drive it with **`GodotSessionDriver`** — a `Node` that pumps `KlothoSession.Update` from `_Process`. Add it as a child node and wire everything in `_Ready`. The async joins return a standard `Task<KlothoSession>` (no UniTask).

```csharp
using System.Threading.Tasks;
using global::Godot;
using xpTURN.Klotho.Core;          // KlothoSessionFlow, KlothoFlowSetup, IS*Config, IKlothoSessionObserver, KlothoState
using xpTURN.Klotho.ECS;           // IDataAssetRegistry
using xpTURN.Klotho.Godot;         // GodotSessionDriver, Godot*Config, GodotKlothoLogger, GodotFlowSetupBuilderExtensions
using xpTURN.Klotho.LiteNetLib;    // LiteNetLibTransport
using xpTURN.Klotho.Network;       // INetworkTransport

public partial class PongController : Node, IKlothoSessionObserver
{
    private KlothoSessionFlow   _flow;
    private INetworkTransport   _transport;
    private GodotSessionDriver  _driver;
    private ISimulationConfig   _simConfig;
    private ISessionConfig      _sessionConfig;
    private IDataAssetRegistry  _registry;
    private readonly PongInput  _input = new();   // your input capture — reads Godot Input in PreSessionUpdate

    public override void _Ready()
    {
        var logger     = GodotKlothoLogger.CreateDefault(categoryName: "Pong");   // GodotLogSink + RollingFileSink under user://logs
        _transport     = new LiteNetLibTransport(logger, connectionKey: "pong");
        _simConfig     = GD.Load<GodotSimulationConfig>("res://Config/Simulation.tres"); // Resource
        _sessionConfig = GD.Load<GodotSessionConfig>("res://Config/Session.tres");
        _registry      = BuildAssetRegistry();   // your DataAssetRegistry, e.g. built from a .bytes asset (FileAccess)

        _flow = new KlothoSessionFlow(
            new KlothoFlowSetupBuilder((sim, sess) =>
                    new SessionCallbacks(new PongSimulationCallbacks(_input), new PongViewCallbacks()))
                .WithLogger(logger)
                .WithTransport(_transport)
                .WithAssetRegistry(_registry)
                .WithGodotDefaults()              // AppVersion from ProjectSettings + GodotDeviceIdProvider
                .WithLifecycleObserver(this)
                .Build()
        );

        _driver = new GodotSessionDriver();
        AddChild(_driver);                                     // _Process pumps the transport while idle
        _driver.BindTransport(_transport);
        _driver.PreSessionUpdate += (s, dt) => { if (s.State == KlothoState.Running) _input.Capture(); };
    }

    // Host a P2P match (synchronous core call; null = listen-bind failed, already torn down).
    public void Host(string address, int port)
    {
        var session = _flow.StartHostAndListen(_simConfig, _sessionConfig, "Pong", address, port);
        if (session != null) _driver.Attach(session);
    }

    // Join as a guest — standard Task (await it from an async method).
    public async Task Join(string host, int port)
    {
        var session = await _flow.JoinP2PAsync(_transport, host, port, _sessionConfig);
        // Logger and DeviceIdProvider are pulled from flow automatically (flow.Logger / flow.DeviceIdProvider).
        _driver.Attach(session);
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

## 5) View Sync (Godot)

Render entities through **`EntityViewFactory` (abstract class, injected with a `PackedScene`)** + **`EntityViewUpdaterNode` (one `Node` in the scene, self-driven by `_Process`)** + **`EntityViewNode` (`Node3D` root of a `.tscn`)**. View creation is **synchronous** (`Create`, no `await`).

```csharp
public sealed class PaddleViewFactory : EntityViewFactory
{
    private readonly PackedScene _paddleScene;
    public PaddleViewFactory(PackedScene paddleScene) => _paddleScene = paddleScene;

    protected override bool ShouldRender(Frame frame, EntityRef e) => frame.Has<PaddleComponent>(e);
    protected override PackedScene ResolvePrefab(Frame frame, EntityRef e) => _paddleScene;
    // base.Create() instantiates the PackedScene (root = EntityViewNode); TryGetBindBehaviour /
    // GetViewFlags have the same signatures as Unity — override only when a sample-specific rule applies.
}

public partial class PaddleView : EntityViewNode
{
    public override void OnActivate(FrameRef frame) { base.OnActivate(frame); /* cache owner */ }
    public override void OnUpdateView()             { base.OnUpdateView();    /* per-tick game data */ }
}
```

Wire it once the session exists (e.g. inside `OnSessionCreated`):

```csharp
var paddleScene = GD.Load<PackedScene>("res://paddle.tscn");
var pool    = new DefaultGodotEntityViewPool();
pool.Prewarm(paddleScene, _sessionConfig.MaxPlayers);
var updater = new EntityViewUpdaterNode();
AddChild(updater);                                          // _Process Reconciles (ProcessPriority = 1000)
updater.Initialize(session.Engine, new PaddleViewFactory(paddleScene), pool);
```

A full reference lives in [`Samples/GodotP2pSample/`](../Samples/GodotP2pSample/) ([walkthrough](Samples/GodotP2pSample.md)).

---

## Session entry points

`KlothoSessionFlow` exposes these (engine-agnostic; the async joins come from `GodotSessionFlowAsync` as `Task`) — pick one per mode:

- `StartLocal(simCfg, sessionCfg, roomName?)` — single player (sync, core, no socket).
- `StartHostAndListen(simCfg, sessionCfg, roomName, address, port)` — P2P host (sync, core).
- `JoinP2PAsync(transport, host, port, sessionCfg)` — P2P guest (`Task<KlothoSession>`). Logger and DeviceIdProvider are pulled from `flow` automatically.
- `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg)` — Server-Driven client.
- `ReconnectAsync(transport, creds, sessionConfigSeed)` — cold-start reconnect (`creds` = `PersistedReconnectCredentials`). Requires `WithGodotDefaults()` (or `.WithHandshake(appVersion, deviceIdProvider)`) on the builder — reconnect credentials are minted during the initial join.
- Spectator / replay follow the core `KlothoSessionFlow` surface.

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

Detailed guides: [GameDevWorkflow.md](GameDevWorkflow.md) · [GameDevAPI.md](GameDevAPI.md) · Unity equivalent: [QuickStart.Unity.md](QuickStart.Unity.md)
