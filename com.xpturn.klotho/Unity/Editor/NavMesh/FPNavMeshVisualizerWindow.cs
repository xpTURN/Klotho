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

        // Last navmesh the runtime bridge handed us. Deliberately not _data.NavMesh: a file
        // loaded mid-play must not be overwritten by the next rebake swap.
        private FPNavMesh _lastBridgeNavMesh;

        // UI state
        private TextAsset _navMeshAsset;
        private Vector2 _scrollPosition;
        private bool _foldoutNavMesh = true;
        private bool _foldoutLayers = true;
        private bool _foldoutPath = true;
        private bool _foldoutAgents = true;
        private bool _foldoutGrid;
        private bool _foldoutInfo;
        private bool _foldoutBuildings = true;
        private string _spawnStartText = "0.0, 0.0, 0.0";
        private string _spawnDestText = "1.0, 0.0, 1.0";

        // Placement tool state. All of it is INPUT: the geometry lives in the data layer's probe.
        private bool _placeRetain = true;
        private int _placeShapeIndex;                  // 0 = box, 1 = hexagon
        private int _placeOrientation;
        private FPBoundaryPlacementPolicy _placePolicy = FPBoundaryPlacementPolicy.Touch;
        private bool _placeAllowTouch = true;
        private string _placeStatus;

        // Which mask the Find Path button uses. It used to be hardcoded ~0, which was the same
        // thing as the agents' mask until a retained footprint could exist; now the two disagree,
        // and being able to flip between them on one mesh is the point of the tool.
        private enum PathMask { AgentDefault, AllAreas }
        private PathMask _pathMask = PathMask.AgentDefault;

        private int ResolvePathMask() =>
            _pathMask == PathMask.AllAreas
                ? FPNavMeshAreas.ALL_AREAS
                : FPNavAgentSystem.DEFAULT_AREA_MASK;

        // The masks handed to the SELECTED agent, and the same two-value vocabulary as above. An
        // agent carries two: the plan mask decides what it may route through, the walk mask what it
        // may enter. The combination worth reaching for is Plan = AllAreas with Walk = AgentDefault
        // — the path is drawn straight through a retained building and the agent walks into it and
        // stops, reporting Blocked in the list below. Applied per agent rather than globally so two
        // agents can differ on one mesh and split on the same tick.
        private PathMask _agentPlanMask = PathMask.AgentDefault;
        private PathMask _agentWalkMask = PathMask.AgentDefault;

        private static int ResolveAgentMask(PathMask m) =>
            m == PathMask.AllAreas ? FPNavMeshAreas.ALL_AREAS : FPNavAgentSystem.DEFAULT_AREA_MASK;

        /// <summary>Short label for an agent's stored override — 0 is "no override".</summary>
        private static string MaskLabel(int mask) =>
            mask == 0 ? "dflt"
            : mask == FPNavMeshAreas.ALL_AREAS ? "all"
            : mask == FPNavAgentSystem.DEFAULT_AREA_MASK ? "dflt"
            : $"0x{mask:X}";

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
            _interaction.OnBuildingPlaced += OnBuildingPlaced;

            // The simulation half of adopting a rebake. Set here so the data layer's
            // InstallRebakedMesh is the only way to install one and cannot be done halfway.
            _data.AgentSwap = mesh => _agentSim.SwapNavMesh(mesh);

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
            _interaction.OnBuildingPlaced -= OnBuildingPlaced;
            _data.AgentSwap = null;

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
            if (!ReloadFromRuntimeBridge()) return;
            _agentSim.Initialize(_data);
        }

        /// <summary>
        /// Rebinds the visualizer to the bridge's current navmesh. Split out of
        /// <see cref="ConnectRuntimeBridge"/> so a mid-play rebake can refresh the view WITHOUT
        /// re-running <see cref="FPNavMeshAgentSimulator.Initialize"/>, which recreates the sim
        /// frame and resets the tick — that would wipe an in-progress editor experiment every
        /// time a building is placed.
        /// Reloading is the whole of the refresh, not just the render cache: LoadFromNavMesh
        /// also rebinds Query and clears the path results, which live in the OLD mesh's
        /// triangle index space.
        /// </summary>
        private bool ReloadFromRuntimeBridge()
        {
            var bridge = EcsDebugBridge.Instance;
            if (bridge == null) return false;

            // Read once — the bridge reads through to the live provider (see EcsDebugBridge).
            FPNavMesh mesh = bridge.NavMesh;
            if (mesh == null) return false;

            _lastBridgeNavMesh = mesh;
            _data.LoadFromNavMesh(mesh, bridge.NavQuery);
            _overlay.SetRuntimeSnapshotBuffer(bridge.AgentSnapshots);
            Repaint();
            return true;
        }

        private void DisconnectRuntimeBridge()
        {
            _lastBridgeNavMesh = null;
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
                if (bridge == null) return;

                // A runtime rebake (building placement) swaps the simulation's navmesh.
                // Track what the BRIDGE last handed us, not _data.NavMesh — the user may have
                // loaded a file mid-play, and comparing against _data would overwrite it on
                // every swap.
                if (!ReferenceEquals(bridge.NavMesh, _lastBridgeNavMesh))
                    ReloadFromRuntimeBridge();

                if (bridge.AgentSnapshotCount > 0)
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
                DrawBuildingSection();
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
            _overlay.ShowObstacleRings = EditorGUILayout.ToggleLeft("Obstacle Rings", _overlay.ShowObstacleRings, GUILayout.Width(120));
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

        #region Buildings (placement probe)

        /// <summary>
        /// Area masks for the selected agent. Two dropdowns rather than one, because an agent
        /// carries two and the interesting setting is the pair where they disagree.
        ///
        /// <para>Applying goes through <c>SetAgentAreaMask</c> → <c>NavAgentComponent.SetAreaMask</c>,
        /// which drops the corridor the old masks planned — so applying to a walking agent makes it
        /// replan on the next tick rather than keep following a route its new mask refuses.</para>
        /// </summary>
        private void DrawAgentAreaMaskControls()
        {
            int sel = _interaction.SelectedAgentIndex;
            bool has = sel >= 0 && sel < _agentSim.AgentCount;

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                has ? $"Area masks for #{sel}" : "Area masks (select an agent)");

            // GUILayout.Label, not EditorGUILayout.LabelField: this section runs one indent level
            // in, and LabelField takes that indent out of the width it is given — a 4-letter label
            // in 28px keeps 13px and clips. GUILayout.Label ignores indentLevel, which is why the
            // "Sim Speed" row above already uses it.
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!has))
            {
                GUILayout.Label("plan", GUILayout.Width(34));
                _agentPlanMask = (PathMask)EditorGUILayout.EnumPopup(
                    _agentPlanMask, GUILayout.Width(115));
                GUILayout.Label("walk", GUILayout.Width(36));
                _agentWalkMask = (PathMask)EditorGUILayout.EnumPopup(
                    _agentWalkMask, GUILayout.Width(115));

                if (GUILayout.Button("Apply", GUILayout.Width(50)))
                {
                    _agentSim.SetAgentAreaMask(sel,
                        ResolveAgentMask(_agentPlanMask), ResolveAgentMask(_agentWalkMask));
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_agentPlanMask == PathMask.AllAreas && _agentWalkMask == PathMask.AgentDefault)
                EditorGUILayout.LabelField(
                    "   plans through buildings, cannot enter them → walks in and reports Blocked",
                    EditorStyles.miniLabel);
        }

        /// <summary>
        /// Place Carve/Retain buildings on the loaded mesh and rebake. The geometry work lives in
        /// the data layer's probe; everything here is input.
        ///
        /// <para>Edit mode only. In play mode the game owns the placements and the bridge follows
        /// its mesh — a list here would be a second, competing account of what is built.</para>
        /// </summary>
        private void DrawBuildingSection()
        {
            _foldoutBuildings = EditorGUILayout.Foldout(_foldoutBuildings, "Buildings", true);
            if (!_foldoutBuildings || !_data.IsLoaded) return;

            EditorGUI.indentLevel++;

            // A stacked base cannot be rebaked at all, and that is known at load. Say so and stop —
            // offering the controls would only lead to a refusal on every click.
            if (!_data.PlacementSupported)
            {
                EditorGUILayout.HelpBox(
                    "Placement is unavailable for this mesh:\n" + _data.PlacementUnsupportedReason,
                    MessageType.Warning);
                EditorGUI.indentLevel--;
                return;
            }

            EditorGUILayout.HelpBox(
                "These shapes belong to the TOOL, not to any game: a 2x1 box and a hexagon. "
                + "Acceptance here says nothing about a game whose shape catalog differs.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Place Building", InteractionMode.PlaceBuilding);
            _placeRetain = EditorGUILayout.ToggleLeft(
                "Retain (keep ground)", _placeRetain, GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();

            // Flush packing needs the centre on the shape's tiling lattice, and those spacings are
            // not round numbers — a 2x1 box at radius 0.5 meets its neighbour at 2.003906, so a
            // free-hand click leaves about four millimetres of walkable ground between footprints
            // and the engine cannot tell that from a door. Off is for looking at a near-miss.
            _data.SnapPlacementToLattice = EditorGUILayout.ToggleLeft(
                "Snap to tiling lattice (flush)", _data.SnapPlacementToLattice);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Shape", GUILayout.Width(70));
            _placeShapeIndex = EditorGUILayout.Popup(
                _placeShapeIndex, new[] { "Box", "Hexagon" }, GUILayout.Width(120));
            if (_placeShapeIndex == 0)
            {
                EditorGUILayout.LabelField("Turn", GUILayout.Width(40));
                _placeOrientation = EditorGUILayout.IntSlider(
                    _placeOrientation, 0, FPNavMeshPlacementProbe.ToolBoxDirections - 1);
            }
            else
            {
                // A hexagon has no orientation in the catalog: no integer hexagon is symmetric
                // under 60 degrees, so the turns a slider would offer do not exist.
                EditorGUILayout.LabelField("(no turns)");
            }
            EditorGUILayout.EndHorizontal();

            // Placement rules. Touch is the default rather than a game's ClipOverlap because under
            // ClipOverlap a RETAINED footprint that crosses the walkable boundary is refused (a
            // carve would be clipped instead) — worth seeing on purpose, not by default.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Boundary", GUILayout.Width(70));
            var policy = (FPBoundaryPlacementPolicy)EditorGUILayout.EnumPopup(
                _placePolicy, GUILayout.Width(120));
            bool allowTouch = EditorGUILayout.ToggleLeft(
                "Allow contact", _placeAllowTouch, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();
            if (policy != _placePolicy || allowTouch != _placeAllowTouch)
            {
                _placePolicy = policy;
                _placeAllowTouch = allowTouch;
                _data.PlacementRules = new FPBuildingPlacementRules(_placeAllowTouch, _placePolicy);
                if (_data.PlacementCount > 0
                    && !_data.TryRebakeBuildings(out FPBuildingRejectionInfo ruleRejection))
                {
                    _placeStatus = $"Rules refused the current set: {ruleRejection.Reason}";
                }
                SceneView.RepaintAll();
            }

            // The list, newest last, with the mode spelled out — retain leaves no hole, so the
            // text is the only place the two are unambiguous without reading the overlay colour.
            EditorGUILayout.LabelField("Placed", $"{_data.PlacementCount}");
            int removeAt = -1;
            for (int i = 0; i < _data.PlacementCount; i++)
            {
                FPBuildingPlacement p = _data.PlacementAt(i);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"#{i}  {(p.Retain ? "retain" : "carve")}  "
                    + $"({p.CentreX.ToFloat():F2}, {p.CentreZ.ToFloat():F2})",
                    GUILayout.Width(230));
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    removeAt = i;
                EditorGUILayout.EndHorizontal();
            }
            if (removeAt >= 0)
            {
                _placeStatus = _data.TryRemoveBuilding(removeAt, out var removeRejection)
                    ? null
                    : $"Remove refused: {removeRejection.Reason} — the building is still placed";
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("Revert to loaded mesh", GUILayout.Width(180)))
            {
                _data.RevertBuildings();
                _placeStatus = null;
                SceneView.RepaintAll();
            }

            if (!string.IsNullOrEmpty(_placeStatus))
                EditorGUILayout.HelpBox(_placeStatus, MessageType.Warning);

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// Shift+Click in Place Building mode. A refusal is a VALUE shown as text — pointing
        /// somewhere you cannot build is normal use, not an error.
        /// </summary>
        private void OnBuildingPlaced(Vector3 point)
        {
            // Pausing first: the simulator steps on a timer, and the engine's contract is that a
            // swap happens at a tick boundary. A swap landing between two steps of the same tick
            // is undefined rather than merely untidy.
            bool wasRunning = _agentSim.IsRunning;
            if (wasRunning)
                _agentSim.Pause();

            int shapeId = _placeShapeIndex == 0
                ? FPNavMeshPlacementProbe.ToolBoxShape
                : FPNavMeshPlacementProbe.ToolHexShape;
            int orientation = _placeShapeIndex == 0 ? _placeOrientation : 0;

            if (_data.TryPlaceBuilding(point, shapeId, orientation, _placeRetain,
                    out FPBuildingRejectionInfo rejection))
            {
                _placeStatus = null;
                if (!_overlay.ShowTriangles)
                    _placeStatus = "Placed. Turn the Triangles layer on to see it.";
            }
            else
            {
                // A gated stage never reaches the click, but a stale mode could: name the stage
                // refusal rather than a rejection reason that was never filled in.
                _placeStatus = _data.PlacementUnsupportedReason ?? $"Refused: {rejection.Reason}";
            }

            if (wasRunning)
                _agentSim.Start();

            Repaint();
            SceneView.RepaintAll();
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

            // The area filter. Agent default excludes the building area, so a retained footprint is
            // a wall to it; All areas plans straight through one. Same mesh, opposite answers.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Area mask", GUILayout.Width(70));
            _pathMask = (PathMask)EditorGUILayout.EnumPopup(_pathMask, GUILayout.Width(120));
            EditorGUILayout.LabelField(
                _pathMask == PathMask.AllAreas ? "~0 (through buildings)" : "~BUILDING_MASK (agents)");
            EditorGUILayout.EndHorizontal();

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
                bool found = _data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask());
                if (!found)
                    Debug.LogWarning(
                        "[FPNavMeshVisualizer] Path not found"
                        + (_pathMask == PathMask.AgentDefault
                            ? " — an endpoint inside a retained building footprint is refused by the agent mask."
                            : "."));
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
            // Bake inset carried by the loaded mesh boundary (R_sim = R_bake convention → set to
            // the bake Agent Radius; 0 = uncorrected double clearance). Applied live.
            float obstInset = _agentSim.ObstacleRadiusInset;
            DrawLabeledFloat("Obst Inset", ref obstInset);
            if (!Mathf.Approximately(obstInset, _agentSim.ObstacleRadiusInset))
                _agentSim.SetObstacleRadiusInset(obstInset);
            // How far a destination click may be moved onto ground the agent's plan mask allows.
            // The default is the mesh's GridCellSize, which is what the projection ALWAYS reaches;
            // the slider goes to 3 cells because the search covers one cell ring and past its far
            // corner (~2.83 cells) there is nothing left to find.
            float snapMax = _agentSim.DestinationSnapMaxDist;
            DrawLabeledFloat("Dest Snap Max", ref snapMax);
            _agentSim.DestinationSnapMaxDist = Mathf.Clamp(snapMax, 0f, 3f * CellSizeOrOne());
            EditorGUI.indentLevel = prevIndent;

            // Why the last destination click was refused. Shown as a lasting state rather than
            // logged: a refusal is "it will not move" for as long as it stands, and the Godot
            // simulator has no status log to mirror a one-shot message into.
            if (!string.IsNullOrEmpty(_agentSim.LastDestinationRefusal))
                EditorGUILayout.HelpBox(_agentSim.LastDestinationRefusal, MessageType.Warning);

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
                    // Off-mesh FIRST. It used to come last, which made it unreachable: the
                    // reseed after a rebake clears HasPath for every agent BEFORE it looks the
                    // triangle up, so an agent the swap left off-mesh always has hasPath == false
                    // and was reported as [No Path] — the symptom, hiding the cause. That case is
                    // the one no pathfinder counter can explain either (the reseed never calls
                    // FindPath), so this row was the only place it could have been said.
                    string extra = "";
                    if (rd.currentTriangleIndex < 0) extra = " [Off Mesh]";
                    else if (!rd.hasDestination) extra = " [No Dest]";
                    else if (!rd.hasPath) extra = " [No Path]";
                    // The masks are shown because a mixed scene is otherwise unreadable — two
                    // agents at the same spot with the same status look identical, and which of
                    // them may enter the building is the whole question.
                    string masks = $" [{MaskLabel(rd.planAreaMask)}/{MaskLabel(rd.walkAreaMask)}]";
                    // WHY it failed, judged against the mesh as it is now. Separate from the
                    // destination refusal above: that one says "the click was refused", this one
                    // says "the destination it has cannot be reached".
                    string why = FPNavPathFailure.Describe(rd.failureReason);
                    string sel = _interaction.SelectedAgentIndex == i ? "▸" : " ";
                    EditorGUILayout.LabelField($"{sel}#{i}: {status}{extra}{masks}{why} {pos}");

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

                DrawAgentAreaMaskControls();
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
                    if (startTri < 0)
                    {
                        Debug.LogWarning("[FPNavMeshVisualizer] Start position is not on the NavMesh. Pathfinding will fail.");
                    }
                    else
                    {
                        // The destination is NOT pre-checked here any more. The check that used to
                        // stand at this spot asked FindTriangleAtPosition, which is unfiltered: it
                        // waved a click on a retained building through (and SetAgentDestination
                        // would then refuse it, two guards disagreeing at one site) while refusing
                        // an off-mesh click outright — so the click never reached the snap that
                        // exists to move it onto usable ground. SetAgentDestination now answers
                        // both cases, and says why when it cannot.
                        _agentSim.ClearAllAgents();
                        int idx = _agentSim.AddAgent(startPos);
                        if (idx >= 0)
                        {
                            _interaction.SelectedAgentIndex = idx;
                            if (!_agentSim.SetAgentDestination(idx, destPos.ToFPVector3()))
                                Debug.LogWarning($"[FPNavMeshVisualizer] {_agentSim.LastDestinationRefusal}");
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

                ref readonly FPNavMeshTriangle tri = ref _data.NavMesh.Triangles[idx];
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
                _data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask());

            SceneView.RepaintAll();
            Repaint();
        }

        private void OnEndPointSet(Vector3 point)
        {
            _data.EndPoint = point;
            _data.HasEnd = true;

            if (_data.HasStart && _data.HasEnd)
                _data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask());

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
            if (!_agentSim.SetAgentDestination(agentIdx, dest.ToFPVector3()))
                Debug.LogWarning($"[FPNavMeshVisualizer] {_agentSim.LastDestinationRefusal}");
            SceneView.RepaintAll();
            Repaint();
        }

        #endregion

        #region Utilities

        // The wording lives here, not in the simulator: the simulator hands over an enum so the
        // pattern tests can compare exactly and the phrasing can be tuned without breaking them.
        // The snap slider's ceiling is expressed in cells, so it needs the loaded mesh's cell
        // size; 1 keeps the clamp harmless before a mesh is loaded.
        private float CellSizeOrOne()
            => _data != null && _data.IsLoaded ? _data.NavMesh.GridCellSize.ToFloat() : 1f;

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
                case InteractionMode.PlaceBuilding: return "Place Building (Shift+Click)";
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
