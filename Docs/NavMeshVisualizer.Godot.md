# Godot NavMesh Visualizer — User Guide

An editor tool that visualizes a serialized `FPNavMesh` (`.bytes`) in the Godot 3D viewport and lets you validate pathfinding and agent simulation.

> Target: `com.xpturn.klotho` Godot adapter · **Godot 4.x mono (.NET)** · editor-only (`#if TOOLS`)
> Related: [Navigation.md](Navigation.md) · NavMesh exporter (`Klotho: Export FPNavMesh`)

---

## 1. Prerequisites

1. **Godot mono (.NET) build** + an installed `dotnet` SDK.
2. The **Klotho addon must be enabled** (`Project > Project Settings > Plugins` → enable the Klotho plugin). The addon entry point is [`plugin.gd`](../com.xpturn.klotho/Godot~/plugin.gd).
3. The **C# solution must have been built once** (`Project > Tools > C#: Build`, or it builds on first run). The visualizer is a C# `[Tool]` class, so the assembly must be built for the menu to work.
4. A **3D scene must be open in the editor** — overlay geometry is attached as temporary nodes under the edited scene root. With no scene open the dock and info still appear, but 3D geometry is not shown (a warning is printed).
5. An input `.bytes` file — produced by the NavMesh exporter (`Klotho: Export FPNavMesh`, run with a `NavigationRegion3D` selected) at `<scene_dir>/<RegionName>.NavMeshData.bytes`. Sample: [`Samples/GodotPolySample/NavigationRegion3D.NavMeshData.bytes`](../Samples/GodotPolySample/NavigationRegion3D.NavMeshData.bytes).

---

## 2. Open / Close

- Click the top menu **`Project > Tools > Klotho: NavMesh Visualizer`** to toggle it.
- When on, an **`FPNavMesh`** dock appears on the right and the 3D viewport overlay and input become active.
- Clicking again removes the dock and overlay and stops intercepting input (the editor returns to default behavior).

> While the tool is off it does not intervene in 3D viewport input/drawing at all.

---

## 3. Loading a NavMesh (`NavMesh Data` section)

1. Enter the `.bytes` `res://` path in the text field (e.g. `res://NavigationRegion3D.NavMeshData.bytes`).
2. Click **`Load`** → after parsing, geometry is shown in the viewport and the vertex / triangle / grid / blocked / boundary & internal edge counts are shown as labels.
3. Click **`Unload`** to clear it.

> You can sanity-check load integrity by comparing the counts against the exporter's sidecar `.json` (e.g. `NavigationRegion3D.NavMeshData.json`).

---

## 4. Visualization Layers (`Visualization Layers`)

Toggle on/off with checkboxes. Geometry layers are re-drawn immediately.

| Toggle | Shows |
|---|---|
| Triangles | Triangle fill (blue; blocked = red) |
| Edges / Boundary | Internal edges / boundary edges |
| Vertices | Vertex markers (line cross) |
| Tri Indices | Triangle-index labels (2D overlay) |
| Centers | Triangle center points |
| Blocked | Whether blocked triangles are highlighted |
| Cost Heatmap | `costMultiplier` gradient (green→red) |
| *(automatic)* Retained footprints | Triangles a **retained** building occupies are shaded apart from ordinary ground — walkable geometry stamped `FPNavMeshAreas.BUILDING_MASK`. Read off the mesh, so it needs nothing from the game; without it retain leaves no visual trace at all |
| Obstacle Rings | ORCA static-obstacle rings extracted from the boundary — flat XZ footprint with per-vertex convex/reflex markers and CW/CCW winding (the same data the runtime obstacle layer feeds to `FPNavAvoidance`) |

> Labels (Tri Indices · Cell Labels · agent `#i`) are drawn in 2D over the 3D view and appear once the camera is captured — i.e. **after the mouse has entered the 3D viewport once**. Labels are drawn only within a fixed distance (~40m) of the camera.

---

## 5. Pathfinding (`Pathfinding`)

