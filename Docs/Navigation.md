# Deterministic Navigation

A deterministic NavMesh navigation system based on FP64. All computation runs on fixed-point arithmetic, guaranteeing synchronization across clients.

## Components

| Class | Role |
| ------ | ---- |
| `FPNavMesh` | NavMesh data: vertices, triangles, and a spatial grid |
| `FPNavMeshTriangle` | Triangle vertices / adjacency / portals / area / cost data |
| `FPNavMeshQuery` | Triangle lookup, height sampling, nearest-point queries |
| `FPNavMeshPathfinder` | A* search over the triangle graph (GC 0) |
| `FPNavMeshBinaryHeap` | A* open-set priority queue |
| `FPNavMeshFunnel` | SSFA (Simple Stupid Funnel Algorithm) — corridor → waypoints |
| `NavAgentComponent` | ECS agent component (`[KlothoComponent(11)]`; position / velocity / corridor / destination) |
| `FPNavAgentStatus` | Agent-status enum (Idle / PathPending / Moving / Arrived / PathFailed) |
| `FPNavAgentSystem` | Path request → steering → ORCA avoidance → movement → NavMesh constraint (operates on Frame + EntityRef[]) |
| `FPNavAvoidance` | ORCA (Optimal Reciprocal Collision Avoidance) agent-collision avoidance |
| `NavCorridorHelper` | Corridor-search and corridor-maintenance helper utilities |
| `FPNavMeshSerializer` | Binary serialization / deserialization |

## File Layout

```text
Assets/Klotho/Runtime/Deterministic/Navigation/
├── FPNavMesh.cs              # NavMesh data (vertices, triangles, spatial grid)
├── FPNavMeshTriangle.cs      # Triangle struct (adjacency, portals, area, cost)
├── FPNavMeshQuery.cs         # Spatial queries (triangle lookup, height sampling)
├── FPNavMeshPathfinder.cs    # A* pathfinding
├── FPNavMeshBinaryHeap.cs    # A* priority queue
├── FPNavMeshFunnel.cs        # SSFA path smoothing
├── FPNavMeshSerializer.cs    # Binary serialization
├── NavAgentComponent.cs      # ECS agent component + FPNavAgentStatus enum
├── FPNavAgentSystem.cs       # Agent update system (Frame + EntityRef[])
├── FPNavAvoidance.cs         # ORCA collision avoidance
└── NavCorridorHelper.cs      # Corridor helper utilities

Assets/Klotho/Editor/NavMesh/
├── FPNavMeshExporter.cs          # Unity NavMesh → FPNavMesh conversion tool
├── FPNavMeshVisualizerWindow.cs  # NavMesh visualization editor window
├── FPNavMeshVisualizerData.cs    # Visualization state
├── FPNavMeshVisualizerStyles.cs  # Visualization styles
├── FPNavMeshSceneOverlay.cs      # Scene-view overlay rendering
├── FPNavMeshAgentSimulator.cs    # Agent-movement simulator
└── FPNavMeshInteraction.cs       # Click-to-navigate interaction
```

## NavMesh Pipeline

```text
Unity NavMesh ──[FPNavMeshExporter]──▸ .bytes file
                                         │
                            FPNavMeshSerializer.Deserialize()
                                         │
                                         ▼
                                     FPNavMesh
                                         │
    ┌────────────────────────────────────┼────────────────────────────────┐
    ▼                                    ▼                                ▼
FPNavMeshQuery                  FPNavMeshPathfinder                FPNavMeshFunnel
(triangle lookup, height)        (A* corridor build)             (corridor → waypoints)
    │                                    │                                │
    └──────────────┬─────────────────────┴────────────────────────────────┘
                   ▼
           FPNavAgentSystem.Update(ref Frame, EntityRef[] ...)
           ┌──────────────────────┐
           │ 1. ProcessPathRequest│ ─▸ A* + Funnel
           │ 2. ProcessSteering   │ ─▸ Seek + Arrive
           │ 3. ORCA Avoidance    │ ─▸ agent-agent collision avoidance
           │ 4. ProcessMovement   │ ─▸ acceleration, speed clamp, position update
           │ 5. ConstrainToNavMesh│ ─▸ boundary-edge sliding
           └──────────────────────┘
```

## Core Data Structures

### FPNavMesh

| Field | Type | Description |
| ---- | ---- | ---- |
| `Vertices` | `FPVector3[]` | 3D vertices (Y = height, XZ = plane) |
| `Triangles` | `FPNavMeshTriangle[]` | Triangle array with adjacency info |
| `BoundsXZ` | `FPBounds2` | Total XZ bounds |
| `GridCells` | `int[]` | Spatial-grid cells (start, count pairs) |
| `GridTriangles` | `int[]` | Triangle indices referenced by cells |
| `GridWidth/Height` | `int` | Grid dimensions |
| `GridCellSize` | `FP64` | Cell size |
| `GridOrigin` | `FPVector2` | Grid origin |

### FPNavMeshTriangle

