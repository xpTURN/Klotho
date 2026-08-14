// Dock Control for the FPNavMesh visualizer: a retained Control tree (load / layers / pathfinding /
// agents / grid / info). EditorPlugin-only operations (UpdateOverlays / dock add-remove) are routed
// through the controller (GodotFPNavMeshVisualizer).
#if TOOLS
using System;
using System.Globalization;

using global::Godot;

namespace xpTURN.Klotho.Godot
{
    [Tool]
    [GlobalClass]
    public partial class GodotFPNavMeshVisualizerDock : VBoxContainer
    {
        private GodotFPNavMeshVisualizer _ctrl;

        private LineEdit _pathEdit;
        private Label _counts;
        private Label _pathInfo;
        private Label _tick;
        private Label _agentList;
        private Label _hoverCell;
        private Label _triInfo;
        private Button _playBtn;
        private LineEdit _spawnStart;
        private LineEdit _spawnDest;

        public void Init(GodotFPNavMeshVisualizer ctrl)
        {
            _ctrl = ctrl;
            Name = "FPNavMesh";
            CustomMinimumSize = new Vector2(280, 0);

            BuildNavMeshSection();
            BuildLayersSection();
            BuildPathfindingSection();
            BuildAgentsSection();
            BuildGridSection();
            BuildInfoSection();

            Refresh();
        }

        public void Refresh()
        {
            var data = _ctrl.Data;
            var sim = _ctrl.AgentSim;
            var ov = _ctrl.Overlay;
            var it = _ctrl.Interaction;

            if (data.IsLoaded)
            {
                int blocked = 0;
                for (int i = 0; i < data.NavMesh.Triangles.Length; i++)
                    if (data.NavMesh.Triangles[i].isBlocked) blocked++;
                _counts.Text =
                    $"Vertices: {data.NavMesh.Vertices.Length}\n" +
                    $"Triangles: {data.NavMesh.Triangles.Length}\n" +
                    $"Grid: {data.NavMesh.GridWidth} x {data.NavMesh.GridHeight} (cell {data.NavMesh.GridCellSize.ToFloat():F1})\n" +
                    $"Blocked: {blocked}   Boundary: {data.BoundaryEdges.Count}   Internal: {data.InternalEdges.Count}";
            }
            else
            {
                _counts.Text = "(not loaded)";
            }

            _pathInfo.Text = data.HasPath
                ? $"Path: OK   Corridor: {data.CorridorLength}   Waypoints: {data.WaypointCount}"
                : $"Start: {(data.HasStart ? Fmt(data.StartPoint) : "-")}   End: {(data.HasEnd ? Fmt(data.EndPoint) : "-")}";

            _tick.Text = $"Tick: {sim.CurrentTick}";
            _playBtn.Text = sim.IsRunning ? "■ Pause" : "▶ Play";

            if (sim.AgentCount > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sim.AgentCount; i++)
                {
                    var rd = sim.GetAgentRenderData(i);
                    string extra = !rd.hasDestination ? " [No Dest]"
                        : !rd.hasPath ? " [No Path]"
                        : rd.currentTriangleIndex < 0 ? " [Off Mesh]" : "";
                    sb.Append($"#{i}: {rd.status}{extra} {Fmt(rd.position)}\n");
                }
                _agentList.Text = sb.ToString();
            }
            else
            {
                _agentList.Text = "(no agents)";
            }