1. Press one of **`Set Start`** / **`Set End`** / **`Inspect`** to enter that mode (press again to exit).
2. **`Shift` + left-click in the 3D viewport** → sets the point on the NavMesh.
3. Once both start and end are set, the path is **found automatically**; use **`Find Path`** to re-run manually and **`Clear Path`** to reset.
4. **`Area mask: all areas (through buildings)`** decides which mask `Find Path` uses. Off (the default) is `FPNavAgentSystem.DEFAULT_AREA_MASK`, which excludes the building area, so a retained footprint is a wall and the corridor routes around it; on is `FPNavMeshAreas.ALL_AREAS`, which plans straight through. Same mesh, opposite answers — toggling it re-runs the search immediately.
5. The result (corridor triangle count · waypoint count) is shown, with **Corridor / Waypoints / Portals** toggles to control the display.
6. In `Inspect` mode, Shift+click a triangle to show its details (vertex indices · neighbors · areaMask · cost · blocked · area) in the `Info` section.

---

## 6. Buildings (`Buildings`)

Places buildings on the loaded mesh and rebakes it — the same runtime rebaker a game drives, so what
the tool refuses is what the game would refuse. The shapes belong to the **tool**, not to any game: a
2×1 box and a hexagon.

1. Press **`Place Building`** to enter the mode, then **`Shift` + left-click** in the viewport to
   place one. The mesh is rebaked immediately and everything re-draws.
2. **`Retain (keep ground)`** picks the mode. Carved leaves a hole; retained keeps the footprint as
   walkable ground stamped as a building — the shaded triangles in the viewport, and the reason the
   agents' default mask treats it as a wall (see [Navigation.md § The area filter](Navigation.md#the-area-filter)).
3. **`Snap to tiling lattice (flush)`** puts the centre where footprints pack flush against each
   other. Those spacings are not round numbers, so a free-hand click otherwise leaves millimetres of
   walkable ground between two buildings.
4. **`Shape`** (Box / Hexagon) and, for the box only, **`Turn`** — the hexagon has no orientation in
   the catalog, because no integer hexagon is symmetric under 60°.
5. **`Boundary`** (`Reject` / `Touch` / `ClipOverlap`) and **`Allow contact`** are the placement
   rules. `Touch` is the tool's default rather than a game's `ClipOverlap`, because under
   `ClipOverlap` a **retained** footprint that crosses the walkable boundary is refused while a carve
   of the same footprint is clipped — worth seeing on purpose rather than by accident.
6. The list shows each placement as `#i  carve|retain  shape  (x, z)`; **`Remove last`** undoes one
   and **`Revert to loaded mesh`** drops them all.

