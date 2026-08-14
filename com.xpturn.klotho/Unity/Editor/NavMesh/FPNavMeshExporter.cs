using System.IO;
using System.Collections.Generic;
using System.Text;

using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Editor tool for converting Unity NavMesh → FPNavMesh.
    /// Menu: Tools/Klotho/Export NavMesh
    /// </summary>
    public static class FPNavMeshExporter
    {
        private const float WELD_EPSILON = 0.001f;
        private const double DEFAULT_CELL_SIZE = 4.0;
        // Geometry robustness constants (degenerate / T-Junction epsilons) now live in
        // FPNavMeshBuildPipeline — single definition shared by Unity + Godot exporters.

        [MenuItem("Tools/Klotho/Export NavMesh")]
        public static void ExportNavMesh()
        {
            string sceneName = SceneManager.GetActiveScene().name;

            var triangulation = NavMesh.CalculateTriangulation();

            if (triangulation.vertices.Length == 0 || triangulation.indices.Length == 0)
            {
                Debug.LogError("[FPNavMeshExporter] No NavMesh found. Please bake the NavMesh first.");
                return;
            }

            string path = EditorUtility.SaveFilePanel(
                "Export NavMesh", "Assets", $"{sceneName}.NavMeshData", "bytes");

            if (string.IsNullOrEmpty(path))
                return;

            // Bake settings block: recorded into the asset so it is self-describing (radius feeds
            // the obstacle inset at load time; slope/height/climb recorded for future consumers).
            // CalculateTriangulation() merges without per-agent-type identity, so read agent
            // type 0 (Humanoid default — the type our stages bake with).
            var bakeSettings = NavMesh.GetSettingsByID(0);

            // CalculateTriangulation() carries no per-agent-type identity, so the recorded radius is
            // read from type 0. If the scene defines more than one agent type (or bakes with a
            // non-type-0 agent), that assumption can silently record the wrong BakeAgentRadius →
            // wrong runtime ObstacleRadiusInset. Warn so it is caught at export, not at runtime.
            if (NavMesh.GetSettingsCount() > 1)
            {
                Debug.LogWarning(
                    $"[FPNavMeshExporter] {NavMesh.GetSettingsCount()} NavMesh agent types present — " +
                    $"BakeAgentRadius is recorded from agent type 0 (radius {bakeSettings.agentRadius:F3}). " +
                    $"Verify this stage bakes with type 0, else the obstacle inset will be wrong.");
            }

            FPNavMesh navMesh = Build(triangulation.vertices, triangulation.indices, triangulation.areas,
                DEFAULT_CELL_SIZE,
                bakeSettings.agentRadius, bakeSettings.agentSlope,
                bakeSettings.agentHeight, bakeSettings.agentClimb);
            int size = FPNavMeshSerializer.GetSerializedSize(navMesh);
            int written;
            using (var buf = SerializationBuffer.Create(size))
            {
                var writer = new SpanWriter(buf.Span);
                FPNavMeshSerializer.Serialize(ref writer, navMesh);
                written = writer.Position;
                byte[] data = buf.Span.Slice(0, written).ToArray();
                File.WriteAllBytes(path, data);
            }

            string jsonPath = Path.ChangeExtension(path, ".json");
            File.WriteAllText(jsonPath, FPNavMeshSerializer.ToJson(navMesh), Encoding.UTF8);

            // Refresh AssetDatabase if the path is inside the Unity project
            if (path.StartsWith(Application.dataPath))
            {
                string relativePath = "Assets" + path.Substring(Application.dataPath.Length);
                AssetDatabase.ImportAsset(relativePath);
                string relativeJsonPath = "Assets" + jsonPath.Substring(Application.dataPath.Length);
                AssetDatabase.ImportAsset(relativeJsonPath);
            }

            Debug.Log($"[FPNavMeshExporter] Export complete: " +
                      $"vertices {navMesh.Vertices.Length}, triangles {navMesh.Triangles.Length}, " +
                      $"grid {navMesh.GridWidth}x{navMesh.GridHeight}, " +
                      $"bakeAgentRadius {navMesh.BakeAgentRadius.ToFloat():F3}, {written} bytes");
            Debug.Log($"[FPNavMeshExporter] Saved JSON: {jsonPath}");
        }

        /// <summary>
        /// Converts Unity NavMesh data to FPNavMesh.
        /// Public static to allow unit testing.
        /// </summary>
        public static FPNavMesh Build(Vector3[] srcVertices, int[] srcIndices, int[] srcAreas, double cellSize,
            double bakeAgentRadius = 0, double bakeMaxSlopeDeg = 0,
            double bakeAgentHeight = 0, double bakeAgentClimb = 0)
        {
            // 1. FP64 conversion + vertex welding
            WeldVertices(srcVertices, WELD_EPSILON,
                out FPVector3[] vertices, out int[] indexRemap);

            // Index remap
            int[] indices = new int[srcIndices.Length];
            for (int i = 0; i < srcIndices.Length; i++)
                indices[i] = indexRemap[srcIndices[i]];

            // Per-triangle area (Clone: pipeline mutates via ref → defensive copy)
            int[] areas = (int[])srcAreas.Clone();

            // Delegate the engine-agnostic geometry pipeline. Diagnostics routed to the Unity
            // console through the IKLogger contract (UnityDebug sink).
            using var loggerFactory = KLoggerFactory.Create(logging => logging.AddUnityDebug());
            IKLogger logger = loggerFactory.CreateLogger("FPNavMeshExporter");
            return FPNavMeshBuildPipeline.Build(
                vertices, indices, areas, cellSize,
                logger,
                bakeAgentRadius: bakeAgentRadius, bakeMaxSlopeDeg: bakeMaxSlopeDeg,
                bakeAgentHeight: bakeAgentHeight, bakeAgentClimb: bakeAgentClimb);
        }

        #region Vertex welding

        /// <summary>
        /// Merges duplicate vertices within epsilon distance.
        /// Unity CalculateTriangulation() results may contain duplicate vertices.
        /// </summary>
        private static void WeldVertices(Vector3[] srcVertices, float epsilon,
            out FPVector3[] outVertices, out int[] indexRemap)
        {
            float epsilonSqr = epsilon * epsilon;
            var welded = new List<FPVector3>();
            var weldedSrc = new List<Vector3>();
            indexRemap = new int[srcVertices.Length];

            for (int i = 0; i < srcVertices.Length; i++)
            {
                Vector3 sv = srcVertices[i];
                int found = -1;

                for (int j = 0; j < weldedSrc.Count; j++)
                {
                    float dx = sv.x - weldedSrc[j].x;
                    float dy = sv.y - weldedSrc[j].y;
                    float dz = sv.z - weldedSrc[j].z;
                    if (dx * dx + dy * dy + dz * dz < epsilonSqr)
                    {
                        found = j;
                        break;
                    }
                }

                if (found >= 0)
                {
                    indexRemap[i] = found;
                }
                else
                {
                    indexRemap[i] = welded.Count;
                    weldedSrc.Add(sv);
                    welded.Add(sv.ToFPVector3());
                }
            }

            outVertices = welded.ToArray();
        }

        #endregion
    }
}
