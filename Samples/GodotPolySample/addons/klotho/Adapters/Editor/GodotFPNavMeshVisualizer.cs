// Controller for the Godot FPNavMesh visualizer. plugin.gd instantiates this [GlobalClass]
// and forwards the 3D editor virtuals here. The controller owns the data/overlay/interaction/sim
// subsystems, the dock Control, and the overlay MeshInstance3D lifecycle. EditorPlugin-only calls
// (AddControlToDock / RemoveControlFromDocks / UpdateOverlays) go through the injected plugin ref.
#if TOOLS
using global::Godot;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Godot
{
    [Tool]
    [GlobalClass]
    public partial class GodotFPNavMeshVisualizer : RefCounted
    {
        private const float LabelMaxDist = 40f;

        private EditorPlugin _plugin;
        private GodotFPNavMeshVisualizerData _data;
        private GodotFPNavMeshOverlay _overlay;
        private GodotFPNavMeshInteraction _interaction;
        private GodotFPNavMeshAgentSimulator _agentSim;
        private GodotFPNavMeshVisualizerDock _dock;
        private ScrollContainer _dockScroll;

        private Camera3D _camera;
        private Font _font;
        private bool _active;
        private bool _attached;

        internal GodotFPNavMeshVisualizerData Data => _data;
        internal GodotFPNavMeshOverlay Overlay => _overlay;
        internal GodotFPNavMeshAgentSimulator AgentSim => _agentSim;
        internal GodotFPNavMeshInteraction Interaction => _interaction;

        // ---- lifecycle (called by plugin.gd) ----

        public void Init(EditorPlugin plugin)
        {
            _plugin = plugin;
            _data = new GodotFPNavMeshVisualizerData();
            _overlay = new GodotFPNavMeshOverlay();
            _interaction = new GodotFPNavMeshInteraction();
            _agentSim = new GodotFPNavMeshAgentSimulator();

            _overlay.SetData(_data);
            _overlay.SetAgentSimulator(_agentSim);
            _interaction.SetData(_data);

            _interaction.OnStartPointSet += OnStartPointSet;
            _interaction.OnEndPointSet += OnEndPointSet;
            _interaction.OnTriangleSelected += OnTriangleSelected;
            _interaction.OnAgentPlaced += OnAgentPlaced;
            _interaction.OnAgentDestinationSet += OnAgentDestinationSet;
            _interaction.OnBuildingPlaced += OnBuildingPlaced;

            // The simulation half of adopting a rebake. Set here so the data layer's
            // InstallRebakedMesh is the only way to install one and cannot be done halfway.
            _data.AgentSwap = mesh => _agentSim.SwapNavMesh(mesh);

            _font = ThemeDB.FallbackFont;
            _dock = new GodotFPNavMeshVisualizerDock();
            _dock.Init(this);

            // Wrap the dock in a ScrollContainer so its (tall) content does not impose a large
            // minimum size on the dock column — that would force the editor to re-layout and can
            // collapse the bottom panel. The ScrollContainer fills the tab and scrolls vertically.
            _dockScroll = new ScrollContainer
            {
                Name = "FPNavMesh",
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            _dock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _dockScroll.AddChild(_dock);
        }

        public void Shutdown()
        {
            if (_active) Deactivate();
            _dockScroll?.QueueFree();   // frees the dock child too
            _dockScroll = null;
            _dock = null;
        }

        public bool IsActive() => _active;

        public void ToggleActive()
        {
            if (_active) Deactivate();
            else Activate();
        }

        private void Activate()
        {
#pragma warning disable CS0618 // AddControlToDock is the supported path across 4.x; AddDock(EditorDock) is newer.
            _plugin.AddControlToDock(EditorPlugin.DockSlot.RightUl, _dockScroll);
#pragma warning restore CS0618
            _active = true;
            TryAttachOverlay();
            _overlay.RebuildStatic();
            _overlay.RebuildDynamic();
            _dock.Refresh();
            _plugin.UpdateOverlays();
        }

        private void Deactivate()
        {
            _overlay.Detach();
            _attached = false;
#pragma warning disable CS0618
            _plugin.RemoveControlFromDocks(_dockScroll);
#pragma warning restore CS0618
            _active = false;
            _plugin.UpdateOverlays();
        }

        private void TryAttachOverlay()
        {
            if (_attached) return;
            Node root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                GD.PushWarning("[GodotFPNavMeshVisualizer] No edited scene — overlay geometry will not show until a 3D scene is open.");
                return;
            }
            _overlay.Attach(root);
            _attached = true;
        }

        // ---- 3D editor virtuals (forwarded from plugin.gd) ----

        public int HandleInput(Camera3D camera, InputEvent ev)
        {
            _camera = camera;
            if (_data == null || !_data.IsLoaded) return (int)EditorPlugin.AfterGuiInput.Pass;

            var prevCell = _interaction.HoveredCell;
            int prevTri = _interaction.HoveredTriangleIndex;

            bool consumed = _interaction.ProcessInput(camera, ev);

            _overlay.HoveredTriangleIndex = _interaction.HoveredTriangleIndex;
            _overlay.HoveredCell = _interaction.HoveredCell;

            if (_interaction.HoveredCell != prevCell)
            {
                _overlay.RebuildDynamic();
                _plugin.UpdateOverlays();
                _dock.Refresh();
            }
            else if (_interaction.HoveredTriangleIndex != prevTri)
            {
                _plugin.UpdateOverlays();
                _dock.Refresh();
            }

            return consumed ? (int)EditorPlugin.AfterGuiInput.Stop : (int)EditorPlugin.AfterGuiInput.Pass;
        }

        public void DrawLabels(Control overlay)
        {
            if (_camera == null || _data == null || !_data.IsLoaded) return;
            float maxDistSqr = LabelMaxDist * LabelMaxDist;
            Vector3 camPos = _camera.GlobalPosition;

            foreach (var (pos, text) in _overlay.Labels)
            {
                if (_camera.IsPositionBehind(pos)) continue;
                if (camPos.DistanceSquaredTo(pos) > maxDistSqr) continue;
                Vector2 screen = _camera.UnprojectPosition(pos);
                overlay.DrawString(_font, screen, text, HorizontalAlignment.Center, -1f, 10);
            }
        }

        public void OnProcess(double delta)
        {
            if (!_active || _agentSim == null) return;
            if (_agentSim.OnEditorUpdate(delta))
            {
                _overlay.RebuildDynamic();
                _plugin.UpdateOverlays();
                _dock.Refresh();
            }
        }

        // ---- operations (called by the dock) ----

        internal void Load(string resPath)
        {
            if (string.IsNullOrEmpty(resPath)) { GD.PushWarning("[GodotFPNavMeshVisualizer] Empty path."); return; }
            if (!FileAccess.FileExists(resPath)) { GD.PushError($"[GodotFPNavMeshVisualizer] File not found: {resPath}"); return; }

            byte[] bytes = FileAccess.GetFileAsBytes(resPath);
            if (bytes == null || bytes.Length == 0) { GD.PushError($"[GodotFPNavMeshVisualizer] Empty file: {resPath}"); return; }

            _agentSim.ClearAllAgents();
            if (_data.LoadFromBytes(bytes))
            {
                _agentSim.Initialize(_data);
                TryAttachOverlay();
                _overlay.RebuildStatic();
                _overlay.RebuildDynamic();
                _plugin.UpdateOverlays();
            }
            _dock.Refresh();
        }

        internal void Unload()
        {
            _data.Unload();
            _agentSim.ClearAllAgents();
            _overlay.RebuildStatic();
            _overlay.RebuildDynamic();
            _plugin.UpdateOverlays();
            _dock.Refresh();
        }

        internal void RequestStaticRedraw()
        {
            _overlay.RebuildStatic();
            _plugin.UpdateOverlays();
        }

        internal void RequestDynamicRedraw()
        {
            _overlay.RebuildDynamic();
            _plugin.UpdateOverlays();
        }

        // ---- placement tool state (input only; the geometry lives in the data layer) ----

        internal bool PlaceRetain = true;
        internal int PlaceShapeIndex;                  // 0 = box, 1 = hexagon
        internal int PlaceOrientation;
        internal FPBoundaryPlacementPolicy PlacePolicy = FPBoundaryPlacementPolicy.Touch;
        internal bool PlaceAllowTouch = true;
        internal string PlaceStatus;

        /// <summary>
        /// Which mask Find Path uses. It was hardcoded ~0, which was the same value the agents pass
        /// until a retained footprint could exist; now the two disagree, and flipping between them
        /// on one mesh is the point of the tool.
        /// </summary>
        internal bool PathMaskAllAreas;

        internal int ResolvePathMask() =>
            PathMaskAllAreas ? FPNavMeshAreas.ALL_AREAS : FPNavAgentSystem.DEFAULT_AREA_MASK;

        // The masks handed to the SELECTED agent. An agent carries two: the plan mask decides what
        // it may route through, the walk mask what it may enter. The combination worth reaching for
        // is plan = all areas with walk = default — the path is drawn straight through a retained
        // building and the agent walks into it and stops, reporting Blocked in the agent list.
        // Per agent rather than global so two agents can differ on one mesh and split on the same
        // tick, which is the only direct evidence that the mask belongs to the agent.
        internal bool AgentPlanMaskAllAreas;
        internal bool AgentWalkMaskAllAreas;

        private static int ResolveAgentMask(bool allAreas) =>
            allAreas ? FPNavMeshAreas.ALL_AREAS : FPNavAgentSystem.DEFAULT_AREA_MASK;

        /// <summary>Short label for an agent's stored override — 0 is "no override".</summary>
        internal static string MaskLabel(int mask) =>
            mask == 0 ? "dflt"
            : mask == FPNavMeshAreas.ALL_AREAS ? "all"
            : mask == FPNavAgentSystem.DEFAULT_AREA_MASK ? "dflt"
            : $"0x{mask:X}";

        /// <summary>
        /// Hands the selected agent the two masks above. Goes through
        /// <c>NavAgentComponent.SetAreaMask</c>, which drops the corridor the old masks planned, so
        /// applying to a walking agent makes it replan rather than keep following a refused route.
        /// </summary>
        internal void ApplyAgentAreaMask()
        {
            int sel = _interaction?.SelectedAgentIndex ?? -1;
            if (_agentSim == null || sel < 0 || sel >= _agentSim.AgentCount) return;

            _agentSim.SetAgentAreaMask(sel,
                ResolveAgentMask(AgentPlanMaskAllAreas), ResolveAgentMask(AgentWalkMaskAllAreas));
            RequestDynamicRedraw();
        }

        internal void ApplyPlacementRules()
        {
            _data.PlacementRules = new FPBuildingPlacementRules(PlaceAllowTouch, PlacePolicy);
            if (_data.PlacementCount > 0
                && !_data.TryRebakeBuildings(out FPBuildingRejectionInfo rejection))
            {
                PlaceStatus = $"Rules refused the current set: {rejection.Reason}";
            }
            RequestStaticRedraw();
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        internal void RemoveBuilding(int index)
        {
            PlaceStatus = _data.TryRemoveBuilding(index, out var removeRejection)
                ? null
                : $"Remove refused: {removeRejection.Reason} — the building is still placed";
            RequestStaticRedraw();
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        internal void RevertBuildings()
        {
            _data.RevertBuildings();
            PlaceStatus = null;
            RequestStaticRedraw();
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        /// <summary>
        /// Shift+Click in Place Building mode. A refusal is a VALUE shown as text — pointing
        /// somewhere you cannot build is normal use, not an error.
        /// </summary>
        private void OnBuildingPlaced(Vector3 point)
        {
            // Pause first: the simulator steps on a timer and the engine's contract is that a swap
            // happens at a tick boundary. A swap landing between two steps is undefined.
            bool wasRunning = _agentSim.IsRunning;
            if (wasRunning)
                _agentSim.Pause();

            int shapeId = PlaceShapeIndex == 0
                ? FPNavMeshPlacementProbe.ToolBoxShape
                : FPNavMeshPlacementProbe.ToolHexShape;
            int orientation = PlaceShapeIndex == 0 ? PlaceOrientation : 0;

            PlaceStatus = _data.TryPlaceBuilding(point, shapeId, orientation, PlaceRetain,
                    out FPBuildingRejectionInfo rejection)
                ? null
                // A gated stage never reaches the click, but a stale mode could: name the stage
                // refusal rather than a rejection reason that was never filled in.
                : _data.PlacementUnsupportedReason ?? $"Refused: {rejection.Reason}";

            if (wasRunning)
                _agentSim.Start();

            RequestStaticRedraw();
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        internal void FindPath()
        {
            if (_data.HasStart && _data.HasEnd)
            {
                if (!_data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask()))
                {
                    GD.PushWarning(
                        "[GodotFPNavMeshVisualizer] Path not found"
                        + (PathMaskAllAreas
                            ? "."
                            : " — an endpoint inside a retained building footprint is refused by the agent mask."));
                }
            }
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        internal void ClearPath()
        {
            _data.ClearPath();
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        internal void SpawnAgentByPosition(Vector3 start, Vector3 dest)
        {
            int startTri = _data.FindTriangleAtPosition(start);
            if (startTri < 0) { GD.PushWarning("[GodotFPNavMeshVisualizer] Start is off the NavMesh."); return; }

            // The destination is NOT pre-checked here any more. The check that used to stand at
            // this spot asked FindTriangleAtPosition, which is unfiltered: it waved a click on a
            // retained building through (and SetAgentDestination would then refuse it, two guards
            // disagreeing at one site) while refusing an off-mesh click outright — so the click
            // never reached the snap that exists to move it onto usable ground. SetAgentDestination
            // now answers both cases, and says why when it cannot.
            _agentSim.ClearAllAgents();
            int idx = _agentSim.AddAgent(start);
            if (idx >= 0)
            {
                _interaction.SelectedAgentIndex = idx;
                if (!_agentSim.SetAgentDestination(idx, dest.ToFPVector3()))
                    GD.PushWarning($"[GodotFPNavMeshVisualizer] {_agentSim.LastDestinationRefusal}");
            }
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        // ---- interaction event handlers ----

        private void OnStartPointSet(Vector3 point)
        {
            _data.StartPoint = point;
            _data.HasStart = true;
            _interaction.Mode = InteractionMode.SetEnd;
            if (_data.HasStart && _data.HasEnd)
                _data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask());
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        private void OnEndPointSet(Vector3 point)
        {
            _data.EndPoint = point;
            _data.HasEnd = true;
            if (_data.HasStart && _data.HasEnd)
                _data.FindPath(_data.StartPoint, _data.EndPoint, ResolvePathMask());
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        private void OnTriangleSelected(int triIdx)
        {
            _plugin.UpdateOverlays();
            _dock.Refresh();
        }

        private void OnAgentPlaced(Vector3 point)
        {
            int idx = _agentSim.AddAgent(point);
            if (idx >= 0) _interaction.SelectedAgentIndex = idx;
            RequestDynamicRedraw();
            _dock.Refresh();
        }

        private void OnAgentDestinationSet(int agentIdx, Vector3 dest)
        {
            if (!_agentSim.SetAgentDestination(agentIdx, dest.ToFPVector3()))
                GD.PushWarning($"[GodotFPNavMeshVisualizer] {_agentSim.LastDestinationRefusal}");
            RequestDynamicRedraw();
            _dock.Refresh();
        }
    }
}
#endif