> A refusal is normal use, not an error: pointing at ground you cannot build on prints
> `Refused: <reason>` under the list and the placement is not added. The reasons are the rebaker's
> own (`BuildingsOverlap`, `TouchesWalkableBoundary`, …) — see
> [Navigation.Rebake.md § 4.5](Navigation.Rebake.md#45-what-gets-rejected).

> The agent simulator follows the rebaked mesh: a placement pauses the simulation, swaps the mesh at
> a tick boundary and resumes it, so agents already walking re-plan against the new geometry. That is
> the same swap-then-reseed protocol a game performs — placing a building under a running crowd is
> exactly the scenario worth watching here.

## 7. Agent Simulation (`Agent Simulation`)

Drives the deterministic `FPNavAgentSystem` directly in the editor.

- **Playback**: `▶ Play` (toggles pause) · `Step` (1 tick) · `Reset` · current `Tick` readout.
- **`Sim Speed`** slider (0.25–4×). Advances on a fixed dt 1/60 accumulator.
- **Agent defaults**: `Speed` · `Radius` · `Accel` spinboxes, `Avoidance` (ORCA) checkbox.
- **Placement**: `Place Agent` mode + Shift+click to add; `Set Dest` mode + Shift+click to set the selected agent's destination.
- **Spawn by coordinates**: enter `x, y, z` on the two lines (start / destination), then `Spawn` (clears existing agents and spawns one). `Remove All` clears everything.
- **Selected agent's area masks**: two checkboxes — `plan: all areas` (what it may route through) and `walk: all areas` (what it may enter) — then `Apply masks to selected agent`. Pick the agent in the viewport first; the list row shows each agent's pair as `[plan/walk]`. Applying also drops the corridor the old masks planned, so the agent replans on the next tick.

  The pair worth setting is **plan on, walk off**: the path is drawn straight through a retained building and the agent walks into it and stops, showing `Blocked` in the list. Give it a destination **inside** the footprint — with both endpoints outside, A\* routes around a small footprint whichever mask it has, and the run proves nothing. Ticking `walk` afterwards and re-applying releases it. Note that `Spawn` clears every agent, so build a mixed pair with `Place Agent` instead. See [Navigation.md § The area filter](Navigation.md#the-area-filter) for all four combinations.
- **Display toggles**: `Agents` (disc) · `Paths` (corridor + corner lines) · `Velocity` (actual/desired velocity arrows) · `ORCA` (avoidance half-plane lines).

> While the simulation runs, dynamic meshes and labels are updated every tick. When the tool is inactive, ticks stop.

---

## 8. Spatial Grid / Info (`Spatial Grid` · `Info`)

- **Grid Lines / Cell Labels** toggles. The hovered cell is highlighted and `(col, row) - triangle count` is shown.
- **Info**: shows details of the selected (Inspect) or hovered triangle.

---

## 9. Limitations / Notes

- **PlayMode runtime agent visualization is not supported** — only editor simulation (based on the loaded `.bytes`) is provided.
- **Coordinate space**: raycasts and queries are in the simulation coordinate space. Picking is accurate only when the NavMesh was baked in the simulation coordinate space.
- **Steep walkable slopes (ramps)**: multi-floor traversal compares the **representative-height difference** of edge-adjacent triangles (`MultiFloorYThreshold`, default 2.0) — a difference above the threshold is treated as a separate floor and blocked. So a **single triangle with a large Y-span** (a ramp baked as one polygon) can exceed the threshold against the adjacent flat triangle and **the agent may fail to cross it**. In particular, if one ramp connects a lower and an upper flat area and its Y-span exceeds **about 2× the threshold**, no single representative height can be within the threshold of both neighbors, so one side is necessarily blocked. → Fix: bake with finer tessellation — lower the NavigationMesh **`edge_max_length` (recommended ≤ 3)** so the ramp splits into several triangles whose representative heights step gradually (`agent_max_slope` bounds the per-triangle rise, so this is safe in practice). *Diagnostic: if raising the dock's `Floor Y Thr` (an editor-simulation-only knob) makes it pass, this is the case.*

---

## 10. Troubleshooting

| Symptom | Check |
|---|---|
| Menu `Klotho: NavMesh Visualizer` is missing | Addon enabled + `C#: Build` run once? |
| Nothing shows in 3D after Load | Is a **3D scene open** (no scene → geometry not attached)? Is the path a valid `res://`? Is the `.bytes` non-empty? |
| `No triangles` / empty data | Did you **bake** the NavMesh before exporting? |
| Shift+click does nothing | Is the tool on? Is a mode button active? Is the click point on the NavMesh? |
| Labels not visible | Has the mouse entered the 3D viewport once (camera cache)? Within ~40m? Tri Indices / Cell Labels toggled on? |
| Agent can't cross a steep ramp/slope | Was the ramp baked as one large triangle? Lower the NavMesh `edge_max_length` (≤3) to subdivide and re-export (§9). *Diagnostic*: raise the dock `Floor Y Thr` (e.g. 5) — if it then passes, this is the case. |
| `Place Building` prints `Refused: …` | Normal — the rebaker refused that spot. `BuildingsOverlap` = it hits one already down; `TouchesWalkableBoundary` = it reaches the edge of the walkable region (and under `ClipOverlap` a **retained** footprint is refused there where a carve would be clipped); see [Navigation.Rebake.md § 4.5](Navigation.Rebake.md#45-what-gets-rejected). |
| A retained building looks like ordinary ground | It is ordinary ground — that is the point. Check the shading (§4) and inspect a triangle: a retained footprint's `areaMask` is `BUILDING_MASK`. Whether agents may enter it is the mask question, not the geometry (§5, §7). |
| Triangles/edges show but **Obstacle Rings** don't after Load | The boundary may be non-manifold — obstacle extraction is isolated in a try/catch, so the mesh still renders; check the Godot console for an `Obstacle ring extraction failed` warning. |

---

*Tool entry point: `Project > Tools > Klotho: NavMesh Visualizer` · implementation: [`com.xpturn.klotho/Godot~/Adapters/Editor/GodotFPNavMeshVisualizer*.cs`](../com.xpturn.klotho/Godot~/Adapters/Editor/)*
