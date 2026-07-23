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
5. An input `.bytes` file — produced by the NavMesh exporter (`Klotho: Export FPNavMesh`, run with a `NavigationRegion3D` selected) at `<scene_dir>/<RegionName>.NavMeshData.bytes`. Sample: [`Samples/GodotP2pSample/NavigationRegion3D.NavMeshData.bytes`](../Samples/GodotP2pSample/NavigationRegion3D.NavMeshData.bytes).

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
| Obstacle Rings | ORCA static-obstacle rings extracted from the boundary — flat XZ footprint with per-vertex convex/reflex markers and CW/CCW winding (the same data the runtime obstacle layer feeds to `FPNavAvoidance`) |

> Labels (Tri Indices · Cell Labels · agent `#i`) are drawn in 2D over the 3D view and appear once the camera is captured — i.e. **after the mouse has entered the 3D viewport once**. Labels are drawn only within a fixed distance (~40m) of the camera.

---

## 5. Pathfinding (`Pathfinding`)

1. Press one of **`Set Start`** / **`Set End`** / **`Inspect`** to enter that mode (press again to exit).
2. **`Shift` + left-click in the 3D viewport** → sets the point on the NavMesh.
3. Once both start and end are set, the path is **found automatically**; use **`Find Path`** to re-run manually and **`Clear Path`** to reset.
4. The result (corridor triangle count · waypoint count) is shown, with **Corridor / Waypoints / Portals** toggles to control the display.
5. In `Inspect` mode, Shift+click a triangle to show its details (vertex indices · neighbors · areaMask · cost · blocked · area) in the `Info` section.

---

## 6. Agent Simulation (`Agent Simulation`)

Drives the deterministic `FPNavAgentSystem` directly in the editor.

- **Playback**: `▶ Play` (toggles pause) · `Step` (1 tick) · `Reset` · current `Tick` readout.
- **`Sim Speed`** slider (0.25–4×). Advances on a fixed dt 1/60 accumulator.
- **Agent defaults**: `Speed` · `Radius` · `Accel` spinboxes, `Avoidance` (ORCA) checkbox.
- **Placement**: `Place Agent` mode + Shift+click to add; `Set Dest` mode + Shift+click to set the selected agent's destination.
- **Spawn by coordinates**: enter `x, y, z` on the two lines (start / destination), then `Spawn` (clears existing agents and spawns one). `Remove All` clears everything.
- **Display toggles**: `Agents` (disc) · `Paths` (corridor + corner lines) · `Velocity` (actual/desired velocity arrows) · `ORCA` (avoidance half-plane lines).

> While the simulation runs, dynamic meshes and labels are updated every tick. When the tool is inactive, ticks stop.

---

## 7. Spatial Grid / Info (`Spatial Grid` · `Info`)

- **Grid Lines / Cell Labels** toggles. The hovered cell is highlighted and `(col, row) - triangle count` is shown.
- **Info**: shows details of the selected (Inspect) or hovered triangle.

---

## 8. Limitations / Notes

- **PlayMode runtime agent visualization is not supported** — only editor simulation (based on the loaded `.bytes`) is provided.
- **Coordinate space**: raycasts and queries are in the simulation coordinate space. Picking is accurate only when the NavMesh was baked in the simulation coordinate space.
- **Steep walkable slopes (ramps)**: multi-floor traversal compares the **representative-height difference** of edge-adjacent triangles (`MultiFloorYThreshold`, default 2.0) — a difference above the threshold is treated as a separate floor and blocked. So a **single triangle with a large Y-span** (a ramp baked as one polygon) can exceed the threshold against the adjacent flat triangle and **the agent may fail to cross it**. In particular, if one ramp connects a lower and an upper flat area and its Y-span exceeds **about 2× the threshold**, no single representative height can be within the threshold of both neighbors, so one side is necessarily blocked. → Fix: bake with finer tessellation — lower the NavigationMesh **`edge_max_length` (recommended ≤ 3)** so the ramp splits into several triangles whose representative heights step gradually (`agent_max_slope` bounds the per-triangle rise, so this is safe in practice). *Diagnostic: if raising the dock's `Floor Y Thr` (an editor-simulation-only knob) makes it pass, this is the case.*

---

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| Menu `Klotho: NavMesh Visualizer` is missing | Addon enabled + `C#: Build` run once? |
| Nothing shows in 3D after Load | Is a **3D scene open** (no scene → geometry not attached)? Is the path a valid `res://`? Is the `.bytes` non-empty? |
| `No triangles` / empty data | Did you **bake** the NavMesh before exporting? |
| Shift+click does nothing | Is the tool on? Is a mode button active? Is the click point on the NavMesh? |
| Labels not visible | Has the mouse entered the 3D viewport once (camera cache)? Within ~40m? Tri Indices / Cell Labels toggled on? |
| Agent can't cross a steep ramp/slope | Was the ramp baked as one large triangle? Lower the NavMesh `edge_max_length` (≤3) to subdivide and re-export (§8). *Diagnostic*: raise the dock `Floor Y Thr` (e.g. 5) — if it then passes, this is the case. |
| Triangles/edges show but **Obstacle Rings** don't after Load | The boundary may be non-manifold — obstacle extraction is isolated in a try/catch, so the mesh still renders; check the Godot console for an `Obstacle ring extraction failed` warning. |

---

*Tool entry point: `Project > Tools > Klotho: NavMesh Visualizer` · implementation: [`com.xpturn.klotho/Godot~/Adapters/Editor/GodotFPNavMeshVisualizer*.cs`](../com.xpturn.klotho/Godot~/Adapters/Editor/)*