            // Info (selected or hovered triangle)
            int idx = it.SelectedTriangleIndex >= 0 ? it.SelectedTriangleIndex : it.HoveredTriangleIndex;
            if (data.IsLoaded && idx >= 0 && idx < data.NavMesh.Triangles.Length)
            {
                ref readonly var tri = ref data.NavMesh.Triangles[idx];
                string n0 = tri.neighbor0 >= 0 ? tri.neighbor0.ToString() : "boundary";
                string n1 = tri.neighbor1 >= 0 ? tri.neighbor1.ToString() : "boundary";
                string n2 = tri.neighbor2 >= 0 ? tri.neighbor2.ToString() : "boundary";
                _triInfo.Text =
                    $"Triangle {idx}\n" +
                    $"verts: v0={tri.v0} v1={tri.v1} v2={tri.v2}\n" +
                    $"neighbors: {n0}, {n1}, {n2}\n" +
                    $"areaMask: {tri.areaMask}   cost: {tri.costMultiplier.ToFloat():F2}\n" +
                    $"blocked: {(tri.isBlocked ? "Yes" : "No")}   area: {tri.area.ToFloat():F4}";
            }
            else
            {
                _triInfo.Text = "Hover or Shift+Inspect a triangle.";
            }

            if (it.HoveredCell.col >= 0 && data.IsLoaded && data.NavMesh.IsCellValid(it.HoveredCell.col, it.HoveredCell.row))
            {
                data.NavMesh.GetCellTriangles(it.HoveredCell.col, it.HoveredCell.row, out _, out int count);
                _hoverCell.Text = $"Hovered cell: ({it.HoveredCell.col}, {it.HoveredCell.row}) - {count} tris";
            }
            else
            {
                _hoverCell.Text = "Hovered cell: -";
            }
        }

        #region sections

        private void BuildNavMeshSection()
        {
            AddChild(Header("NavMesh Data"));
            _pathEdit = new LineEdit { PlaceholderText = "res://NavigationRegion3D.NavMeshData.bytes" };
            AddChild(_pathEdit);

            var row = new HBoxContainer();
            row.AddChild(Btn("Load", () => _ctrl.Load(_pathEdit.Text)));
            row.AddChild(Btn("Unload", () => _ctrl.Unload()));
            AddChild(row);

            _counts = Lbl();
            AddChild(_counts);
            AddChild(new HSeparator());
        }

