// Converts a Godot NavigationRegion3D's NavigationMesh into a deterministic FPNavMesh (.bytes).
// plugin.gd instantiates this [GlobalClass] and calls ExportNavMesh(region).
// Wrapped in #if TOOLS so it compiles only into the editor build.
//
// Engine-specific part only (mesh extraction + vertex weld + save). The geometry pipeline
// (degenerate removal, T-Junction split, adjacency, spatial grid) is the engine-agnostic
// FPNavMeshBuildPipeline (Runtime assembly).
#if TOOLS
using System.Collections.Generic;

using global::Godot;

using xpTURN.Klotho.Serialization;            // SerializationBuffer, SpanWriter
using xpTURN.Klotho.Deterministic.Math;       // FPVector3, ToFPVector3
using xpTURN.Klotho.Deterministic.Navigation; // FPNavMeshBuildPipeline, FPNavMeshSerializer, FPNavMesh

namespace xpTURN.Klotho.Godot
{
    [Tool]
    [GlobalClass]
    public partial class GodotFPNavMeshExporter : RefCounted
    {
        private const float WELD_EPSILON = 0.001f;
        private const double DEFAULT_CELL_SIZE = 4.0;
        // Geometry robustness constants live in FPNavMeshBuildPipeline (shared, single definition).

        public void ExportNavMesh(NavigationRegion3D region)
        {
            if (region == null) { GD.PushError("[GodotFPNavMeshExporter] NavigationRegion3D is null."); return; }

            var navMesh = region.NavigationMesh;
            if (navMesh == null) { GD.PushError("[GodotFPNavMeshExporter] NavigationMesh is missing."); return; }

            // Pass the region so GlobalTransform can be applied (local -> world).
            var (vertices, indices, areas) = ExtractTriangles(region, navMesh);
            // Check triangle presence via indices — vertices may exist while polygon count is 0.
            if (indices.Length == 0) { GD.PushError("[GodotFPNavMeshExporter] No triangles. Bake the NavigationMesh first."); return; }

            // Determine the output path (synchronous). Based on the edited scene's directory + region name.
            // GetEditedSceneRoot(): provides the edited scene path independent of the Owner chain.
            string sceneRes = EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath;
            string dir = string.IsNullOrEmpty(sceneRes) ? "res://" : sceneRes.GetBaseDir();
            // PathJoin: handles dir trailing slash ("res://") automatically — avoids "res:///" triple slash.
            string outPath = dir.PathJoin($"{region.Name}.NavMeshData.bytes");

            FPNavMesh fpNavMesh = FPNavMeshBuildPipeline.Build(
                vertices, indices, areas, DEFAULT_CELL_SIZE,
                log: m => GD.Print(m), logError: m => GD.PushError(m),
                // Bake settings block: recorded into the asset so it is self-describing (radius
                // feeds the obstacle inset at load time; slope/height/climb for future consumers).
                bakeAgentRadius: navMesh.AgentRadius, bakeMaxSlopeDeg: navMesh.AgentMaxSlope,
                bakeAgentHeight: navMesh.AgentHeight, bakeAgentClimb: navMesh.AgentMaxClimb);

            Save(fpNavMesh, outPath);
        }

        private static (FPVector3[] vertices, int[] indices, int[] areas) ExtractTriangles(
            NavigationRegion3D region, NavigationMesh navMesh)
        {
            global::Godot.Vector3[] srcVerts = navMesh.GetVertices();   // C# binding: Vector3[]
            Transform3D xform = region.GlobalTransform;                 // local -> world

            // WeldVertices (Godot.Vector3 based, GlobalTransform applied)
            WeldVertices(srcVerts, xform, WELD_EPSILON, out FPVector3[] vertices, out int[] indexRemap);

            // Fan triangulation — iterate polygons via GetPolygonCount()/GetPolygon(i).
            // Winding (CW/CCW) is irrelevant thanks to the pipeline's Abs() handling.
            var triIndices = new List<int>();
            int polyCount = navMesh.GetPolygonCount();
            for (int p = 0; p < polyCount; p++)
            {
                int[] poly = navMesh.GetPolygon(p);              // int[] vertex indices
                for (int i = 1; i < poly.Length - 1; i++)
                {
                    triIndices.Add(indexRemap[poly[0]]);
                    triIndices.Add(indexRemap[poly[i]]);
                    triIndices.Add(indexRemap[poly[i + 1]]);
                }
            }

            // All triangles fixed to area 0 (Godot has no per-polygon area).
            int triCount = triIndices.Count / 3;
            int[] areas = new int[triCount];   // default 0

            return (vertices, triIndices.ToArray(), areas);
        }

        /// <summary>
        /// Merges duplicate vertices within epsilon distance (Godot.Vector3, GlobalTransform applied).
        /// Welds in float space, before the deterministic conversion — kept engine-specific.
        /// </summary>
        private static void WeldVertices(global::Godot.Vector3[] srcVerts, Transform3D xform, float epsilon,
            out FPVector3[] outVertices, out int[] indexRemap)
        {
            float epsilonSqr = epsilon * epsilon;
            var welded = new List<FPVector3>();
            var weldedSrc = new List<global::Godot.Vector3>();
            indexRemap = new int[srcVerts.Length];

            for (int i = 0; i < srcVerts.Length; i++)
            {
                global::Godot.Vector3 sv = xform * srcVerts[i];   // local -> world
                int found = -1;
                for (int j = 0; j < weldedSrc.Count; j++)
                {
                    float dx = sv.X - weldedSrc[j].X;
                    float dy = sv.Y - weldedSrc[j].Y;
                    float dz = sv.Z - weldedSrc[j].Z;
                    if (dx * dx + dy * dy + dz * dz < epsilonSqr) { found = j; break; }
                }
                if (found >= 0) indexRemap[i] = found;
                else { indexRemap[i] = welded.Count; weldedSrc.Add(sv); welded.Add(sv.ToFPVector3()); }
            }
            outVertices = welded.ToArray();
        }

        private static void Save(FPNavMesh fpNavMesh, string resPath)
        {
            int size = FPNavMeshSerializer.GetSerializedSize(fpNavMesh);
            int written;
            byte[] data;
            using (var buf = SerializationBuffer.Create(size))
            {
                var writer = new SpanWriter(buf.Span);
                FPNavMeshSerializer.Serialize(ref writer, fpNavMesh);
                written = writer.Position;
                data = buf.Span.Slice(0, written).ToArray();
            }

            string absPath = ProjectSettings.GlobalizePath(resPath);
            using (var f = FileAccess.Open(absPath, FileAccess.ModeFlags.Write))
            {
                if (f == null) { GD.PushError($"[GodotFPNavMeshExporter] Failed to open file: {absPath}"); return; }
                f.StoreBuffer(data);
            }

            // JSON sidecar (debug / inspection) — engine-agnostic serializer (Runtime assembly).
            string jsonRes = resPath.GetBaseName() + ".json";
            string jsonAbs = ProjectSettings.GlobalizePath(jsonRes);
            using (var jf = FileAccess.Open(jsonAbs, FileAccess.ModeFlags.Write))
            {
                if (jf == null) { GD.PushError($"[GodotFPNavMeshExporter] Failed to open file: {jsonAbs}"); }
                else { jf.StoreString(FPNavMeshSerializer.ToJson(fpNavMesh)); }
            }

            EditorInterface.Singleton.GetResourceFilesystem().Scan();
            GD.Print($"[GodotFPNavMeshExporter] Export complete: {resPath} ({written} bytes), JSON: {jsonRes}");
        }
    }
}
#endif
