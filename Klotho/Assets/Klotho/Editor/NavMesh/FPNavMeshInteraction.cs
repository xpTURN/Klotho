using System;
using UnityEditor;
using UnityEngine;
namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Interaction modes for the NavMesh visualizer.
    /// </summary>
    internal enum InteractionMode
    {
        None,
        SetStart,
        SetEnd,
        InspectTriangle,
        PlaceAgent,
        SetAgentDest,
    }

    /// <summary>
    /// Handles mouse and keyboard input for the NavMesh visualizer.
    /// </summary>
    internal class FPNavMeshInteraction
    {
        public InteractionMode Mode;
        public int HoveredTriangleIndex = -1;
        public (int col, int row) HoveredCell = (-1, -1);
        public int SelectedTriangleIndex = -1;
        public int SelectedAgentIndex = -1;

        private FPNavMeshVisualizerData _data;

        public event Action<Vector3> OnStartPointSet;
        public event Action<Vector3> OnEndPointSet;
        public event Action<int> OnTriangleSelected;
        public event Action<Vector3> OnAgentPlaced;
        public event Action<int, Vector3> OnAgentDestinationSet;

        public void SetData(FPNavMeshVisualizerData data)
        {
            _data = data;
        }

        public void ProcessSceneInput(SceneView sceneView)
        {
            if (_data == null || !_data.IsLoaded) return;
            if (Mode == InteractionMode.None) return;

            Event e = Event.current;

            // Update hover (in all modes)
            UpdateHover(e);

            // Handle interaction only on Shift+click
            if (e.type == EventType.MouseDown && e.button == 0 && e.shift)
            {
                if (RaycastNavMesh(e.mousePosition, out Vector3 hitPoint, out int triIdx, true))
                {
                    HandleClick(hitPoint, triIdx);
                    e.Use();
                }
            }

            // Disable SceneView default controls while Shift is held
            if (e.shift && Mode != InteractionMode.None)
            {
                if (e.type == EventType.Layout)
                {
                    HandleUtility.AddDefaultControl(
                        GUIUtility.GetControlID(FocusType.Passive));
                }
            }
        }

        private void UpdateHover(Event e)
        {
            if (e.type != EventType.MouseMove && e.type != EventType.Repaint) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_data.RaycastNavMesh(ray, out Vector3 hitPoint, out int triIdx))
            {
                HoveredTriangleIndex = triIdx;
                HoveredCell = _data.GetGridCell(hitPoint);
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
            }
        }

        private bool RaycastNavMesh(Vector2 mousePos, out Vector3 hitPoint, out int triIdx, bool enableLog = false)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
            return _data.RaycastNavMesh(ray, out hitPoint, out triIdx, enableLog);
        }
    }
}