        private void BuildLayersSection()
        {
            AddChild(Header("Visualization Layers"));
            var ov = _ctrl.Overlay;
            var grid = new GridContainer { Columns = 2 };
            grid.AddChild(Check("Triangles", ov.ShowTriangles, v => { ov.ShowTriangles = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Edges", ov.ShowEdges, v => { ov.ShowEdges = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Boundary", ov.ShowBoundaryEdges, v => { ov.ShowBoundaryEdges = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Obstacle Rings", ov.ShowObstacleRings, v => { ov.ShowObstacleRings = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Vertices", ov.ShowVertices, v => { ov.ShowVertices = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Tri Indices", ov.ShowTriangleIndices, v => { ov.ShowTriangleIndices = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Centers", ov.ShowTriangleCenters, v => { ov.ShowTriangleCenters = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Blocked", ov.ShowBlockedTriangles, v => { ov.ShowBlockedTriangles = v; _ctrl.RequestStaticRedraw(); }));
            grid.AddChild(Check("Cost Heatmap", ov.ShowCostHeatmap, v => { ov.ShowCostHeatmap = v; _ctrl.RequestStaticRedraw(); }));
            AddChild(grid);
            AddChild(new HSeparator());
        }

        private void BuildPathfindingSection()
        {
            AddChild(Header("Pathfinding"));
            var modes = new HBoxContainer();
            modes.AddChild(Btn("Set Start", () => ToggleMode(InteractionMode.SetStart)));
            modes.AddChild(Btn("Set End", () => ToggleMode(InteractionMode.SetEnd)));
            modes.AddChild(Btn("Inspect", () => ToggleMode(InteractionMode.InspectTriangle)));
            AddChild(modes);

            AddChild(Lbl("Shift + Click in the 3D viewport to set."));

            var actions = new HBoxContainer();
            actions.AddChild(Btn("Find Path", () => _ctrl.FindPath()));
            actions.AddChild(Btn("Clear Path", () => _ctrl.ClearPath()));
            AddChild(actions);

            _pathInfo = Lbl();
            AddChild(_pathInfo);

            var ov = _ctrl.Overlay;
            var toggles = new HBoxContainer();
            toggles.AddChild(Check("Corridor", ov.ShowCorridor, v => { ov.ShowCorridor = v; _ctrl.RequestDynamicRedraw(); }));
            toggles.AddChild(Check("Waypoints", ov.ShowWaypoints, v => { ov.ShowWaypoints = v; _ctrl.RequestDynamicRedraw(); }));
            toggles.AddChild(Check("Portals", ov.ShowPortals, v => { ov.ShowPortals = v; _ctrl.RequestDynamicRedraw(); }));
            AddChild(toggles);
            AddChild(new HSeparator());
        }

        private void BuildAgentsSection()
        {
            AddChild(Header("Agent Simulation"));
            var sim = _ctrl.AgentSim;

            var ctrlRow = new HBoxContainer();
            _playBtn = Btn("▶ Play", () =>
            {
                if (sim.IsRunning) sim.Pause(); else sim.Start();
                Refresh();
            });
            ctrlRow.AddChild(_playBtn);
            ctrlRow.AddChild(Btn("Step", () => { sim.Step(); _ctrl.RequestDynamicRedraw(); Refresh(); }));
            ctrlRow.AddChild(Btn("Reset", () => { sim.Reset(); _ctrl.RequestDynamicRedraw(); Refresh(); }));
            // Plain (non-autowrap) label: an autowrap Label inside an HBox inflates the row height,
            // stretching the sibling buttons vertically. Fixed width, vertically centered.
            _tick = new Label { Text = "Tick: 0", CustomMinimumSize = new Vector2(70, 0), VerticalAlignment = VerticalAlignment.Center };
            ctrlRow.AddChild(_tick);
            AddChild(ctrlRow);

            AddChild(SliderRow("Sim Speed", 0.25, 4.0, sim.SimulationSpeed, v => sim.SimulationSpeed = (float)v));
            AddChild(SpinRow("Speed", sim.DefaultSpeed, v => sim.DefaultSpeed = (float)v));
            AddChild(SpinRow("Radius", sim.DefaultRadius, v => sim.DefaultRadius = (float)v));
            AddChild(SpinRow("Accel", sim.DefaultAcceleration, v => sim.DefaultAcceleration = (float)v));
            AddChild(SpinRow("Floor Y Thr", sim.MultiFloorYThreshold, v => sim.SetMultiFloorYThreshold((float)v)));
            AddChild(Check("Avoidance", sim.EnableAvoidance, v => sim.EnableAvoidance = v));
            // Bake inset carried by the loaded mesh boundary (R_sim = R_bake convention → set to
            // the bake Agent Radius; 0 = uncorrected double clearance). Applied live.
            AddChild(SpinRow("Obst Inset", sim.ObstacleRadiusInset, v => sim.SetObstacleRadiusInset((float)v)));

            var modes = new HBoxContainer();
            modes.AddChild(Btn("Place Agent", () => ToggleMode(InteractionMode.PlaceAgent)));
            modes.AddChild(Btn("Set Dest", () => ToggleMode(InteractionMode.SetAgentDest)));
            AddChild(modes);

            AddChild(Lbl("Spawn agent by position (x, y, z)"));
            _spawnStart = new LineEdit { Text = "0, 0, 0" };
            _spawnDest = new LineEdit { Text = "1, 0, 1" };
            AddChild(_spawnStart);
            AddChild(_spawnDest);
            var spawnRow = new HBoxContainer();
            spawnRow.AddChild(Btn("Spawn", OnSpawn));
            spawnRow.AddChild(Btn("Remove All", () => { sim.ClearAllAgents(); _ctrl.RequestDynamicRedraw(); Refresh(); }));
            AddChild(spawnRow);

            var ov = _ctrl.Overlay;
            var toggles = new HBoxContainer();
            toggles.AddChild(Check("Agents", ov.ShowAgents, v => { ov.ShowAgents = v; _ctrl.RequestDynamicRedraw(); }));
            toggles.AddChild(Check("Paths", ov.ShowAgentPaths, v => { ov.ShowAgentPaths = v; _ctrl.RequestDynamicRedraw(); }));
            toggles.AddChild(Check("Velocity", ov.ShowAgentVelocities, v => { ov.ShowAgentVelocities = v; _ctrl.RequestDynamicRedraw(); }));
            toggles.AddChild(Check("ORCA", ov.ShowOrcaLines, v => { ov.ShowOrcaLines = v; _ctrl.RequestDynamicRedraw(); }));
            AddChild(toggles);

            _agentList = Lbl();
            AddChild(_agentList);
            AddChild(new HSeparator());
        }

        private void BuildGridSection()
        {
            AddChild(Header("Spatial Grid"));
            var ov = _ctrl.Overlay;
            var row = new HBoxContainer();
            row.AddChild(Check("Grid Lines", ov.ShowGrid, v => { ov.ShowGrid = v; _ctrl.RequestStaticRedraw(); _ctrl.RequestDynamicRedraw(); }));
            row.AddChild(Check("Cell Labels", ov.ShowGridLabels, v => { ov.ShowGridLabels = v; _ctrl.RequestStaticRedraw(); }));
            AddChild(row);
            _hoverCell = Lbl("Hovered cell: -");
            AddChild(_hoverCell);
            AddChild(new HSeparator());
        }

        private void BuildInfoSection()
        {
            AddChild(Header("Info"));
            _triInfo = Lbl();
            AddChild(_triInfo);
        }

        #endregion

        #region helpers

        private void OnSpawn()
        {
            if (TryParseVector3(_spawnStart.Text, out Vector3 s) && TryParseVector3(_spawnDest.Text, out Vector3 d))
                _ctrl.SpawnAgentByPosition(s, d);
            else
                GD.PushWarning("[GodotFPNavMeshVisualizer] Failed to parse coordinates. Format: x, y, z");
        }

        private void ToggleMode(InteractionMode mode)
        {
            var it = _ctrl.Interaction;
            it.Mode = it.Mode == mode ? InteractionMode.None : mode;
        }

        private static Label Header(string text)
        {
            var l = new Label { Text = text };
            l.AddThemeFontSizeOverride("font_size", 14);
            return l;
        }

        private static Label Lbl(string text = "")
        {
            // No autowrap: labels use explicit '\n'. An autowrap Label inside a container/ScrollContainer
            // triggers a min-size feedback loop that inflates the dock's minimum size and pushes the
            // editor layout around (collapsing the bottom panel).
            return new Label { Text = text };
        }

        private static Button Btn(string text, Action onPress)
        {
            var b = new Button { Text = text };
            b.Pressed += onPress;
            return b;
        }

        private static CheckBox Check(string text, bool initial, Action<bool> onToggle)
        {
            var cb = new CheckBox { Text = text, ButtonPressed = initial };
            cb.Toggled += on => onToggle(on);
            return cb;
        }

        private static HBoxContainer SliderRow(string label, double min, double max, double value, Action<double> onChanged)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) });
            var s = new HSlider { MinValue = min, MaxValue = max, Step = 0.05, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            s.ValueChanged += v => onChanged(v);
            row.AddChild(s);
            return row;
        }

        private static HBoxContainer SpinRow(string label, double value, Action<double> onChanged)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) });
            var sb = new SpinBox { MinValue = 0, MaxValue = 100, Step = 0.1, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            sb.ValueChanged += v => onChanged(v);
            row.AddChild(sb);
            return row;
        }

        private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

        private static bool TryParseVector3(string text, out Vector3 result)
        {
            result = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            if (parts.Length != 3) return false;
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            return false;
        }

        #endregion
    }
}
#endif
