// Mouse/keyboard interaction for the FPNavMesh visualizer: hover tracking + Shift+Click picking,
// driven by _forward_3d_gui_input params and Camera3D.ProjectRayOrigin/Normal.
#if TOOLS
using System;

using global::Godot;

namespace xpTURN.Klotho.Godot
{
    internal enum InteractionMode
    {
        None,
        SetStart,
        SetEnd,
        InspectTriangle,
        PlaceAgent,
        SetAgentDest,
        PlaceBuilding,
    }

    internal class GodotFPNavMeshInteraction
    {
        public InteractionMode Mode;
        public int HoveredTriangleIndex = -1;
        public (int col, int row) HoveredCell = (-1, -1);
        public int SelectedTriangleIndex = -1;
        public int SelectedAgentIndex = -1;

        private GodotFPNavMeshVisualizerData _data;

        public event Action<Vector3> OnStartPointSet;
        public event Action<Vector3> OnEndPointSet;
        public event Action<int> OnTriangleSelected;
        public event Action<Vector3> OnAgentPlaced;
        public event Action<int, Vector3> OnAgentDestinationSet;
        public event Action<Vector3> OnBuildingPlaced;

        public void SetData(GodotFPNavMeshVisualizerData data) => _data = data;

        /// <summary>
        /// Returns true when the event was consumed (caller should return AFTER_GUI_INPUT_STOP).
        /// </summary>
        public bool ProcessInput(Camera3D camera, InputEvent ev)
        {
            if (_data == null || !_data.IsLoaded || Mode == InteractionMode.None) return false;

            if (ev is InputEventMouseMotion mm)
            {
                UpdateHover(camera, mm.Position);
                return false;
            }

            if (ev is InputEventMouseButton mb && mb.Pressed &&
                mb.ButtonIndex == MouseButton.Left && mb.ShiftPressed)
            {
                if (Raycast(camera, mb.Position, out Vector3 hit, out int triIdx))
                {
                    HandleClick(hit, triIdx);
                    return true;
                }
            }
            return false;
        }

        private void UpdateHover(Camera3D camera, Vector2 mousePos)
        {
            if (Raycast(camera, mousePos, out Vector3 hit, out int triIdx))
            {
                HoveredTriangleIndex = triIdx;
                HoveredCell = _data.GetGridCell(hit);
            }
            else
            {
                HoveredTriangleIndex = -1;
                HoveredCell = (-1, -1);
            }
        }

        private void HandleClick(Vector3 hitPoint, int triIdx)
        {
            switch (Mode)
            {
                case InteractionMode.SetStart:
                    OnStartPointSet?.Invoke(hitPoint);
                    break;
                case InteractionMode.SetEnd:
                    OnEndPointSet?.Invoke(hitPoint);
                    break;
                case InteractionMode.InspectTriangle:
                    SelectedTriangleIndex = triIdx;
                    OnTriangleSelected?.Invoke(triIdx);
                    break;
                case InteractionMode.PlaceAgent:
                    OnAgentPlaced?.Invoke(hitPoint);
                    break;
                case InteractionMode.SetAgentDest:
                    if (SelectedAgentIndex >= 0)
                        OnAgentDestinationSet?.Invoke(SelectedAgentIndex, hitPoint);
                    break;

                case InteractionMode.PlaceBuilding:
                    OnBuildingPlaced?.Invoke(hitPoint);
                    break;
            }
        }

        private bool Raycast(Camera3D camera, Vector2 mousePos, out Vector3 hitPoint, out int triIdx)
        {
            hitPoint = Vector3.Zero;
            triIdx = -1;
            if (camera == null) return false;

            Vector3 origin = camera.ProjectRayOrigin(mousePos);
            Vector3 dir = camera.ProjectRayNormal(mousePos);
            return _data.RaycastNavMesh(origin, dir, out hitPoint, out triIdx);
        }
    }
}
#endif
