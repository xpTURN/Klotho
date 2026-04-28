using UnityEngine;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// GUI style definitions for the NavMesh visualizer.
    /// </summary>
    internal static class FPNavMeshVisualizerStyles
    {
        // NavMesh geometry
        public static readonly Color TriangleFill = new Color(0.2f, 0.6f, 0.9f, 0.15f);
        public static readonly Color TriangleFillBlocked = new Color(0.9f, 0.2f, 0.2f, 0.25f);
        public static readonly Color EdgeInternal = new Color(0.3f, 0.3f, 0.3f, 0.4f);
        public static readonly Color EdgeBoundary = new Color(1.0f, 0.4f, 0.0f, 0.9f);
        public static readonly Color Vertex = new Color(1.0f, 1.0f, 0.0f, 0.8f);
        public static readonly Color TriangleCenter = new Color(0.5f, 0.5f, 0.5f, 0.6f);

        public const float EdgeInternalWidth = 1.0f;
        public const float EdgeBoundaryWidth = 3.0f;
        public const float VertexSize = 0.08f;

        // Path
        public static readonly Color CorridorFill = new Color(1.0f, 0.8f, 0.0f, 0.3f);
        public static readonly Color CorridorEdge = new Color(1.0f, 0.8f, 0.0f, 0.7f);
        public static readonly Color WaypointLine = new Color(0.0f, 1.0f, 0.3f, 0.9f);
        public static readonly Color WaypointDot = new Color(0.0f, 1.0f, 0.0f, 1.0f);
        public static readonly Color PortalLine = new Color(0.8f, 0.0f, 1.0f, 0.6f);

        public const float WaypointLineWidth = 3.0f;
        public const float WaypointDotSize = 0.15f;
        public const float PortalLineWidth = 2.0f;

        // Start/end markers
        public static readonly Color StartMarker = new Color(0.0f, 0.8f, 0.0f, 1.0f);
        public static readonly Color EndMarker = new Color(0.9f, 0.0f, 0.0f, 1.0f);
        public const float MarkerSize = 0.3f;

        // Agents
        public static readonly Color AgentBody = new Color(0.0f, 0.5f, 1.0f, 0.8f);
        public static readonly Color AgentVelocity = new Color(0.0f, 1.0f, 0.5f, 0.9f);
        public static readonly Color AgentDesiredVel = new Color(1.0f, 1.0f, 0.0f, 0.6f);
        public static readonly Color AgentDestination = new Color(1.0f, 0.0f, 0.5f, 0.8f);
        public static readonly Color AgentPath = new Color(0.0f, 0.7f, 1.0f, 0.5f);

        // ORCA
        public static readonly Color OrcaLine = new Color(1.0f, 0.5f, 0.0f, 0.7f);
        public static readonly Color OrcaVelocity = new Color(1.0f, 0.0f, 1.0f, 0.8f);

        // Spatial grid
        public static readonly Color GridLine = new Color(0.5f, 0.5f, 0.5f, 0.2f);
        public static readonly Color GridHighlight = new Color(1.0f, 1.0f, 0.0f, 0.15f);
        public static readonly Color GridCellLabel = new Color(1.0f, 1.0f, 1.0f, 0.6f);

        public const float GridLineWidth = 1.0f;
    }
}
