# Deterministic Navigation

A deterministic NavMesh navigation system based on FP64. All computation runs on fixed-point arithmetic, guaranteeing synchronization across clients.

> **Engine scope**: the navigation **runtime** (`FPNavMesh`, `FPNavMeshQuery`, `FPNavMeshPathfinder`, `FPNavAgentSystem`, …) is engine-agnostic core — it runs unchanged on Unity, Godot, and the .NET server. The **baking + visualization editor tools** exist for **both Unity and Godot**: the geometry pipeline core (`FPNavMeshBuildPipeline`) is shared in Runtime, with an engine-specific editor exporter and visualizer on each side. Each engine bakes its **own** scene's NavMesh to a `.bytes` asset, which it then loads at runtime (Unity via `TextAsset.bytes`, Godot via `Godot.FileAccess.GetFileAsBytes`). Cross-engine `.bytes` sharing is **not** supported (coordinate-handedness differs — each engine bakes independently).

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
| `FPNavAgentStatus` | Agent-status enum (Idle / PathPending / Moving / Arrived / PathFailed / **Blocked**) |
| `FPNavAgentSystem` | Path request → steering → ORCA avoidance → movement → NavMesh constraint → position-correction pass (operates on Frame + EntityRef[]); `LoadNavMeshObstacles()` registers the NavMesh boundary as ORCA obstacles and builds the graph-local obstacle-query CSR |
| `FPNavAvoidance` | ORCA (Optimal Reciprocal Collision Avoidance): agent-agent **and** static-obstacle half-planes (wall/cliff), general non-convex + winding-aware |
| `FPObstacleVertex` | Static-obstacle vertex (RVO2 `Obstacle` equivalent), flat-array ring representation (point / unitDir / isConvex / prev/next / polygonIndex) |
| `FPNavMeshObstacleExtractor` | Extracts ORCA obstacle rings from the NavMesh boundary (`neighbor == -1` edges); load/build-time only |
| `NavCorridorHelper` | Corridor-search and corridor-maintenance helper utilities |
| `FPNavMeshSerializer` | Binary serialization / deserialization |
| `FPNavMeshRebaker` | Runtime rebake: base mesh + building footprints → new `FPNavMesh` (see [Runtime Rebake](#runtime-rebake)) |
| `FPNavMeshAreas` | The area indices and masks the runtime reserves: `BUILDING_AREA` / `BUILDING_MASK`, `DEFAULT_AGENT_MASK`, `ALL_AREAS` |
| `FPNavMeshPlacementProbe` | Base mesh + an editable, ordered placement list, rebaked on demand — the data layer editor placement tools drive |
| `FPConstrainedDelaunay` | Deterministic constrained Delaunay triangulation on the exact integer grid |
| `FPBuildingShapeCatalog` | The set of footprints a game can place (integer offsets about the shape centre) |
| `FPConvexOffset` | Expands a convex footprint by the agent radius (integer miter, conservative) |

## File Layout

```text
com.xpturn.klotho/Runtime/Deterministic/Navigation/
├── FPNavMesh.cs              # NavMesh data (vertices, triangles, spatial grid)
├── FPNavMeshTriangle.cs      # Triangle struct (adjacency, portals, area, cost)
├── FPNavMeshQuery.cs         # Spatial queries (triangle lookup, height sampling)
├── FPNavMeshPathfinder.cs    # A* pathfinding
├── FPNavMeshBinaryHeap.cs    # A* priority queue
├── FPNavMeshFunnel.cs        # SSFA path smoothing
├── FPNavMeshSerializer.cs    # Binary serialization
├── FPNavMeshBuildPipeline.cs # Engine-agnostic bake pipeline (degenerate/T-junction/adjacency/grid)
├── FPNavMeshAreas.cs         # Reserved area indices and the masks built from them
├── NavAgentComponent.cs      # ECS agent component + FPNavAgentStatus enum
├── FPNavAgentSystem.cs       # Agent update system (Frame + EntityRef[]) + LoadNavMeshObstacles()
├── FPNavAvoidance.cs         # ORCA collision avoidance (agent-agent + static obstacles)
├── FPObstacleVertex.cs       # Static-obstacle vertex struct (flat ring)
├── FPNavMeshObstacleExtractor.cs # NavMesh boundary → ORCA obstacle rings
├── NavCorridorHelper.cs      # Corridor helper utilities
├── FPNavMeshRebaker.cs       # Runtime rebake orchestrator + snapshot/context/placement types
├── FPNavMeshRebakeBufferPool.cs  # Reusable rebake work buffers (per room)
├── FPNavMeshRebakeDriver.cs  # Derives the installed mesh from the frame; slicing + mesh cache
├── FPNavMeshRebakeSeam.cs    # What a game supplies the driver (placement table, install/reseed)
├── FPNavMeshPlacementValidator.cs # Command-path verdict from the driver's own derivation
├── FPNavMeshPlacementTableOps.cs  # The derivation both share (active set, canonical order, audits)
├── FPNavMeshPlacementProbe.cs # Editable placement list + rebake on demand (editor placement tools)
├── FPNavAgentInstaller.cs    # Swap/reseed call protocol, as two separate halves
├── FPConstrainedDelaunay.cs  # Deterministic constrained Delaunay triangulation
├── FPBuildingShapeCatalog.cs # Shape table + radius-expanded derivative
├── FPConvexOffset.cs         # Convex footprint ⊕ agent radius (integer miter)
└── INavFingerprintSource.cs  # Cross-peer nav fingerprint hook

com.xpturn.klotho/Unity/Editor/NavMesh/   # Unity Editor (baking + visualization)
├── FPNavMeshExporter.cs          # Unity NavMesh → FPNavMesh conversion tool
├── FPNavMeshVisualizerWindow.cs  # NavMesh visualization editor window
├── FPNavMeshVisualizerData.cs    # Visualization state
├── FPNavMeshVisualizerStyles.cs  # Visualization styles
├── FPNavMeshSceneOverlay.cs      # Scene-view overlay rendering
├── FPNavMeshAgentSimulator.cs    # Agent-movement simulator
└── FPNavMeshInteraction.cs       # Click-to-navigate interaction

com.xpturn.klotho/Godot~/Adapters/Editor/ # Godot Editor (baking + visualization; #if TOOLS)
├── GodotFPNavMeshExporter.cs           # NavigationRegion3D → FPNavMesh conversion tool
├── GodotFPNavMeshVisualizer.cs         # Visualizer controller (plugin.gd forwards 3D virtuals)
├── GodotFPNavMeshVisualizerData.cs     # Visualization state
├── GodotFPNavMeshVisualizerDock.cs     # Dock UI (Control tree)
├── GodotFPNavMeshOverlay.cs            # Viewport overlay (ImmediateMesh)
├── GodotFPNavMeshAgentSimulator.cs     # Agent-movement simulator
├── GodotFPNavMeshInteraction.cs        # Shift+Click interaction
└── GodotFPNavMeshVisualizerStyles.cs   # Visualization styles
```

## NavMesh Pipeline

```text
Unity NavMesh / Godot NavigationRegion3D ──[exporter]──▸ .bytes file   ← bake: Unity or Godot Editor (each its own scene)
                                         │              ← load: any engine (Unity TextAsset / Godot FileAccess)
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
           │ 3. ORCA Avoidance    │ ─▸ agent-agent + static-obstacle (wall) avoidance (graph-local select)
           │ 4. ProcessMovement   │ ─▸ acceleration, speed clamp, position update
           │ 5. ConstrainToNavMesh│ ─▸ boundary-edge sliding
           │ 6. ResolveCollisions │ ─▸ position-space overlap push (gated on avoidance)
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
| `BakeAgentRadius` | `FP64` | Agent radius the source was baked with (bake settings block) — consumed as `ObstacleRadiusInset` |
| `BakeMaxSlopeDeg` | `FP64` | Max walkable slope baked with (bake settings block) — graph-local query auto-derives its climb cap |
| `BakeAgentHeight` / `BakeAgentClimb` | `FP64` | Recorded bake settings block (no runtime consumer) |

### FPNavMeshTriangle

| Field | Type | Description |
| ---- | ---- | ---- |
| `v0, v1, v2` | `int` | Vertex indices |
| `neighbor0/1/2` | `int` | Neighbor triangles (-1 = boundary) |
| `portal*Left/Right` | `int` | Funnel-portal vertex indices |
| `centerXZ` | `FPVector2` | Precomputed centroid (A* heuristic) |
| `area` | `FP64` | Triangle area |
| `areaMask` | `int` | Area membership, one bit (`1 << areaIndex` from the bake). A query passes an allowed-area *set* and a triangle is walkable to it where the two intersect — see below |
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
| `Status` | `byte` (`FPNavAgentStatus`) | Idle / PathPending / Moving / Arrived / PathFailed / Blocked |
| **Area masks** | | |
| `PlanAreaMaskOverride` | `int` | What this agent may ROUTE through. **0 = no override** → `FPNavAgentSystem.DEFAULT_AREA_MASK` |
| `WalkAreaMaskOverride` | `int` | What this agent may ENTER. Same 0 rule. Assign both through `SetAreaMask`; see [the area filter](#the-area-filter) |

Initialization is performed via the static method `NavAgentComponent.Init(ref nav, startPosition)`, which leaves both area-mask overrides at 0.

## Usage

Inside an ECS system, batch-process entities holding `NavAgentComponent` via `FPNavAgentSystem.Update`.

```csharp
// Bootstrap — load NavMesh and wire up the system (e.g., in a RegisterSystems hook)
byte[] data = navMeshAsset.bytes;                                   // Unity (TextAsset)
// Godot: byte[] data = Godot.FileAccess.GetFileAsBytes("res://Data/NavMesh.bytes");
FPNavMesh navMesh = FPNavMeshSerializer.Deserialize(data);          // same binary format on both engines

var query      = new FPNavMeshQuery(navMesh);
var pathfinder = new FPNavMeshPathfinder(navMesh, query);
var funnel     = new FPNavMeshFunnel(navMesh, query);
var navSystem  = new FPNavAgentSystem(navMesh, query, pathfinder, funnel, logger);

// Enable ORCA avoidance (optional)
navSystem.SetAvoidance(new FPNavAvoidance());
// Register the NavMesh boundary as static obstacles (wall avoidance). Call AFTER SetAvoidance.
// On all peers/server for a deterministic match (see Static Obstacles below).
navSystem.LoadNavMeshObstacles();

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

### The area filter

`areaMask` is a per-triangle **area membership** — the bake writes `1 << areaIndex`, so exactly one
bit — and a query passes the **set of areas it accepts**. A triangle is walkable to that query where
the two intersect (`(queryMask & tri.areaMask) != 0`); `~0` (`FPNavMeshAreas.ALL_AREAS`) accepts
everything. The agent system passes `FPNavAgentSystem.DEFAULT_AREA_MASK` =
`FPNavMeshAreas.DEFAULT_AGENT_MASK`: every area except the runtime's **building area** (index 1,
`FPNavMeshAreas.BUILDING_AREA`), which the rebaker stamps onto retained building footprints — so
agents treat those as walls while a caller passing `ALL_AREAS` plans through them.

**An agent can name its own masks, and it names two.** `NavAgentComponent.PlanAreaMaskOverride`
decides what it may ROUTE through and `WalkAreaMaskOverride` what it may ENTER; assign them through
`NavAgentComponent.SetAreaMask`, which also drops the corridor the old masks planned. **Zero means
"no override"** and resolves to `DEFAULT_AREA_MASK`, so an agent that names nothing behaves exactly
as it did before these fields existed — zero as a literal mask would be total paralysis, and zero is
what a `default(NavAgentComponent)` carries. Setting the plan mask permissively and the walk mask
restrictively is the interesting combination: the path is drawn straight through a retained building
and the unit walks into it and stops, i.e. it plans as if it did not know the building was there.
That stop is reported as `FPNavAgentStatus.Blocked` — see the movement contract below.

There are four pairs and three of them mean something:

| `PlanAreaMaskOverride` | `WalkAreaMaskOverride` | What the agent does at a retained footprint |
|---|---|---|
| `0` / `DEFAULT_AGENT_MASK` | `0` / `DEFAULT_AGENT_MASK` | **Routes around it.** The behaviour before these fields existed, and what every agent does until something assigns them. A destination *inside* the footprint is refused outright: `PathFailed`, counted in `DebugAreaMaskRejectedCount` |
| **`ALL_AREAS`** | **`DEFAULT_AGENT_MASK`** | **Plans through it and stops on contact.** The corridor is the shortest route as if the building were not there; the walk refuses to enter, so the agent arrives at the edge and reports `Blocked`. This is the pair the two masks exist for |
| `ALL_AREAS` | `ALL_AREAS` | **Walks through it.** Plans and enters — a unit the building does not obstruct |
| `DEFAULT_AGENT_MASK` | `ALL_AREAS` | Nothing observable. The plan already routes around, so the walk permission is never exercised |

Only the middle two need a permissive plan mask, and that is the load-bearing half: **an agent whose
plan mask excludes buildings never touches one**, so giving it a permissive walk mask changes nothing
and it can never report `Blocked`. If you are trying to observe the stop, the plan mask is what has
to be widened.

Area 1 is
therefore reserved, and `FPNavMeshBuildPipeline.Build` refuses a bake that uses it. `int` is the
ceiling, so 32 areas. The Unity exporter forwards `NavMeshTriangulation.areas` verbatim, so Unity's area types
are the indices; the Godot exporter emits area 0 for every triangle.

Both halves of the engine's movement apply it: `FindPath` filters the start triangle, the end
triangle and every expansion, and `FPNavMeshQuery.MoveAlongSurface`/`MoveAlongSurfaceWithVisited`
filter every expansion of the surface walk. Two contract points are worth knowing before you narrow
a mask:

- **Neither half gates the ground the agent is already on, and both let it leave.** The walk never
  tests the starting triangle, `FindPath` exempts a masked-out start, and — the part that makes the
  promise real — a neighbour the mask refuses is still expandable **when the triangle being expanded
  from is refused too**, in the walk and in the A\* alike. So the guarantee is "does not walk **into**
  what the mask forbids", not "cannot walk out of it": narrow a mask under an agent's feet and it
  crosses the forbidden region and leaves. Entry from accepted ground is unchanged, and the rule
  cannot carry an agent between two separate forbidden regions — crossing accepted ground resets it,
  so the reachable set is exactly *the forbidden component you stand in, plus the accepted ground it
  touches*.

  Exempting only the start was not enough, and the reason is worth knowing before you narrow a mask:
  a single small building footprint is a handful of triangles that all touch ordinary ground, but two
  snapped flush leave interior triangles with **no accepted neighbour at all**. An agent there had a
  start exemption and nowhere to use it. `FindPath` reports a masked-out start in
  `DebugMaskedStartCount` rather than refusing the call, because otherwise nothing in the result says
  the unit was ever inside a building.

- **The destination is still gated.** Only the start is exempt: a path *into* ground the mask forbids
  is refused as before and counted in `DebugAreaMaskRejectedCount`. That asymmetry is the whole
  contract — you may leave, you may not enter.
- **A rejected neighbour is a wall of the same class as `isBlocked`**, so the agent slides along its
  edge. Starting from ground the mask **accepts**, a mask that forbids every neighbour pins the agent
  where it stands; starting from ground it **refuses** it does not, because the escape rule opens those
  neighbours. The matching `FindPath` runs the same start rather than refusing it and reports it in
  `DebugMaskedStartCount` — `DebugAreaMaskRejectedCount` is the end-point counter and is silent about
  the start.
  The walk does **not** tell its caller which term refused a neighbour — `isBlocked`, the mask and
  the multi-floor threshold all take the same wall path, and the mask is re-tested there only to log
  it, so the production path pays nothing for the distinction.
- **`FPNavAgentStatus.Blocked` names the stop that follows.** When an agent's corridor leads into
  ground its own walk mask refuses and it has run into it, the agent system stops it there and says
  so. Three conditions are required — the agent **asked to move**, the walk moved it nowhere, AND the
  corridor's next triangle is refused by this mask — because each alone misfires: a real wall also
  stops the walk, "not advancing" is the normal state of an agent part-way across a triangle, and an
  agent held still by a crowd asked for nothing and has touched nothing. So `Blocked` arrives on the
  tick an agent pushes against the edge, not on the tick its velocity reaches zero. Without the status the
  stall is invisible: such an agent keeps `Moving` and a valid path, and the off-corridor repath
  never fires, because standing still inside the corridor's current triangle counts as being *on*
  the corridor.
  *"The corridor's next triangle"* means the one after **this agent's own index** in the corridor. An
  agent that is **off** its corridor gets no verdict at all: the corridor's head is not its next
  step, and the off-corridor repath — not a terminal status — is the answer for it.
- **Leaving `Blocked` is mostly the game's decision, with one engine-side exception.** Widen the mask
  (`SetAreaMask` releases the agent), retarget it, or destroy what is in the way; the engine does not
  decide who believes what. The exception is the event that can make the block untrue on its own: a
  **navmesh swap**. `ReseedAgents` hands a `Blocked` agent that still has a destination back to the
  planner (`PathPending`, cooldown bypassed), so demolishing the building that stopped a unit starts
  it moving again on the next tick. `Arrived` and `PathFailed` are deliberately left alone.
  While an agent is `Blocked` nothing recomputes its status, but the position-correction pass still
  moves it: crowd pressure can press it along the footprint edge — never into the footprint, because
  that pass carries the agent's own walk mask.

Non-uniform areas do **not** survive a runtime rebake: the rebake feeds the pipeline an all-zero
`areas` array and then copies the base mesh's uniform `areaMask` over every triangle, logging an
error and falling back to the pipeline default if the base is non-uniform. Per-region reassignment
needs the area polygons preserved as assets, which is a follow-up. A mask stamped through
`FPNavMesh.TrianglesMutable` is lost the same way. The one runtime producer that survives is the
rebaker's own: after the inherit it stamps retained building footprints
`FPNavMeshAreas.BUILDING_MASK` (exclusively), on every rebake and on the patch path too, and the
stamp is part of `ComputeFingerprint`.

`areaMask == 0` on a triangle makes it unreachable to every query including `~0`, since no bit can
intersect. The bake never produces one; `TrianglesMutable` can.

## Static Obstacles (ORCA)

`FPNavAvoidance` adds RVO2 static-obstacle half-planes on top of agent-agent avoidance, so agents steer around walls/cliffs (not just each other). Obstacle lines are **hard constraints** — `LinearProgram3` never relaxes them (only agent lines are relaxed), so velocity never leaks through a wall.

**Two obstacle sources (same flat format):** `FPVector2[] vertices + int[] polygonOffsets` fed to `FPNavAvoidance.LoadObstacles(...)`:

1. **NavMesh boundary** (Unity/Godot) — `FPNavMeshObstacleExtractor.Extract(navMesh, out verts, out offsets)` walks the boundary edges (`neighbor == -1`), orienting each by the interior (opposite) vertex — no triangle-winding assumption — and chains them by rotating around each shared vertex through the walkable fan (handles pinch/bowtie vertices). Outer boundaries come out clockwise, holes counter-clockwise, automatically. `FPNavAgentSystem.LoadNavMeshObstacles()` wraps extract + load using the system's own NavMesh.
2. **Convex polygon rings** — e.g. procedurally baked terrain blocks — passed directly to `LoadObstacles`.

Convex and non-convex (reflex) rings are both handled (the RVO2 non-convex leg arms are present). Convex CCW input exercises only the convex paths.

**Winding convention:** free (walkable) space is on the **right** of each edge's `unitDir`. Solid blocks (agents outside) are wound CCW; walkable-boundary loops (agents inside) CW. Callers guarantee this — the NavMesh extractor by construction, procedural sources by their gate.

**Determinism:** obstacle data is **per-peer local static** — it is *not* in the wire/frame/state hash. All peers load the same baked geometry, so every peer extracts an identical obstacle set and computes identical avoidance velocities. In server-driven (SD) mode the **client and server must both** call `LoadNavMeshObstacles()` — a one-sided wiring makes `ComputeNewVelocity` diverge and desyncs. `FPNavAgentSystem.DebugObstacleCount` is a setup-time diagnostic (0 while avoidance is set ⇒ missing wiring or a boundary-free mesh). Load is once per match (tick-independent), idempotent-replace across stage changes; the hot path stays GC-0.

**An agent standing exactly on an obstacle corner is a normal position, not an edge case.** Agent
coordinates and obstacle ring vertices live on the same snap lattice, and a runtime rebake can drop a
new hole ring where an agent already stands, so the segment test treats the closed end of a segment
as a hit — both ends of a segment agree, which matters because every shared ring vertex is one
segment's end and the next one's start.

**Graph-local obstacle selection (multi-floor / ramp):** `FPNavAgentSystem` picks obstacle candidates by walking the NavMesh adjacency (BFS) outward from the agent's current triangle rather than scanning every loaded segment, so only walls on the agent's own floor/ramp are considered — a stacked upper floor whose walls overlap in XZ no longer yields phantom obstacles. Expansion is gated by a per-edge step-delta floor test, a padded XZ range, and a climb cap: on a mesh that records its bake slope (`FPNavMesh.BakeMaxSlopeDeg`) the cap auto-derives as `obstRange·sin(slope)`, otherwise `MaxClimbWithinHorizon` (∞ by default). Only *which* segments are selected changes; the half-plane math stays XZ-2D and GC-0. An unlocalized agent (seed triangle `-1`) falls back to the brute-force scan.

> **Radius inset / clearance (`ObstacleRadiusInset`):** a baked NavMesh boundary is inset from the real wall by the bake **Agent Radius** (Unity min 0.05, **0 not allowed**; Godot per-resource). Since ORCA also holds the agent its own radius off the obstacle line, a naive setup double-counts clearance — agents stop ~`bakeRadius + simRadius` from walls, blocking tight corridors. `FPNavAvoidance.ObstacleRadiusInset` cancels the baked inset: the effective obstacle radius is `max(0, agent.Radius − ObstacleRadiusInset)`. `LoadNavMeshObstacles()` auto-sets it to the asset's recorded `BakeAgentRadius` (bake settings block), so under the `R_sim = R_bake` convention the effective radius is 0 and the boundary itself is the constraint (matching the point-agent funnel path, which hugs corners). Per-peer local config — auto-riding the asset keeps lockstep peers symmetric; consumers may override after the load. Keep the baked mesh clean (a fragmented mesh yields spurious obstacle rings).

## Runtime Rebake

Everything above assumes the NavMesh is fixed. It does not have to be: a building placed during a
match can be carved out of the walkable region and the rest re-triangulated, deterministically, on
every peer. A footprint then blocks only the space it covers rather than the whole corridor triangle
it happens to sit in.

The pieces live in this folder — `FPNavMeshRebaker` orchestrates, `FPConstrainedDelaunay` does the
triangulation on the exact integer grid, `FPBuildingShapeCatalog` holds the footprints a game may
place, `FPConvexOffset` expands them by the agent radius. The result is an ordinary `FPNavMesh`,
indistinguishable from a baked one, which you install with `FPNavAgentSystem.SwapNavMesh(...)` —
that rebinds the query, pathfinder and funnel.

**Installing it is a second decision, not a detail.** The swap does *not* reseed the agents; that
call is yours and it is not optional, because every agent still holds a triangle index and corridor
into the mesh you just replaced and both are hashed frame state. And in a rollback netcode you must
not install where the command was handled at all: the rewind returns the frame and leaves the
NavMesh ahead of it. `FPNavMeshRebakeDriver` owns that problem — it re-derives the installed mesh
from frame state every tick, keeps the reseed on the tick that owns it, spreads a large rebake across
frames, and keeps the two meshes a predicting peer bounces between. **Registering it as a system is
the whole wiring**: the engine paces its slices and re-derives the mesh at world init and after a full
state applies, so a game writes a placement table and an installer and nothing else.

**[Navigation.Rebake.md](Navigation.Rebake.md)** is the guide: footprint types, the placement grid,
what gets rejected, the determinism envelope, installing from a command stream, and measured costs.

## NavMesh Export & Visualization *(Editor)*

Both engines ship an editor exporter and visualizer; they share the geometry pipeline (`FPNavMeshBuildPipeline`) and produce the same `.bytes` binary format.

**Unity** — `Tools > Klotho > Export NavMesh` exports the current scene's Unity NavMesh; visualization is at `Tools > Klotho > Visualizer > NavMesh`.

**Godot** — `Project > Tools > Klotho: Export FPNavMesh` exports the selected `NavigationRegion3D` (output: `<scene_dir>/<RegionName>.NavMeshData.bytes` + `.json` sidecar); the visualizer toggles at `Project > Tools > Klotho: NavMesh Visualizer` — see [NavMeshVisualizer.Godot.md](NavMeshVisualizer.Godot.md).

**Both visualizers can also place buildings on the loaded mesh and rebake it**, carved or retained,
and then run the agent simulator over the result — including per-agent plan/walk masks, so two agents
that differ only in their walk mask can be watched splitting at a footprint edge. The shapes belong
to the tool, not to a game. The data layer behind it is the runtime's `FPNavMeshPlacementProbe`, so
both editors drive one code path; the Godot side is documented in
[NavMeshVisualizer.Godot.md](NavMeshVisualizer.Godot.md).

Shared bake steps:
- Vertex welding (WELD_EPSILON = 0.001)
- Degenerate-triangle removal + T-junction split
- Automatic adjacency + portal build
- Spatial-grid build (default cell size = 4.0)

> Each engine bakes its **own** scene; the resulting `.bytes` is not interchangeable across engines (coordinate-handedness). Load it on the engine that produced it.

> **Steep walkable slopes**: multi-floor traversal compares the **representative-height difference** of edge-adjacent triangles (`MultiFloorYThreshold`, default 2.0); a difference above the threshold is treated as a separate floor and blocked. A ramp baked as **one large triangle** whose Y-span exceeds ~2× the threshold therefore cannot stay within the threshold of *both* its lower and upper neighbour, so one side becomes impassable. Bake with finer tessellation (Godot NavigationMesh `edge_max_length` ≤ ~3; `agent_max_slope` bounds the per-triangle rise) so a ramp splits into triangles whose representative heights step gradually.

## Crowd Scaling

`FPNavAgentSystem.Update(ref frame, entities, count, tick, dt)` is **not engine-scheduled**. The game
collects the array and calls it, so who is in it, in what order, and how many calls a tick takes are
all decisions you already own. That is the whole lever this section is about — no engine change is
involved in any of it.

### What a move order costs

Measured with `FPNavAgentCrowdPerfTests` (`[Explicit]`, Release, warmup 32) on a 96×96-cell open
field — 192×192 world units, 18,432 triangles — with agents on a 1.5-unit lattice. Median ms per
tick, .NET 8 on one desktop machine: read the **shape**, not the absolute numbers, and re-measure on
your target before budgeting against them.

| Agents | A* storm tick | ORCA + correction | path follow + movement | `Frame.CopyFrom` |
|---:|---:|---:|---:|---:|
| 64 | 41.8 | 0.47 | 0.34 | 0.002 |
| 256 | 254.5 | 2.31 | 1.35 | 0.009 |
| 800 | **968.8** | 12.21 | 4.23 | 0.028 |
| 3200 | **4230.2** | 136.39 | 15.13 | 0.108 |

Two things in that table are worth more than the rest:

**The order tick dominates everything else by orders of magnitude.** Steady-state following at 800
agents costs ~16 ms; the tick where all 800 receive the order costs ~970 ms. The repath cooldown
(`PathRepathCooldown`, default 10 ticks) does not help — it only gates agents that have *already*
repathed, so an idle army all fires on the same tick.

**Most of that second is spent failing.** The diagnostic counters say so directly: across the
measured runs at 800 agents, 11,200 searches produced 6,542 corridor clamps and 4,658 budget
exhaustions — every single search hit one cap or the other, and the ~42% that exhausted
`MAX_ITERATIONS` returned **no path at all**, leaving those units in `PathFailed` with nothing
logged. A cross-map order on a field this size is past the built-in ceiling, and before these
counters existed that fact was invisible from outside the engine.

| Agents | searches | corridor-clamped | budget-exhausted (no path) |
|---:|---:|---:|---:|
| 64 | 896 | 896 | 0 |
| 256 | 3,584 | 3,233 | 351 |
| 800 | 11,200 | 6,542 | 4,658 |
| 3200 | 44,800 | 15,086 | 26,992 |

So the first thing to build for hundreds of units is not a faster A* — it is **not calling A* for
everyone at once**: a bounded admission queue (promote K destinations per tick), and for
same-destination groups a flow field, which needs nothing from the engine that is not already public
(see below).

### Splitting the array into clusters

Calling `Update` once per spatial cluster turns the ORCA neighbour scan from O(N²) into O(Σnᵢ²).
Measured at 800 agents, steady state:

| Cluster size | Calls | Median ms | Agents actually position-corrected |
|---:|---:|---:|---:|
| 16 | 50 | **6.35** | 800 |
| 32 | 25 | 7.30 | 800 |
| 64 | 13 | 9.94 | 800 |
| 128 | 7 | 10.54 | 448 |
| 256 | 4 | 11.57 | 256 |
| 800 (single call) | 1 | 16.28 | **64** |

Smaller clusters win on both axes at once: the tick gets cheaper *and* more agents come out of the
correction pass separated, because `MAX_AGENTS` is a per-call cap — 13 calls of 62 correct all 800,
while one call of 800 corrects 64 and drops the other 736 silently. At 3200 agents the same split is
5.1× cheaper (152.0 → 30.0 ms).

**It is not lossless, and the cost is not in the table.** In `ComputeNewVelocity` the update set *is*
the neighbour-candidate set, so agents stop seeing each other across a cluster boundary and avoidance
is cut there. Nothing in the engine can express "update this agent, but consider that one as a
neighbour" today. Put boundaries where density is low, prefer larger clusters where crowding matters,
and treat the sweep above as a cost curve to trade against quality — not as a recommendation to
cluster as small as possible.

### A worked partitioner

`FPNavAgentClusterSplitSampleTests` is the rule above as code you can copy — sort agents by
`(cell z, cell x, entity index)`, break a run at every cell change and again at `MAX_AGENTS`, and
call `Update` once per run. Three details in it are load-bearing:

- **The cell index is a shift, not a division.** Pick the cell size as a power of two in world
  units and the index is `position.RawValue >> (FP64.FRACTIONAL_BITS + log2)` — exact integer
  arithmetic, and a shift floors correctly on negative coordinates, so an agent at `x = -0.1` lands
  in cell −1 instead of sharing cell 0 with `x = +0.1`.
- **The sort key ends in the entity index**, which makes it a total order. Leave ties open and the
  sort implementation decides the array order — and the array order is simulation input.
- **Break runs at cell boundaries first, and at the cap second.** Cutting the sorted sequence every
  `MAX_AGENTS` agents instead merges whole cells into one call whenever cells are smaller than the
  cap, and that produces clusters *wider* than plain index chunking (measured on the fixture's
  layout: worst cluster bounding box 89 vs 45 world units², where breaking at cells gives 9.7).
  The cap is a ceiling, not a target.

The cell size is the only dial: cells hold whatever local density puts in them, so a smaller cell
means more, smaller calls. It is also what the enumeration-order property rests on — the partition
is a function of positions and entity ids, so a peer that walks its entities in another order still
produces the same runs in the same order and converges. Index chunking does not: the fixture pins
both, the spatial rule converging and index chunking diverging under a reversed input array.

### The partition is a determinism input

Not a local optimisation. ORCA breaks coincident ties on **array index**, and the correction pass
takes the **first `MAX_AGENTS`** of whatever it is handed, so changing the partition changes the
simulation. Two peers that cluster differently diverge, and both results look internally consistent.

- **Required**: the rule is a pure function of frame state (e.g. sort by grid cell, then by entity
  index). `FPSpatialGrid.GetPairs` sorting after cell traversal is the idiom this repo already uses.
- **Forbidden**: wall-clock, local input, hash-map iteration order, camera or visibility.
- **Partition, not overlap**: an agent in two arrays moves twice in one tick.
- **A path-admission cursor lives in frame state or nowhere.** `FPNavAgentSystem` is not an
  `ISnapshotParticipant`, so a round-robin cursor kept in a system field is not restored on rollback
  and resimulation diverges. Derive it from the tick (`index % K == tick % K`) or keep it in a game
  component.

Multiple `Update` calls in one tick are safe: no scratch state carries between calls (the only
instance state that survives is visit-stamp generations and diagnostic counters, and neither affects
results).

### Diagnostic counters

`FindPath` returns a bare `false` for five different reasons and logs only one of them (an endpoint
off the mesh). The counters below name the other four, and the correction-pass cap besides — all as
diagnostic fields outside the state hash, the wire and replay:

| Counter | Owner | Reports |
|---|---|---|
| `DebugCollisionResolveTruncatedCount` | `FPNavAgentSystem` | Agents dropped from the correction pass past `MAX_AGENTS` |
| `DebugCorridorTruncatedCount` | `FPNavMeshPathfinder` | Paths clamped to `MAX_CORRIDOR` (the far end is dropped, so the agent runs off the corridor and repaths) |
| `DebugIterationExhaustedCount` | `FPNavMeshPathfinder` | Searches that failed with work still queued — the budget ran out, as opposed to there being no route |
| `DebugBlockedEndpointCount` | `FPNavMeshPathfinder` | Start or end triangle flagged `isBlocked` — the search never started |
| `DebugAreaMaskRejectedCount` | `FPNavMeshPathfinder` | The requested `areaMask` shares no bit with the **end** triangle. `FindPath` only — the surface walk applies the same filter but treats a rejection as a wall rather than a failure, so it has nothing to count |
| `DebugMaskedStartCount` | `FPNavMeshPathfinder` | The search **started** on ground the mask forbids. Not a failure: the start is exempt and the escape rule carries the path out, so this is the only trace that the agent was inside a building at all |

They accumulate over the owning instance's lifetime and are **not** rollback-aware: a resimulated
tick is counted again. They are never reset — take two readings if you want a delta. Across a
navmesh swap the two overloads differ: `SwapNavMesh(mesh)` rebinds the existing pathfinder, so the
pathfinder totals carry across a runtime rebake (this is the path `FPNavAgentInstaller` uses), while
the four-argument overload installs a caller-built one and starts its totals over.
`DebugCollisionResolveTruncatedCount` lives on the agent system and survives either. `DebugIterationExhaustedCount` deliberately keys off the open set rather than
the iteration count, so a search that drains its frontier exactly at the budget is reported as "no
route", not as a truncation.

`DebugBlockedEndpointCount` is worth watching in particular, because it counts the one silent failure
a game creates **deliberately**. Nothing in the engine sets `isBlocked` — the runtime rebaker carves
geometry away rather than blocking it, and a destination on carved ground is *off-mesh*, which logs.
The flag has exactly one producer: `FPNavMesh.TrianglesMutable`, the door this API opens for closing
a gate at runtime. So a rising count means units are being ordered through something the game shut,
and until now that produced the same wordless `false` as "there is no route".

`DebugAreaMaskRejectedCount` is a **plan-side** instrument: it counts **end**-triangle refusals
inside `FindPath` and nothing else, so an agent whose PLAN mask admits buildings never trips it
however often its walk is refused — that half is what `FPNavAgentStatus.Blocked` is for. It moves
through `FPNavAgentSystem` in exactly one case: a **destination** inside a **retained building
footprint**, which `DEFAULT_AREA_MASK` excludes (see the area filter above). A masked-out *start* no
longer refuses the call and no longer lands here — it is reported in `DebugMaskedStartCount`
instead. On a mesh without retained buildings both count direct callers of the pathfinder only.

Note that a non-zero `DebugCollisionResolveTruncatedCount` is only *observable* for
position-authoritative consumers: the correction pass writes `NavAgentComponent.Position` and nothing
else, so an integration driving the character from `Velocity` (the Brawler sample does) never sees
its effect either way.

### Flow fields stay on the game side

For same-destination groups the answer to the A* storm is one Dijkstra pass over the triangle graph
plus a per-triangle next-hop, and everything that needs is already public: `FPNavMesh.Triangles` with
`neighbor0..2`, `centerXZ`, `isBlocked`, `costMultiplier` and `areaMask`. It stays out of the core
because the cost function and grouping policy differ per game, and anything the core owns becomes a
**state-hash input** the game can no longer change.

Flyers have no off-mesh link concept — keep them out of the `entities` array entirely and steer them
directly.

---

## Constants

| Constant | Location | Value | Description |
| ---- | ---- | -- | ---- |
| `MAX_AGENTS` | `FPNavAgentSystem` | 64 | Max agents the position-correction pass separates per `Update` call |
| `MAX_CORRIDOR` | `NavAgentComponent`, `FPNavMeshPathfinder` | 128 | Max length of A* / agent corridor |
| `MAX_WAYPOINTS` | `FPNavMeshFunnel` | 64 | Max number of Funnel waypoints |
| `MAX_ITERATIONS` | `FPNavMeshPathfinder` | 4096 | Max A* iterations |
| `MAX_ORCA_LINES` | `FPNavAvoidance` | 64 | Max ORCA half-planes (obstacle + agent) |
| `MAX_OBST_LINES` | `FPNavAvoidance` | 48 | Max obstacle half-planes (= `MAX_ORCA_LINES − MAX_NEIGHBORS`; reserves agent-line slots) |
| `MAX_NEIGHBORS` | `FPNavAvoidance` | 16 | Max ORCA neighbor agents |
| `BFS_FRONTIER_CAP` | `FPNavAgentSystem` | 256 | Frontier/visited bound of the graph-local obstacle query (overflow reported by `DebugBfsFrontierOverflowCount`) |
| `DEFAULT_AREA_MASK` | `FPNavAgentSystem` | `FPNavMeshAreas.DEFAULT_AGENT_MASK` | The mask a path or walk gets when the agent names none — i.e. what a **zero** `PlanAreaMaskOverride`/`WalkAreaMaskOverride` resolves to |
| `BUILDING_AREA` | `FPNavMeshAreas` | 1 | Area index the rebaker stamps onto retained building footprints; reserved — the build pipeline refuses a bake that uses it |
| `BUILDING_MASK` | `FPNavMeshAreas` | `1 << 1` | A retained triangle's `areaMask`, exclusively |
| `DEFAULT_AGENT_MASK` | `FPNavMeshAreas` | `~BUILDING_MASK` | Every area except the building's |
| `ALL_AREAS` | `FPNavMeshAreas` | `~0` | Every area, retained footprints included — for a planner allowed to route through buildings |

---

*Last updated: 2026-09-04 (0.12.0) — `FPNavMeshPlacementProbe` and `FPNavMeshAreas` added to the component list and file layout, the NavMesh visualizers gained a building-placement tool on both editors, and ORCA now treats an agent standing exactly on an obstacle corner as an ordinary position. (2026-09-03 — an agent standing on ground its mask forbids can now leave it (the start is exempt in `FindPath`, and both the walk and the A\* expand a refused neighbour when the triangle they expand from is refused too; `DebugMaskedStartCount` reports it, and `Blocked` now requires that the agent actually asked to move), per-agent area masks (`PlanAreaMaskOverride`/`WalkAreaMaskOverride`, zero = no override, `SetAreaMask`, `FPNavAgentStatus.Blocked`) and, earlier the same day, the building area: `FPNavMeshAreas` (index 1 reserved and stamped onto retained footprints), `DEFAULT_AREA_MASK` now excludes it, and `DebugAreaMaskRejectedCount` can move through the agent path. (2026-09-02 — crowd scaling: a worked spatial partitioner (`FPNavAgentClusterSplitSampleTests`) with the three details that make it safe to copy; the four measured costs of a move order at 64/256/800/3200 agents, the cluster-split call pattern and its sweep, the determinism rules the partition has to meet, and the three diagnostic counters that make the caps visible (`MAX_AGENTS` and `BFS_FRONTIER_CAP` added to the constants table). (2026-08-18: the rebake driver is self-wiring: registering the system is the whole wiring, and `KlothoEngine` owns slice pacing plus the corrections at world init and after a full-state apply; `FPNavMeshPlacementValidator` gives the command path the driver’s own derivation, order and audits. (2026-08-17: delayed install: `FPNavMeshRebakeDriver` derives the installed mesh from frame state each tick (rollback-safe), time-sliced rebake across frames, two-mesh boundary cache, `FPNavAgentInstaller` swap/reseed protocol.) (2026-08-12: runtime NavMesh rebake (deterministic re-triangulation from building footprints, shape catalog, placement grid) — see [Navigation.Rebake.md](Navigation.Rebake.md).) (2026-07-23: graph-local obstacle query (BFS multi-floor/ramp, bake-slope climb cap), clearance tuning (`ObstacleRadiusInset` auto-applied from the recorded bake-settings block), position-correction pass, non-convex/dual-source extraction.) (2026-07-22: ORCA static obstacles — hard-constraint LP3, `FPNavMeshObstacleExtractor`, `LoadNavMeshObstacles()`, `MAX_OBST_LINES`.)*
