using UnityEditor;
using UnityEngine;

using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Editor window for editing and inspecting NavMesh geometry, agents, and paths.
    /// </summary>
    internal class FPNavMeshVisualizerWindow : EditorWindow
    {
        [MenuItem("Tools/Klotho/Visualizer/NavMesh")]
        public static void ShowWindow()
        {
            var window = GetWindow<FPNavMeshVisualizerWindow>("FPNavMesh Visualizer");
            window.minSize = new Vector2(320, 200);
        }

        // Subsystems
        private FPNavMeshVisualizerData _data;
        private FPNavMeshSceneOverlay _overlay;
        private FPNavMeshInteraction _interaction;
        private FPNavMeshAgentSimulator _agentSim;

        // UI state
        private TextAsset _navMeshAsset;
        private Vector2 _scrollPosition;
        private bool _foldoutNavMesh = true;
        private bool _foldoutLayers = true;
        private bool _foldoutPath = true;
        private bool _foldoutAgents = true;
        private bool _foldoutGrid;
        private bool _foldoutInfo;
        private string _spawnStartText = "0.0, 0.0, 0.0";
        private string _spawnDestText = "1.0, 0.0, 1.0";

        private void OnEnable()
        {
            _data = new FPNavMeshVisualizerData();
            _overlay = new FPNavMeshSceneOverlay();
            _interaction = new FPNavMeshInteraction();
            _agentSim = new FPNavMeshAgentSimulator();

            _overlay.SetData(_data);
            _overlay.SetAgentSimulator(_agentSim);
            _interaction.SetData(_data);

            // Wire up events
            _interaction.OnStartPointSet += OnStartPointSet;
            _interaction.OnEndPointSet += OnEndPointSet;
            _interaction.OnTriangleSelected += OnTriangleSelected;
            _interaction.OnAgentPlaced += OnAgentPlaced;
            _interaction.OnAgentDestinationSet += OnAgentDestinationSet;

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnEditorUpdate;

            if (Application.isPlaying)
                ConnectRuntimeBridge();
        }

        private void OnDisable()
        {
            _interaction.OnStartPointSet -= OnStartPointSet;
            _interaction.OnEndPointSet -= OnEndPointSet;
            _interaction.OnTriangleSelected -= OnTriangleSelected;
            _interaction.OnAgentPlaced -= OnAgentPlaced;
            _interaction.OnAgentDestinationSet -= OnAgentDestinationSet;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;

            _agentSim.Pause();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                ConnectRuntimeBridge();
            else if (state == PlayModeStateChange.ExitingPlayMode)
                DisconnectRuntimeBridge();
        }

        private void ConnectRuntimeBridge()
        {
            var bridge = EcsDebugBridge.Instance;
            if (bridge == null || bridge.NavMesh == null) return;

            _data.LoadFromNavMesh(bridge.NavMesh, bridge.NavQuery);
            _agentSim.Initialize(_data);
            _overlay.SetRuntimeSnapshotBuffer(bridge.AgentSnapshots);
            Repaint();
        }

        private void DisconnectRuntimeBridge()
        {
            _data.Unload();
            _overlay.SetRuntimeSnapshotBuffer(null);
            _overlay.RuntimeAgentSnapshotCount = 0;
            Repaint();
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (Application.isPlaying)
            {
                var bridge = EcsDebugBridge.Instance;
                if (bridge != null)
                    _overlay.RuntimeAgentSnapshotCount = bridge.AgentSnapshotCount;
            }
            else
            {
                _interaction.ProcessSceneInput(sceneView);
                _overlay.HoveredTriangleIndex = _interaction.HoveredTriangleIndex;
                _overlay.HoveredCell = _interaction.HoveredCell;
            }

            _overlay.OnSceneGUI(sceneView);
        }

        private void OnEditorUpdate()
        {
            if (Application.isPlaying)
            {
                var bridge = EcsDebugBridge.Instance;
                if (bridge != null && bridge.AgentSnapshotCount > 0)
                    Repaint();
                return;
            }

            if (_agentSim != null && _agentSim.IsRunning)
            {
                _agentSim.OnEditorUpdate();
                Repaint();
            }
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            if (Application.isPlaying)
            {
                DrawPlayModeHeader();
                DrawLayerToggles();
                DrawRuntimeAgentSection();
                DrawGridSection();
                DrawInfoSection();
            }
            else
            {
                DrawNavMeshSection();
                DrawLayerToggles();
                DrawPathSection();
                DrawAgentSection();
                DrawGridSection();
                DrawInfoSection();
            }

            EditorGUILayout.EndScrollView();
        }

        #region PlayMode

        private void DrawPlayModeHeader()
        {
            var bridge = EcsDebugBridge.Instance;
            if (bridge == null || bridge.NavMesh == null)
            {
                EditorGUILayout.HelpBox("PlayMode: EcsDebugBridge or NavMesh not found.", MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox("PlayMode — Connected to runtime NavMesh.", MessageType.Info);

            if (!_data.IsLoaded)
            {
                if (GUILayout.Button("Connect Runtime NavMesh"))
                    ConnectRuntimeBridge();
                return;
            }

            EditorGUILayout.LabelField("Vertices", _data.NavMesh.Vertices.Length.ToString());
            EditorGUILayout.LabelField("Triangles", _data.NavMesh.Triangles.Length.ToString());
            EditorGUILayout.Space(4);
        }

        private void DrawRuntimeAgentSection()
        {
            _foldoutAgents = EditorGUILayout.Foldout(_foldoutAgents, "Runtime Agents", true);
            if (!_foldoutAgents) return;

            EditorGUI.indentLevel++;

            var bridge = EcsDebugBridge.Instance;
            int count = bridge != null ? bridge.AgentSnapshotCount : 0;

            // Entity.Index input
            _overlay.SelectedEntityIndex = EditorGUILayout.IntField("Entity Index", _overlay.SelectedEntityIndex);

            EditorGUILayout.LabelField($"Active Agents: {count}");

            // Selected agent info
            int foundIdx = -1;
            if (count > 0 && _overlay.SelectedEntityIndex >= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (bridge.AgentSnapshots[i].Entity.Index == _overlay.SelectedEntityIndex)
                    {
                        foundIdx = i;
                        break;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                ref NavAgentSnapshot snap =
                    ref bridge.AgentSnapshots[i];

                string dest = snap.HasDestination
                    ? FormatVector3(snap.Destination.ToVector3())
                    : "[No Dest]";

                string path = snap.HasPath ? "" : " [No Path]";

                bool isSelected = i == foundIdx;
                var prevBg = GUI.backgroundColor;
                if (isSelected)
                    GUI.backgroundColor = Color.cyan;
                EditorGUILayout.LabelField(
                    $"#{snap.Entity.Index}  Pos:{FormatVector3(snap.Position.ToVector3())}  Dest:{dest}{path}",
                    isSelected ? EditorStyles.boldLabel : EditorStyles.label);
                GUI.backgroundColor = prevBg;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region NavMesh data

        private void DrawNavMeshSection()
        {
            _foldoutNavMesh = EditorGUILayout.Foldout(_foldoutNavMesh, "NavMesh Data", true);
            if (!_foldoutNavMesh) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            _navMeshAsset = (TextAsset)EditorGUILayout.ObjectField(
                "NavMesh File", _navMeshAsset, typeof(TextAsset), false);

            if (GUILayout.Button("Load", GUILayout.Width(50)) && _navMeshAsset != null)
            {
                _data.Unload();
                _agentSim.Reset();

                if (_data.LoadFromBytes(_navMeshAsset.bytes))
                {
                    _agentSim.Initialize(_data);
                    // Move SceneView camera to NavMesh center
                    FocusSceneViewOnNavMesh();
                }
            }

            if (GUILayout.Button("Unload", GUILayout.Width(60)))
            {
                _data.Unload();
                _agentSim.Reset();
            }
            EditorGUILayout.EndHorizontal();

            if (_data.IsLoaded)
            {
                EditorGUILayout.LabelField("Vertices", _data.NavMesh.Vertices.Length.ToString());
                EditorGUILayout.LabelField("Triangles", _data.NavMesh.Triangles.Length.ToString());
                EditorGUILayout.LabelField("Grid",
                    $"{_data.NavMesh.GridWidth} x {_data.NavMesh.GridHeight} (Cell Size: {_data.NavMesh.GridCellSize.ToFloat():F1})");

                var bounds = _data.NavMesh.BoundsXZ;
                EditorGUILayout.LabelField("Bounds",
                    $"({bounds.min.x.ToFloat():F1}, {bounds.min.y.ToFloat():F1}) ~ " +
                    $"({bounds.max.x.ToFloat():F1}, {bounds.max.y.ToFloat():F1})");

                int blocked = 0;
                for (int i = 0; i < _data.NavMesh.Triangles.Length; i++)
                    if (_data.NavMesh.Triangles[i].isBlocked) blocked++;
                EditorGUILayout.LabelField("Blocked Triangles", blocked.ToString());

                EditorGUILayout.LabelField("Boundary Edges", _data.BoundaryEdges.Count.ToString());
                EditorGUILayout.LabelField("Internal Edges", _data.InternalEdges.Count.ToString());
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                _data.EnableLogs = EditorGUILayout.ToggleLeft("enable Logs", _data.EnableLogs, GUILayout.Width(160));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region Visualization layers

        private void DrawLayerToggles()
        {
            _foldoutLayers = EditorGUILayout.Foldout(_foldoutLayers, "Visualization Layers", true);
            if (!_foldoutLayers) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            _overlay.ShowTriangles = EditorGUILayout.ToggleLeft("Triangles", _overlay.ShowTriangles, GUILayout.Width(100));
            _overlay.ShowEdges = EditorGUILayout.ToggleLeft("Edges", _overlay.ShowEdges, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _overlay.ShowBoundaryEdges = EditorGUILayout.ToggleLeft("Boundary", _overlay.ShowBoundaryEdges, GUILayout.Width(100));
            _overlay.ShowVertices = EditorGUILayout.ToggleLeft("Vertices", _overlay.ShowVertices, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _overlay.ShowTriangleIndices = EditorGUILayout.ToggleLeft("Tri Indices", _overlay.ShowTriangleIndices, GUILayout.Width(100));
            _overlay.ShowTriangleCenters = EditorGUILayout.ToggleLeft("Centers", _overlay.ShowTriangleCenters, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _overlay.ShowBlockedTriangles = EditorGUILayout.ToggleLeft("Blocked", _overlay.ShowBlockedTriangles, GUILayout.Width(100));
            _overlay.ShowCostHeatmap = EditorGUILayout.ToggleLeft("Cost Heatmap", _overlay.ShowCostHeatmap, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            if (GUI.changed)
                SceneView.RepaintAll();

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region Pathfinding

        private void DrawPathSection()
        {
            _foldoutPath = EditorGUILayout.Foldout(_foldoutPath, "Pathfinding", true);
            if (!_foldoutPath || !_data.IsLoaded) return;

            EditorGUI.indentLevel++;

            // Interaction mode
            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Set Start", InteractionMode.SetStart);
            DrawModeButton("Set End", InteractionMode.SetEnd);
            DrawModeButton("Inspect Tri", InteractionMode.InspectTriangle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Mode", GetModeLabel(_interaction.Mode));
            EditorGUILayout.HelpBox("Shift + Click to set position.", MessageType.Info);

            // Start/end point display
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Start",
                _data.HasStart ? FormatVector3(_data.StartPoint) : "(not set)", GUILayout.Width(250));
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _data.HasStart = false;
                _data.ClearPath();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("End",
                _data.HasEnd ? FormatVector3(_data.EndPoint) : "(not set)", GUILayout.Width(250));
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _data.HasEnd = false;
                _data.ClearPath();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            // Find path button
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _data.HasStart && _data.HasEnd;
            if (GUILayout.Button("Find Path"))
            {
                bool found = _data.FindPath(_data.StartPoint, _data.EndPoint);
                if (!found)
                    Debug.LogWarning("[FPNavMeshVisualizer] Path not found.");
                SceneView.RepaintAll();
            }
            GUI.enabled = true;

            if (GUILayout.Button("Clear Path", GUILayout.Width(80)))
            {
                _data.ClearPath();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            // Path results
            if (_data.HasPath)
            {
                EditorGUILayout.LabelField("Status", "Success");
                EditorGUILayout.LabelField("Corridor", $"{_data.CorridorLength} triangles");
                EditorGUILayout.LabelField("Waypoints", $"{_data.WaypointCount} points");

                EditorGUILayout.BeginHorizontal();
                _overlay.ShowCorridor = EditorGUILayout.ToggleLeft("Corridor", _overlay.ShowCorridor, GUILayout.Width(100));
                _overlay.ShowWaypoints = EditorGUILayout.ToggleLeft("Waypoints", _overlay.ShowWaypoints, GUILayout.Width(100));
                _overlay.ShowPortals = EditorGUILayout.ToggleLeft("Portals", _overlay.ShowPortals, GUILayout.Width(100));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region Agent simulation

        private void DrawAgentSection()
        {
            _foldoutAgents = EditorGUILayout.Foldout(_foldoutAgents, "Agent Simulation", true);
            if (!_foldoutAgents || !_data.IsLoaded) return;

            EditorGUI.indentLevel++;

            // Simulation controls
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(_agentSim.IsRunning ? "\u25A0" : "\u25B6", GUILayout.Width(26)))
            {
                if (_agentSim.IsRunning) _agentSim.Pause();
                else _agentSim.Start();
            }
            if (GUILayout.Button("\u25B6|", GUILayout.Width(30)))
            {
                _agentSim.Step();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Reset", GUILayout.Width(45)))
            {
                _agentSim.Reset();
                SceneView.RepaintAll();
            }
            GUILayout.Label($"Tick: {_agentSim.CurrentTick}", GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Sim Speed", GUILayout.Width(70));
            _agentSim.SimulationSpeed = EditorGUILayout.Slider(
                _agentSim.SimulationSpeed, 0.25f, 4f);
            EditorGUILayout.EndHorizontal();

            // Agent settings
            var prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            DrawLabeledFloat("Agent Speed", ref _agentSim.DefaultSpeed);
            DrawLabeledFloat("Agent Radius", ref _agentSim.DefaultRadius);
            DrawLabeledFloat("Accel", ref _agentSim.DefaultAcceleration);
            DrawLabeledToggle("Avoidance", ref _agentSim.EnableAvoidance);
            EditorGUI.indentLevel = prevIndent;

            // Interaction mode
            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Place Agent", InteractionMode.PlaceAgent);
            DrawModeButton("Set Dest", InteractionMode.SetAgentDest);
            EditorGUILayout.EndHorizontal();

            // Agent list
            if (_agentSim.AgentCount > 0)
            {
                EditorGUILayout.Space(2);
                for (int i = 0; i < _agentSim.AgentCount; i++)
                {
                    var rd = _agentSim.GetAgentRenderData(i);
                    EditorGUILayout.BeginHorizontal();

                    string status = rd.status.ToString();
                    string pos = FormatVector3(rd.position);
                    string extra = "";
                    if (!rd.hasDestination) extra = " [No Dest]";
                    else if (!rd.hasPath) extra = " [No Path]";
                    else if (rd.currentTriangleIndex < 0) extra = " [Off Mesh]";
                    EditorGUILayout.LabelField($"#{i}: {status}{extra} {pos}");

                    if (GUILayout.Button("Select", GUILayout.Width(40)))
                    {
                        _interaction.SelectedAgentIndex = i;
                        _interaction.Mode = InteractionMode.SetAgentDest;
                    }
                    if (GUILayout.Button("X", GUILayout.Width(25)))
                    {
                        _agentSim.RemoveAgent(i);
                        SceneView.RepaintAll();
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Remove All"))
            {
                _agentSim.ClearAllAgents();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            // Spawn agent by entering coordinates directly
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Spawn Agent by Position", EditorStyles.boldLabel);
            _spawnStartText = EditorGUILayout.TextField("Start (x, y, z)", _spawnStartText);
            _spawnDestText = EditorGUILayout.TextField("Dest  (x, y, z)", _spawnDestText);
            if (GUILayout.Button("Spawn Agent"))
            {
                if (TryParseVector3(_spawnStartText, out Vector3 startPos) &&
                    TryParseVector3(_spawnDestText, out Vector3 destPos))
                {
                    int startTri = _data.FindTriangleAtPosition(startPos);
                    int destTri = _data.FindTriangleAtPosition(destPos);
                    if (startTri < 0)
                    {
                        Debug.LogWarning("[FPNavMeshVisualizer] Start position is not on the NavMesh. Pathfinding will fail.");
                    }
                    else if (destTri < 0)
                    {
                        Debug.LogWarning("[FPNavMeshVisualizer] Dest position is not on the NavMesh. Pathfinding will fail.");
                    }
                    else
                    {
                        _agentSim.ClearAllAgents();
                        int idx = _agentSim.AddAgent(startPos);
                        if (idx >= 0)
                        {
                            _agentSim.SetAgentDestination(idx, destPos);
                            _interaction.SelectedAgentIndex = idx;
                        }
                        SceneView.RepaintAll();
                    }
                }
                else
                {
                    Debug.LogWarning("[FPNavMeshVisualizer] Failed to parse coordinates. Format: x, y, z");
                }
            }

            // Agent visualization toggles
            EditorGUILayout.BeginHorizontal();
            _overlay.ShowAgents = EditorGUILayout.ToggleLeft("Agents", _overlay.ShowAgents, GUILayout.Width(80));
            _overlay.ShowAgentPaths = EditorGUILayout.ToggleLeft("Paths", _overlay.ShowAgentPaths, GUILayout.Width(80));
            _overlay.ShowAgentVelocities = EditorGUILayout.ToggleLeft("Velocity", _overlay.ShowAgentVelocities, GUILayout.Width(90));
            _overlay.ShowOrcaLines = EditorGUILayout.ToggleLeft("ORCA", _overlay.ShowOrcaLines, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region Grid

        private void DrawGridSection()
        {
            _foldoutGrid = EditorGUILayout.Foldout(_foldoutGrid, "Spatial Grid", true);
            if (!_foldoutGrid || !_data.IsLoaded) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            _overlay.ShowGrid = EditorGUILayout.ToggleLeft("Grid Lines", _overlay.ShowGrid, GUILayout.Width(100));
            _overlay.ShowGridLabels = EditorGUILayout.ToggleLeft("Cell Labels", _overlay.ShowGridLabels, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            if (_interaction.HoveredCell.col >= 0)
            {
                int col = _interaction.HoveredCell.col;
                int row = _interaction.HoveredCell.row;
                if (_data.NavMesh.IsCellValid(col, row))
                {
                    _data.NavMesh.GetCellTriangles(col, row, out _, out int count);
                    EditorGUILayout.LabelField("Hovered Cell", $"({col}, {row}) - {count} triangles");
                }
            }

            if (GUI.changed)
                SceneView.RepaintAll();

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        #endregion

        #region Info

        private void DrawInfoSection()
        {
            _foldoutInfo = EditorGUILayout.Foldout(_foldoutInfo, "Info", true);
            if (!_foldoutInfo || !_data.IsLoaded) return;

            EditorGUI.indentLevel++;

            int idx = _interaction.SelectedTriangleIndex >= 0
                ? _interaction.SelectedTriangleIndex
                : _interaction.HoveredTriangleIndex;

            if (idx >= 0 && idx < _data.NavMesh.Triangles.Length)
            {
                EditorGUILayout.LabelField("Triangle", $"Index {idx}",
                    EditorStyles.boldLabel);

                ref FPNavMeshTriangle tri = ref _data.NavMesh.Triangles[idx];
                EditorGUILayout.LabelField("Vertex Indices", $"v0={tri.v0}, v1={tri.v1}, v2={tri.v2}");

                var rd = _data.CachedTriangles[idx];
                EditorGUILayout.LabelField("v0", FormatVector3(rd.v0));
                EditorGUILayout.LabelField("v1", FormatVector3(rd.v1));
                EditorGUILayout.LabelField("v2", FormatVector3(rd.v2));

                string n0 = tri.neighbor0 >= 0 ? tri.neighbor0.ToString() : "boundary";
                string n1 = tri.neighbor1 >= 0 ? tri.neighbor1.ToString() : "boundary";
                string n2 = tri.neighbor2 >= 0 ? tri.neighbor2.ToString() : "boundary";
                EditorGUILayout.LabelField("Neighbors", $"n0={n0}, n1={n1}, n2={n2}");
                EditorGUILayout.LabelField("Area Mask", tri.areaMask.ToString());
                EditorGUILayout.LabelField("Cost Multiplier", tri.costMultiplier.ToFloat().ToString("F2"));
                EditorGUILayout.LabelField("Blocked", tri.isBlocked ? "Yes" : "No");
                EditorGUILayout.LabelField("Area", tri.area.ToFloat().ToString("F4"));
            }
            else
            {
                EditorGUILayout.LabelField("Hover or click a triangle to inspect.");
            }

            EditorGUI.indentLevel--;
        }

        #endregion

        #region Event handlers

        private void OnStartPointSet(Vector3 point)
        {
            _data.StartPoint = point;
            _data.HasStart = true;
            _interaction.Mode = InteractionMode.SetEnd;

            // Auto-find path when both start and end are set
            if (_data.HasStart && _data.HasEnd)
                _data.FindPath(_data.StartPoint, _data.EndPoint);

            SceneView.RepaintAll();
            Repaint();
        }

        private void OnEndPointSet(Vector3 point)
        {
            _data.EndPoint = point;
            _data.HasEnd = true;

            if (_data.HasStart && _data.HasEnd)
                _data.FindPath(_data.StartPoint, _data.EndPoint);

            SceneView.RepaintAll();
            Repaint();
        }

        private void OnTriangleSelected(int triIdx)
        {
            SceneView.RepaintAll();
            Repaint();
        }

        private void OnAgentPlaced(Vector3 point)
        {
            int idx = _agentSim.AddAgent(point);
            if (idx >= 0)
            {
                _interaction.SelectedAgentIndex = idx;
            }
            SceneView.RepaintAll();
            Repaint();
        }

        private void OnAgentDestinationSet(int agentIdx, Vector3 dest)
        {
            _agentSim.SetAgentDestination(agentIdx, dest);
            SceneView.RepaintAll();
            Repaint();
        }

        #endregion

        #region Utilities

        private static void DrawLabeledFloat(string label, ref float value)
        {
            Rect rect = EditorGUILayout.GetControlRect(false);
            Rect labelRect = new Rect(rect.x, rect.y, rect.width - 65, rect.height);
            Rect fieldRect = new Rect(rect.xMax - 60, rect.y, 60, rect.height);
            GUI.Label(labelRect, label);
            value = EditorGUI.FloatField(fieldRect, value);
        }

        private static void DrawLabeledToggle(string label, ref bool value)
        {
            Rect rect = EditorGUILayout.GetControlRect(false);
            Rect labelRect = new Rect(rect.x, rect.y, rect.width - 20, rect.height);
            Rect fieldRect = new Rect(rect.xMax - 16, rect.y, 16, rect.height);
            GUI.Label(labelRect, label);
            value = EditorGUI.Toggle(fieldRect, value);
        }

        private void DrawModeButton(string label, InteractionMode mode)
        {
            bool isActive = _interaction.Mode == mode;
            GUI.backgroundColor = isActive ? Color.cyan : Color.white;
            if (GUILayout.Button(label))
            {
                _interaction.Mode = isActive ? InteractionMode.None : mode;
            }
            GUI.backgroundColor = Color.white;
        }

        private static string GetModeLabel(InteractionMode mode)
        {
            switch (mode)
            {
                case InteractionMode.SetStart: return "Set Start (Shift+Click)";
                case InteractionMode.SetEnd: return "Set End (Shift+Click)";
                case InteractionMode.InspectTriangle: return "Inspect Triangle (Shift+Click)";
                case InteractionMode.PlaceAgent: return "Place Agent (Shift+Click)";
                case InteractionMode.SetAgentDest: return "Set Destination (Shift+Click)";
                default: return "None";
            }
        }

        private static bool TryParseVector3(string text, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            if (parts.Length != 3) return false;
            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            return false;
        }

        private static string FormatVector3(Vector3 v)
        {
            return $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
        }

        private void FocusSceneViewOnNavMesh()
        {
            if (!_data.IsLoaded) return;

            var bounds = _data.NavMesh.BoundsXZ;
            Vector3 center = new Vector3(
                bounds.center.x.ToFloat(), 0,
                bounds.center.y.ToFloat());

            float size = Mathf.Max(
                bounds.extents.x.ToFloat(),
                bounds.extents.y.ToFloat()) * 2f;

            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                sv.LookAt(center, Quaternion.Euler(60, 0, 0), size);
            }
        }

        #endregion
    }
}