| Field | Type | Description |
| ---- | ---- | ---- |
| `v0, v1, v2` | `int` | Vertex indices |
| `neighbor0/1/2` | `int` | Neighbor triangles (-1 = boundary) |
| `portal*Left/Right` | `int` | Funnel-portal vertex indices |
| `centerXZ` | `FPVector2` | Precomputed centroid (A* heuristic) |
| `area` | `FP64` | Triangle area |
| `areaMask` | `int` | Area mask (walkable-area filter) |
| `costMultiplier` | `FP64` | Cost multiplier (1.0 = default) |
| `isBlocked` | `bool` | Dynamic-block flag |

### NavAgentComponent

A `[KlothoComponent(11)]` ECS component. Holds the corridor with `unsafe` + `fixed` buffers, GC-free.

| Field | Type | Description |
| ---- | ---- | ---- |
| **Configuration** | | |
| `Speed` | `FP64` | Max speed (default: 5) |
| `Acceleration` | `FP64` | Acceleration (default: 10) |
| `AngularSpeed` | `FP64` | Max angular speed (default: 360) |
| `Radius` | `FP64` | Agent radius |
| `StoppingDistance` | `FP64` | Stopping distance |
| `PathRepathCooldown` | `FP64` | Re-pathing cooldown |
| **Runtime State** | | |
| `Position` | `FPVector3` | Current position |
| `Velocity` | `FPVector2` | Current velocity (XZ) |
| `DesiredVelocity` | `FPVector2` | Desired velocity (steering / ORCA result) |
| `CurrentSpeed` | `FP64` | Current linear speed |
| **Path (corridor)** | | |
| `Corridor[128]` | `int (fixed)` | Triangle corridor (`MAX_CORRIDOR`) |
| `CorridorLength` | `int` | Effective corridor length |
| `PathTarget` | `FPVector3` | Final path target |
| `PathId` | `int` | Path identifier |
| `PathIsValid` | `bool` | Path validity |
| **Destination / Triangle** | | |
| `Destination` | `FPVector3` | Destination |
| `HasNavDestination` | `bool` | Whether a destination is set |
| `HasPath` | `bool` | Whether a path exists |
| `CurrentTriangleIndex` | `int` | Currently occupied triangle |
| **Internal Counters** | | |
| `LastRepathTick` | `int` | Last re-path tick |
| `PathRequestId` | `int` | Path-request ID |
| `OffCorridorTicks` | `int` | Off-corridor tick counter |
| `Status` | `byte` (`FPNavAgentStatus`) | Idle / PathPending / Moving / Arrived / PathFailed |

Initialization is performed via the static method `NavAgentComponent.Init(ref nav, startPosition)`.

## Usage

Inside an ECS system, batch-process entities holding `NavAgentComponent` via `FPNavAgentSystem.Update`.

```csharp
// Bootstrap — load NavMesh and wire up the system (e.g., in a RegisterSystems hook)
byte[] data = navMeshAsset.bytes;
FPNavMesh navMesh = FPNavMeshSerializer.Deserialize(data);

var query      = new FPNavMeshQuery(navMesh);
var pathfinder = new FPNavMeshPathfinder(navMesh, query);
var funnel     = new FPNavMeshFunnel(navMesh, query);
var navSystem  = new FPNavAgentSystem(navMesh, query, pathfinder, funnel, logger);

// Enable ORCA avoidance (optional)
navSystem.SetAvoidance(new FPNavAvoidance());

// Spawn an agent — attach NavAgentComponent to an ECS entity
var entity = frame.CreateEntity();
frame.Add(entity, new NavAgentComponent());
ref var nav = ref frame.Get<NavAgentComponent>(entity);
NavAgentComponent.Init(ref nav, new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)));
nav.Destination       = new FPVector3(FP64.FromInt(9), FP64.Zero, FP64.FromInt(1));
nav.HasNavDestination = true;

// Per-tick update (inside ISystem.Update) — collect entities with NavAgentComponent and batch-process
navSystem.Update(ref frame, entities, entityCount, currentTick, dt);
```

## NavMesh Export

Use the Unity Editor menu `Tools > Klotho > Export NavMesh` to export the current scene's Unity NavMesh to a `.bytes` file. Visualization is at `Tools > Klotho > Visualizer > NavMesh`.

- Vertex welding (WELD_EPSILON = 0.001)
- Degenerate-triangle removal
- Automatic adjacency + portal build
- Spatial-grid build (default cell size = 4.0)

## Constants

| Constant | Location | Value | Description |
| ---- | ---- | -- | ---- |
| `MAX_CORRIDOR` | `NavAgentComponent`, `FPNavMeshPathfinder` | 128 | Max length of A* / agent corridor |
| `MAX_WAYPOINTS` | `FPNavMeshFunnel` | 64 | Max number of Funnel waypoints |
| `MAX_ITERATIONS` | `FPNavMeshPathfinder` | 4096 | Max A* iterations |
| `MAX_ORCA_LINES` | `FPNavAvoidance` | 64 | Max ORCA half-planes |
| `MAX_NEIGHBORS` | `FPNavAvoidance` | 16 | Max ORCA neighbor agents |
| `DEFAULT_AREA_MASK` | `FPNavAgentSystem` | `~0` | Default area mask (all areas allowed) |

---

*Last updated: 2026-04-24*
