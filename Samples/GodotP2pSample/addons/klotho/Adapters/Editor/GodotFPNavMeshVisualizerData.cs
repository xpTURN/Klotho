// Data + render cache for the FPNavMesh visualizer. Loads a serialized FPNavMesh (.bytes) and
// delegates queries to the deterministic FP core (Query/Pathfinder/Funnel). Geometry is cached as
// Godot.Vector3 for drawing; raycast picking takes an (origin, dir) pair.
#if TOOLS
using System.Collections.Generic;

using global::Godot;

using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Godot
{
    /// <summary>
    /// Triangle rendering data. Three vertex positions plus metadata.
    /// </summary>
    internal struct TriangleRenderData
    {
        public Vector3 v0, v1, v2;
        public Vector3 center;
        public bool isBlocked;
        public int areaMask;
        public float costMultiplier;
        public int index;
        public int neighbor0, neighbor1, neighbor2;
    }

    /// <summary>
    /// Cached rendering data for the NavMesh visualizer. Manages triangles, vertices, and adjacency info.
    /// </summary>
    internal class GodotFPNavMeshVisualizerData
    {
        // Source FP64 data
        public FPNavMesh NavMesh { get; private set; }
        public FPNavMeshQuery Query { get; private set; }
        public FPNavMeshPathfinder Pathfinder { get; private set; }
        public FPNavMeshFunnel Funnel { get; private set; }

        // Visualization cache
        public Vector3[] CachedVertices;
        public TriangleRenderData[] CachedTriangles;
        public List<(Vector3 a, Vector3 b)> BoundaryEdges = new List<(Vector3, Vector3)>();
        public List<(Vector3 a, Vector3 b)> InternalEdges = new List<(Vector3, Vector3)>();

        // ORCA static-obstacle rings extracted from the NavMesh boundary (same data the runtime
        // obstacle layer uses). Flat XZ footprint drawn at ObstacleRingY.
        public struct ObstacleRingRender
        {
            public Vector3[] Points;   // ring vertices (world; Y = ObstacleRingY)
            public bool[] Convex;      // per-vertex obstacle convexity
            public bool IsCCW;         // winding: CCW = hole/pillar, CW = outer boundary
        }
        public const float ObstacleRingY = 0.1f;
        public List<ObstacleRingRender> ObstacleRings = new List<ObstacleRingRender>();

        // Path results
        public Vector3 StartPoint;
        public Vector3 EndPoint;
        public bool HasStart;
        public bool HasEnd;
        public int[] Corridor;
        public int CorridorLength;
        public Vector3[] Waypoints;
        public int WaypointCount;
        public List<(Vector3 left, Vector3 right)> Portals = new List<(Vector3, Vector3)>();
        public bool HasPath;

        public bool IsLoaded => NavMesh != null;

        #region Building placement

        // The same public probe the Unity window drives — one code path, one shape table, and the
        // gates over it cover both hosts. It went public precisely because these
        // adapters compile into the consuming project's assembly, where no friend declaration in
        // the package can reach an internal type.
        private FPNavMeshPlacementProbe _probe;

        /// <summary>Installs a rebaked mesh into the SIMULATION side; see InstallRebakedMesh.</summary>
        public System.Action<FPNavMesh> AgentSwap;

        public int PlacementCount => _probe != null ? _probe.Count : 0;

        public FPBuildingPlacementRules PlacementRules
        {
            get => _probe != null ? _probe.Rules : default;
            set { if (_probe != null) _probe.Rules = value; }
        }

        public FPBuildingPlacement PlacementAt(int index) => _probe.PlacementAt(index);

        /// <summary>Snap a placement onto the shape's tiling lattice so neighbours meet with no
        /// sliver. On by default — plain quantisation keeps a placement legal but not flush.</summary>
        public bool SnapPlacementToLattice
        {
            get => _probe == null || _probe.SnapToTilingLattice;
            set { if (_probe != null) _probe.SnapToTilingLattice = value; }
        }

        public bool TryPlaceBuilding(
            Vector3 point, int shapeId, int orientation, bool retain,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            if (_probe == null) return false;

            if (!_probe.TryPlace(shapeId, orientation,
                    FP64.FromFloat(point.X), FP64.FromFloat(point.Z), FP64.FromFloat(point.Y),
                    retain, out FPNavMesh mesh, out rejection))
                return false;

            InstallRebakedMesh(mesh);
            return true;
        }

        public bool TryRemoveBuilding(int index)
        {
            if (_probe == null || index < 0 || index >= _probe.Count) return false;
            if (!_probe.TryRemoveAt(index, out FPNavMesh mesh, out _)) return false;
            InstallRebakedMesh(mesh);
            return true;
        }

        public bool TryRebakeBuildings(out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            if (_probe == null) return false;
            if (!_probe.TryRebake(out FPNavMesh mesh, out rejection)) return false;
            InstallRebakedMesh(mesh);
            return true;
        }

        public void RevertBuildings()
        {
            if (_probe == null) return;
            InstallRebakedMesh(_probe.Revert());
        }

        /// <summary>
        /// Adopts a rebaked mesh. **The one entry point**, so nothing can update half of it: the
        /// mesh field and the render cache are what the overlay draws, the query trio is REBOUND
        /// (identity preserved — the agent system was built from these very instances), the
        /// corridor indexes the old triangle numbering and has to go, and the simulation side moves
        /// in the same call.
        /// </summary>
        private void InstallRebakedMesh(FPNavMesh mesh)
        {
            if (mesh == null) return;

            NavMesh = mesh;
            FPNavMeshPlacementProbe.RebindQueries(mesh, Query, Pathfinder, Funnel);

            BuildRenderCache();
            InvalidatePathResult();

            AgentSwap?.Invoke(mesh);
        }

        /// <summary>
        /// Drops the corridor while KEEPING the start and end markers, which <c>ClearPath</c> would
        /// also clear — placing a building between two markers the user just set is the point.
        /// </summary>
        public void InvalidatePathResult()
        {
            HasPath = false;
            Corridor = null;
            CorridorLength = 0;
            Waypoints = null;
            WaypointCount = 0;
            Portals.Clear();
        }

        #endregion

        public bool LoadFromBytes(byte[] data)
        {
            try
            {
                var reader = new SpanReader(data);
                NavMesh = FPNavMeshSerializer.Deserialize(ref reader);
                Query = new FPNavMeshQuery(NavMesh, null);
                Pathfinder = new FPNavMeshPathfinder(NavMesh, Query, null);
                Funnel = new FPNavMeshFunnel(NavMesh, Query, null);

                BuildRenderCache();
                ClearPath();
                // A new base means a new probe: the snapshot it caches is a function of the base.
                _probe = new FPNavMeshPlacementProbe(NavMesh, FPNavMeshPlacementProbe.ToolCatalog);
                return true;
            }
            catch (System.Exception e)
            {
                GD.PushError($"[GodotFPNavMeshVisualizer] Load failed: {e.Message}");
                Unload();
                return false;
            }
        }

        public void Unload()
        {
            NavMesh = null;
            Query = null;
            Pathfinder = null;
            Funnel = null;
            CachedVertices = null;
            CachedTriangles = null;
            BoundaryEdges.Clear();
            InternalEdges.Clear();
            ObstacleRings.Clear();
            ClearPath();
            _probe = null;
        }

        public void BuildRenderCache()
        {
            if (NavMesh == null) return;

            // Vertex conversion
            CachedVertices = new Vector3[NavMesh.Vertices.Length];
            for (int i = 0; i < NavMesh.Vertices.Length; i++)
                CachedVertices[i] = NavMesh.Vertices[i].ToVector3();

            // Triangle render data
            CachedTriangles = new TriangleRenderData[NavMesh.Triangles.Length];
            for (int i = 0; i < NavMesh.Triangles.Length; i++)
            {
                ref readonly FPNavMeshTriangle tri = ref NavMesh.Triangles[i];
                CachedTriangles[i] = new TriangleRenderData
                {
                    v0 = CachedVertices[tri.v0],
                    v1 = CachedVertices[tri.v1],
                    v2 = CachedVertices[tri.v2],
                    center = new Vector3(
                        tri.centerXZ.x.ToFloat(), 0f, tri.centerXZ.y.ToFloat()),
                    isBlocked = tri.isBlocked,
                    areaMask = tri.areaMask,
                    costMultiplier = tri.costMultiplier.ToFloat(),
                    index = i,
                    neighbor0 = tri.neighbor0,
                    neighbor1 = tri.neighbor1,
                    neighbor2 = tri.neighbor2,
                };

                // Correct center Y to vertex average
                CachedTriangles[i].center = new Vector3(
                    CachedTriangles[i].center.X,
                    (CachedTriangles[i].v0.Y + CachedTriangles[i].v1.Y + CachedTriangles[i].v2.Y) / 3f,
                    CachedTriangles[i].center.Z);
            }

            // Classify edges (deduplicated)
            BoundaryEdges.Clear();
            InternalEdges.Clear();
            var visitedEdges = new HashSet<long>();

            for (int t = 0; t < NavMesh.Triangles.Length; t++)
            {
                ref readonly FPNavMeshTriangle tri = ref NavMesh.Triangles[t];
                for (int e = 0; e < 3; e++)
                {
                    tri.GetEdgeVertices(e, out int va, out int vb);
                    int minV = va < vb ? va : vb;
                    int maxV = va < vb ? vb : va;
                    long key = ((long)minV << 32) | (uint)maxV;

                    if (!visitedEdges.Add(key))
                        continue;

                    Vector3 a = CachedVertices[va];
                    Vector3 b = CachedVertices[vb];

                    if (tri.GetNeighbor(e) < 0)
                        BoundaryEdges.Add((a, b));
                    else
                        InternalEdges.Add((a, b));
                }
            }

            BuildObstacleRings();
        }

        // Extracts ORCA obstacle rings from the NavMesh boundary once per cache rebuild (not per
        // frame). Reveals winding (outer CW vs hole CCW), per-vertex convexity, and any
        // coincident/duplicate rings from raised (multi-level) geometry.
        private void BuildObstacleRings()
        {
            ObstacleRings.Clear();
            if (NavMesh == null) return;

            // Obstacle extraction runs in the mesh-load path; isolate its failure so a malformed /
            // non-manifold boundary (FPNavMeshObstacleExtractor.Extract throws) only drops the
            // obstacle overlay — the triangles/edges already built by BuildRenderCache still render
            // for diagnosis, instead of the whole load aborting to a blank view.
            try
            {
                FPNavMeshObstacleExtractor.Extract(NavMesh, out var verts, out var offsets);
                if (verts.Length == 0) return;

                var avoidance = new FPNavAvoidance();
                avoidance.LoadObstacles(verts, offsets);
                var obst = avoidance.DebugObstacles;

                int ringCount = offsets.Length;
                for (int r = 0; r < ringCount; r++)
                {
                    int s = offsets[r];
                    int e = (r + 1 < ringCount) ? offsets[r + 1] : verts.Length;
                    int n = e - s;
                    if (n < 2) continue;

                    var points = new Vector3[n];
                    var convex = new bool[n];
                    double area2 = 0;
                    for (int i = s; i < e; i++)
                    {
                        var p = verts[i];
                        var q = verts[i + 1 < e ? i + 1 : s];
                        points[i - s] = new Vector3(p.x.ToFloat(), ObstacleRingY, p.y.ToFloat());
                        convex[i - s] = obst[i].isConvex;
                        area2 += p.x.ToFloat() * q.y.ToFloat() - q.x.ToFloat() * p.y.ToFloat();
                    }
                    ObstacleRings.Add(new ObstacleRingRender { Points = points, Convex = convex, IsCCW = area2 > 0 });
                }
            }
            catch (System.Exception ex)
            {
                ObstacleRings.Clear();
                GD.PushWarning($"[GodotFPNavMeshVisualizer] Obstacle ring extraction failed (mesh still shown): {ex.Message}");
            }
        }

        /// <summary>
        /// Finds a path under <paramref name="areaMask"/>. Required argument with no default,
        /// following <c>FPNavMeshQuery.MoveAlongSurface</c>: it used to be hardcoded to <c>~0</c>,
        /// which was the same thing as the agents' mask until a retained building footprint could
        /// exist. Now the two disagree on the same mesh, and a caller that forgets should not get
        /// the permissive half by accident.
        /// </summary>
        public bool FindPath(Vector3 start, Vector3 end, int areaMask)
        {
            if (NavMesh == null || Pathfinder == null) return false;

            FPVector3 startFP = start.ToFPVector3();
            FPVector3 endFP = end.ToFPVector3();

            bool found = Pathfinder.FindPath(startFP, endFP, areaMask,
                out int[] corridor, out int corridorLength);

            if (!found)
            {
                HasPath = false;
                return false;
            }

            // Copy corridor (reference to Pathfinder's internal buffer)
            Corridor = new int[corridorLength];
            System.Array.Copy(corridor, Corridor, corridorLength);
            CorridorLength = corridorLength;

            // Funnel
            Funnel.Funnel(corridor, corridorLength, startFP, endFP,
                out FPVector3[] waypoints, out int waypointCount);

            Waypoints = new Vector3[waypointCount];
            for (int i = 0; i < waypointCount; i++)
                Waypoints[i] = waypoints[i].ToVector3();
            WaypointCount = waypointCount;

            // Extract portals
            ExtractPortals(Corridor, CorridorLength);

            HasPath = true;
            return true;
        }

        public void ClearPath()
        {
            HasStart = false;
            HasEnd = false;
            HasPath = false;
            Corridor = null;
            CorridorLength = 0;
            Waypoints = null;
            WaypointCount = 0;
            Portals.Clear();
        }

        public (int col, int row) GetGridCell(Vector3 worldPos)
        {
            if (NavMesh == null) return (-1, -1);
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.X), FP64.FromFloat(worldPos.Z));
            NavMesh.GetCellCoords(xz, out int col, out int row);
            return (col, row);
        }

        public int FindTriangleAtPosition(Vector3 worldPos)
        {
            if (Query == null) return -1;
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.X), FP64.FromFloat(worldPos.Z));
            FP64 y = FP64.FromFloat(worldPos.Y);
            return Query.FindTriangle(xz, y);
        }

        public bool RaycastNavMesh(Vector3 origin, Vector3 direction, out Vector3 hitPoint, out int triIdx)
        {
            hitPoint = Vector3.Zero;
            triIdx = -1;

            if (Query == null) return false;

            FPVector3 fpOrigin = origin.ToFPVector3();
            FPVector3 fpDir = direction.ToFPVector3();

            bool result = Query.Raycast(fpOrigin, fpDir, out FPVector3 fpHit, out triIdx);
            if (!result)
                return false;

            hitPoint = fpHit.ToVector3();
            return true;
        }

        public float SampleHeightAt(Vector3 worldPos, int triIdx)
        {
            if (Query == null || triIdx < 0) return worldPos.Y;
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.X), FP64.FromFloat(worldPos.Z));
            return Query.SampleHeight(xz, triIdx).ToFloat();
        }

        private void ExtractPortals(int[] corridor, int corridorLength)
        {
            Portals.Clear();
            for (int i = 0; i < corridorLength - 1; i++)
            {
                int curTri = corridor[i];
                int nextTri = corridor[i + 1];
                ref readonly FPNavMeshTriangle tri = ref NavMesh.Triangles[curTri];

                for (int e = 0; e < 3; e++)
                {
                    if (tri.GetNeighbor(e) == nextTri)
                    {
                        tri.GetPortal(e, out int leftIdx, out int rightIdx);
                        Vector3 left = CachedVertices[leftIdx];
                        Vector3 right = CachedVertices[rightIdx];
                        Portals.Add((left, right));
                        break;
                    }
                }
            }
        }
    }
}
#endif
