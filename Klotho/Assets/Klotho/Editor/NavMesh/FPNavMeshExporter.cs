using System.IO;
using System.Collections.Generic;
using System.Text;

using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

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
        private const double DEGENERATE_AREA_EPSILON = 0.0001f;
        private const double T_JUNCTION_EPSILON = 0.002;
        private const double T_JUNCTION_HEIGHT_EPSILON = 0.5;
        private const double DEFAULT_CELL_SIZE = 4.0;

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

            FPNavMesh navMesh = Build(triangulation.vertices, triangulation.indices, triangulation.areas, DEFAULT_CELL_SIZE);
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
            File.WriteAllText(jsonPath, ToJson(navMesh), Encoding.UTF8);

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
                      $"grid {navMesh.GridWidth}x{navMesh.GridHeight}, {written} bytes");
            Debug.Log($"[FPNavMeshExporter] Saved JSON: {jsonPath}");
        }

        /// <summary>
        /// Converts Unity NavMesh data to FPNavMesh.
        /// Public static to allow unit testing.
        /// </summary>
        public static FPNavMesh Build(Vector3[] srcVertices, int[] srcIndices, int[] srcAreas, double cellSize)
        {
            // 1. FP64 conversion + vertex welding
            WeldVertices(srcVertices, WELD_EPSILON,
                out FPVector3[] vertices, out int[] indexRemap);

            // Index remap
            int[] indices = new int[srcIndices.Length];
            for (int i = 0; i < srcIndices.Length; i++)
                indices[i] = indexRemap[srcIndices[i]];

            // Build areas array (per triangle, after index remap)
            int[] areas = new int[indices.Length / 3];
            for (int i = 0; i < areas.Length; i++)
                areas[i] = srcAreas[i];

            // 2. Remove degenerate triangles (shrink indices + areas together)
            RemoveDegenerateTriangles(vertices, ref indices, ref areas, DEGENERATE_AREA_EPSILON);

            // 3. T-Junction detection & edge splitting
            SplitTJunctions(vertices, ref indices, ref areas,
                T_JUNCTION_EPSILON, T_JUNCTION_HEIGHT_EPSILON);

            // 3.1 Re-check degenerate triangles after splitting
            RemoveDegenerateTriangles(vertices, ref indices, ref areas, DEGENERATE_AREA_EPSILON);

            // 4. Create triangle structs + precompute data
            int triCount = indices.Length / 3;
            var triangles = new FPNavMeshTriangle[triCount];
            for (int i = 0; i < triCount; i++)
            {
                int v0 = indices[i * 3];
                int v1 = indices[i * 3 + 1];
                int v2 = indices[i * 3 + 2];

                FPVector2 a = vertices[v0].ToXZ();
                FPVector2 b = vertices[v1].ToXZ();
                FPVector2 c = vertices[v2].ToXZ();

                // Compute Y range (multi-level space support)
                FP64 y0 = vertices[v0].y;
                FP64 y1 = vertices[v1].y;
                FP64 y2 = vertices[v2].y;
                FP64 minY = FP64.Min(FP64.Min(y0, y1), y2);
                FP64 maxY = FP64.Max(FP64.Max(y0, y1), y2);
                FP64 centerY = (minY + maxY) * FP64.Half;

                triangles[i] = new FPNavMeshTriangle
                {
                    v0 = v0, v1 = v1, v2 = v2,
                    neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                    portal0Left = -1, portal0Right = -1,
                    portal1Left = -1, portal1Right = -1,
                    portal2Left = -1, portal2Right = -1,
                    centerXZ = new FPVector2(
                        (a.x + b.x + c.x) / FP64.FromInt(3),
                        (a.y + b.y + c.y) / FP64.FromInt(3)),
                    area = FP64.Abs(FPNavMeshQuery.TriangleArea2D(a, b, c)),
                    areaMask = 1 << areas[i],
                    costMultiplier = FP64.One,
                    isBlocked = false,
                    minY = minY,
                    maxY = maxY,
                    centerY = centerY,
                };
            }

            // 5. Build adjacency + portals
            BuildAdjacency(triangles, vertices);

            // 6. Compute XZ bounds
            FPBounds2 boundsXZ = ComputeBoundsXZ(vertices);

            // 7. Build spatial grid
            FP64 fpCellSize = FP64.FromDouble(cellSize);
            BuildSpatialGrid(vertices, triangles, boundsXZ, fpCellSize,
                out int gridWidth, out int gridHeight, out FPVector2 gridOrigin,
                out int[] gridCells, out int[] gridTriangles);

            return new FPNavMesh(
                vertices, triangles, boundsXZ,
                gridCells, gridTriangles,
                gridWidth, gridHeight, fpCellSize, gridOrigin);
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

        #region Degenerate triangle removal

        private static void RemoveDegenerateTriangles(FPVector3[] vertices, ref int[] indices, ref int[] areas, double areaEpsilon)
        {
            FP64 fpEpsilon = FP64.FromDouble(areaEpsilon);
            var valid = new List<int>();
            var validAreas = new List<int>();

            for (int i = 0; i < indices.Length; i += 3)
            {
                int v0 = indices[i];
                int v1 = indices[i + 1];
                int v2 = indices[i + 2];

                // Check for duplicate indices
                if (v0 == v1 || v1 == v2 || v2 == v0)
                    continue;

                FPVector2 a = vertices[v0].ToXZ();
                FPVector2 b = vertices[v1].ToXZ();
                FPVector2 c = vertices[v2].ToXZ();

                FP64 area = FP64.Abs(FPNavMeshQuery.TriangleArea2D(a, b, c));
                if (area < fpEpsilon)
                    continue;

                valid.Add(v0);
                valid.Add(v1);
                valid.Add(v2);
                validAreas.Add(areas[i / 3]);
            }

            indices = valid.ToArray();
            areas = validAreas.ToArray();
        }

        #endregion

        #region T-Junction splitting

        /// <summary>
        /// Detects T-Junctions and splits edges.
        /// When a vertex of one triangle lies on the edge of another,
        /// splits that triangle to restore 1-to-1 edge adjacency.
        /// </summary>
        private static void SplitTJunctions(
            FPVector3[] vertices, ref int[] indices, ref int[] areas,
            double epsilon, double heightEpsilon)
        {
            double epsSq = epsilon * epsilon;
            int maxIterations = 10;
            int initialTriCount = indices.Length / 3;
            int totalSplits = 0;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                int triCount = indices.Length / 3;

                // Safety limit on triangle count — prevent OOM
                if (triCount > initialTriCount * 20)
                {
                    Debug.LogError($"[FPNavMeshExporter] T-Junction split aborted: triangle count " +
                        $"exceeded safety limit ({triCount} > {initialTriCount * 20}). " +
                        $"Possible false T-junction detection. Check mesh geometry.");
                    break;
                }

                bool anySplit = false;
                int splitCount = 0;

                var newIndices = new List<int>(indices.Length);
                var newAreas = new List<int>(areas.Length);

                for (int t = 0; t < triCount; t++)
                {
                    int i0 = indices[t * 3];
                    int i1 = indices[t * 3 + 1];
                    int i2 = indices[t * 3 + 2];

                    // Search for T-Junction vertices on each edge
                    int splitEdge = -1;
                    var midVertices = new List<(int vertIdx, double tParam)>();

                    for (int e = 0; e < 3; e++)
                    {
                        int ea, eb;
                        switch (e)
                        {
                            case 0: ea = i0; eb = i1; break;
                            case 1: ea = i1; eb = i2; break;
                            default: ea = i2; eb = i0; break;
                        }

                        midVertices.Clear();
                        FindVerticesOnEdge(vertices,
                            ea, eb, epsSq, heightEpsilon, midVertices);

                        if (midVertices.Count > 0)
                        {
                            splitEdge = e;
                            break;
                        }
                    }

                    if (splitEdge >= 0)
                    {
                        midVertices.Sort((a, b) => a.tParam.CompareTo(b.tParam));

                        int eA, eB, eC;
                        switch (splitEdge)
                        {
                            case 0: eA = i0; eB = i1; eC = i2; break;
                            case 1: eA = i1; eB = i2; eC = i0; break;
                            default: eA = i2; eB = i0; eC = i1; break;
                        }

                        // Fan split: (eA, M0, eC), (M0, M1, eC), ..., (Mn, eB, eC)
                        int prev = eA;
                        for (int m = 0; m < midVertices.Count; m++)
                        {
                            newIndices.Add(prev);
                            newIndices.Add(midVertices[m].vertIdx);
                            newIndices.Add(eC);
                            newAreas.Add(areas[t]);
                            prev = midVertices[m].vertIdx;
                        }
                        newIndices.Add(prev);
                        newIndices.Add(eB);
                        newIndices.Add(eC);
                        newAreas.Add(areas[t]);

                        anySplit = true;
                        splitCount++;
                    }
                    else
                    {
                        newIndices.Add(i0);
                        newIndices.Add(i1);
                        newIndices.Add(i2);
                        newAreas.Add(areas[t]);
                    }
                }

                indices = newIndices.ToArray();
                areas = newAreas.ToArray();
                totalSplits += splitCount;

                Debug.Log($"[FPNavMeshExporter] T-Junction split: iteration {iter + 1}, " +
                    $"split {splitCount} triangles ({triCount} → {indices.Length / 3})");

                if (!anySplit) break;

                if (iter == maxIterations - 1)
                {
                    Debug.LogError($"[FPNavMeshExporter] T-Junction split did not converge " +
                        $"after {maxIterations} iterations ({totalSplits} total splits, " +
                        $"{initialTriCount} → {indices.Length / 3} triangles). " +
                        $"Remaining T-Junctions may cause pathfinding disconnection.");
                }
            }
        }

        /// <summary>
        /// Finds vertices lying on edge (eA, eB).
        /// Iterates the vertex array directly in O(vertexCount).
        /// (Previously: iterated all indices O(triCount*3) — triCount grows on repeated splits)
        /// </summary>
        private static void FindVerticesOnEdge(
            FPVector3[] vertices,
            int eA, int eB,
            double epsSq, double heightEpsilon,
            List<(int vertIdx, double tParam)> result)
        {
            FPVector3 a3 = vertices[eA];
            FPVector3 b3 = vertices[eB];
            double ax = a3.x.ToDouble(), az = a3.z.ToDouble(), ay = a3.y.ToDouble();
            double bx = b3.x.ToDouble(), bz = b3.z.ToDouble(), by = b3.y.ToDouble();
            double abx = bx - ax, abz = bz - az, aby = by - ay;
            double abLenSq = abx * abx + abz * abz;

            if (abLenSq < epsSq) return;

            for (int vi = 0; vi < vertices.Length; vi++)
            {
                if (vi == eA || vi == eB) continue;

                FPVector3 p3 = vertices[vi];
                double px = p3.x.ToDouble(), pz = p3.z.ToDouble(), py = p3.y.ToDouble();

                // point-on-segment (XZ)
                double apx = px - ax, apz = pz - az;
                double cross = apx * abz - apz * abx;
                if (cross * cross > epsSq * abLenSq) continue;

                // Endpoint proximity check (actual distance based on epsilon)
                double distToASq = apx * apx + apz * apz;
                if (distToASq < epsSq) continue;
                double bpx = px - bx, bpz = pz - bz;
                double distToBSq = bpx * bpx + bpz * bpz;
                if (distToBSq < epsSq) continue;

                double dot = apx * abx + apz * abz;
                double tParam = dot / abLenSq;

                // Segment range validation
                if (tParam <= 0.0 || tParam >= 1.0) continue;

                // Height validation
                double edgeY = ay + tParam * aby;
                if (System.Math.Abs(py - edgeY) > heightEpsilon) continue;

                result.Add((vi, tParam));
            }
        }

        #endregion

        #region Adjacency building

        /// <summary>
        /// Builds adjacency and portals using edge key (min, max) → Dictionary.
        /// </summary>
        private static void BuildAdjacency(FPNavMeshTriangle[] triangles, FPVector3[] vertices)
        {
            // (minVertIdx, maxVertIdx) → (triIdx, edgeLocalIdx)
            var edgeMap = new Dictionary<long, (int triIdx, int edgeIdx)>();

            for (int t = 0; t < triangles.Length; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    triangles[t].GetEdgeVertices(e, out int va, out int vb);

                    int minV = va < vb ? va : vb;
                    int maxV = va < vb ? vb : va;
                    long key = ((long)minV << 32) | (uint)maxV;

                    if (edgeMap.TryGetValue(key, out var other))
                    {
                        // Set adjacency
                        triangles[t].SetNeighbor(e, other.triIdx);
                        triangles[other.triIdx].SetNeighbor(other.edgeIdx, t);

                        // Set portal: determine left/right via cross product from opposite vertex
                        ComputePortalLeftRight(ref triangles[t], e, vertices, out int leftV, out int rightV);
                        triangles[t].SetPortal(e, leftV, rightV);

                        ComputePortalLeftRight(ref triangles[other.triIdx], other.edgeIdx, vertices,
                            out leftV, out rightV);
                        triangles[other.triIdx].SetPortal(other.edgeIdx, leftV, rightV);

                        edgeMap.Remove(key);
                    }
                    else
                    {
                        edgeMap[key] = (t, e);
                    }
                }
            }
            // Edges remaining in edgeMap = NavMesh boundary (neighbor = -1, portal = -1 kept)

            if (edgeMap.Count > 0)
            {
                // Non-manifold edge warning can occur when shared by 3+ triangles
                // Here these are simply boundary edges — normal
            }
        }

        /// <summary>
        /// Determines left/right portal vertex indices for a triangle edge relative to travel direction.
        /// Approximates travel direction as opposite vertex → edge midpoint,
        /// then uses cross product sign to determine left/right.
        /// </summary>
        private static void ComputePortalLeftRight(
            ref FPNavMeshTriangle tri, int edgeIndex, FPVector3[] vertices,
            out int left, out int right)
        {
            tri.GetEdgeVertices(edgeIndex, out int va, out int vb);
            FPVector2 a = vertices[va].ToXZ();
            FPVector2 b = vertices[vb].ToXZ();

            int oppositeVert = edgeIndex == 0 ? tri.v2 : edgeIndex == 1 ? tri.v0 : tri.v1;
            FPVector2 opp = vertices[oppositeVert].ToXZ();

            FPVector2 edgeMid = (a + b) * FP64.Half;
            FPVector2 travelDir = edgeMid - opp;

            // Cross(travelDir, a - b) < 0 means a is on the left
            if (FPVector2.Cross(travelDir, a - b) < FP64.Zero)
            {
                left = va;
                right = vb;
            }
            else
            {
                left = vb;
                right = va;
            }
        }

        #endregion

        #region Bounds computation

        private static FPBounds2 ComputeBoundsXZ(FPVector3[] vertices)
        {
            if (vertices.Length == 0)
                return default;

            FPVector2 mn = vertices[0].ToXZ();
            FPVector2 mx = vertices[0].ToXZ();

            for (int i = 1; i < vertices.Length; i++)
            {
                FPVector2 xz = vertices[i].ToXZ();
                mn = FPVector2.Min(mn, xz);
                mx = FPVector2.Max(mx, xz);
            }

            FPBounds2 bounds = default;
            bounds.SetMinMax(mn, mx);
            return bounds;
        }

        #endregion

        #region Spatial grid building

        private static void BuildSpatialGrid(
            FPVector3[] vertices,
            FPNavMeshTriangle[] triangles,
            FPBounds2 boundsXZ,
            FP64 cellSize,
            out int gridWidth, out int gridHeight, out FPVector2 gridOrigin,
            out int[] gridCells, out int[] gridTriangles)
        {
            gridOrigin = boundsXZ.min;
            FPVector2 size = boundsXZ.size;

            gridWidth = FP64.Ceiling(size.x / cellSize).ToInt() + 1;
            gridHeight = FP64.Ceiling(size.y / cellSize).ToInt() + 1;

            int totalCells = gridWidth * gridHeight;

            // Pass 1: collect list of triangles per cell
            var cellLists = new List<int>[totalCells];
            for (int i = 0; i < totalCells; i++)
                cellLists[i] = new List<int>();

            for (int t = 0; t < triangles.Length; t++)
            {
                // Triangle AABB (XZ)
                FPVector2 a = vertices[triangles[t].v0].ToXZ();
                FPVector2 b = vertices[triangles[t].v1].ToXZ();
                FPVector2 c = vertices[triangles[t].v2].ToXZ();

                FPVector2 triMin = FPVector2.Min(FPVector2.Min(a, b), c);
                FPVector2 triMax = FPVector2.Max(FPVector2.Max(a, b), c);

                int colMin = ((triMin.x - gridOrigin.x) / cellSize).ToInt();
                int colMax = ((triMax.x - gridOrigin.x) / cellSize).ToInt();
                int rowMin = ((triMin.y - gridOrigin.y) / cellSize).ToInt();
                int rowMax = ((triMax.y - gridOrigin.y) / cellSize).ToInt();

                // Clamp
                if (colMin < 0) colMin = 0;
                if (rowMin < 0) rowMin = 0;
                if (colMax >= gridWidth) colMax = gridWidth - 1;
                if (rowMax >= gridHeight) rowMax = gridHeight - 1;

                for (int r = rowMin; r <= rowMax; r++)
                {
                    for (int col = colMin; col <= colMax; col++)
                    {
                        int cellIdx = r * gridWidth + col;
                        cellLists[cellIdx].Add(t);
                    }
                }
            }

            // Pass 2: compress to flat arrays
            gridCells = new int[totalCells * 2];
            int totalTris = 0;
            for (int i = 0; i < totalCells; i++)
                totalTris += cellLists[i].Count;

            gridTriangles = new int[totalTris];
            int offset = 0;

            for (int i = 0; i < totalCells; i++)
            {
                gridCells[i * 2] = offset;
                gridCells[i * 2 + 1] = cellLists[i].Count;

                for (int j = 0; j < cellLists[i].Count; j++)
                    gridTriangles[offset + j] = cellLists[i][j];

                offset += cellLists[i].Count;
            }
        }

        #endregion

        #region JSON output

        private static string ToJson(FPNavMesh navMesh)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");

            // vertices
            sb.AppendLine("  \"vertices\": [");
            for (int i = 0; i < navMesh.Vertices.Length; i++)
            {
                var v = navMesh.Vertices[i];
                sb.Append($"    [{v.x.ToFloat()},{v.y.ToFloat()},{v.z.ToFloat()}]");
                if (i < navMesh.Vertices.Length - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            // triangles
            sb.AppendLine("  \"triangles\": [");
            for (int i = 0; i < navMesh.Triangles.Length; i++)
            {
                var t = navMesh.Triangles[i];
                sb.Append("    {");
                sb.Append($"\"v0\":{t.v0},\"v1\":{t.v1},\"v2\":{t.v2}");
                sb.Append($",\"neighbor0\":{t.neighbor0},\"neighbor1\":{t.neighbor1},\"neighbor2\":{t.neighbor2}");
                sb.Append($",\"portal0Left\":{t.portal0Left},\"portal0Right\":{t.portal0Right}");
                sb.Append($",\"portal1Left\":{t.portal1Left},\"portal1Right\":{t.portal1Right}");
                sb.Append($",\"portal2Left\":{t.portal2Left},\"portal2Right\":{t.portal2Right}");
                sb.Append($",\"centerXZ\":[{t.centerXZ.x.ToFloat()},{t.centerXZ.y.ToFloat()}]");
                sb.Append($",\"area\":{t.area.ToFloat()}");
                sb.Append($",\"areaMask\":{t.areaMask}");
                sb.Append($",\"costMultiplier\":{t.costMultiplier.ToFloat()}");
                sb.Append($",\"isBlocked\":{(t.isBlocked ? "true" : "false")}");
                sb.Append($",\"minY\":{t.minY.ToFloat()},\"maxY\":{t.maxY.ToFloat()},\"centerY\":{t.centerY.ToFloat()}");
                sb.Append('}');
                if (i < navMesh.Triangles.Length - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            // bounds
            sb.Append($"  \"boundsXZ\": {{\"center\":[{navMesh.BoundsXZ.center.x.ToFloat()},{navMesh.BoundsXZ.center.y.ToFloat()}]");
            sb.AppendLine($",\"extents\":[{navMesh.BoundsXZ.extents.x.ToFloat()},{navMesh.BoundsXZ.extents.y.ToFloat()}]}},");

            // grid metadata
            sb.AppendLine($"  \"gridWidth\": {navMesh.GridWidth},");
            sb.AppendLine($"  \"gridHeight\": {navMesh.GridHeight},");
            sb.AppendLine($"  \"gridCellSize\": {navMesh.GridCellSize.ToFloat()},");
            sb.AppendLine($"  \"gridOrigin\": [{navMesh.GridOrigin.x.ToFloat()},{navMesh.GridOrigin.y.ToFloat()}]");

            sb.Append('}');
            return sb.ToString();
        }

        #endregion
    }
}
