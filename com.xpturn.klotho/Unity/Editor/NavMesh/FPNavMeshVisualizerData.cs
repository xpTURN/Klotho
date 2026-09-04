using System.Collections.Generic;

using UnityEngine;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Triangle rendering data. Contains the three vertex positions and color.
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
    internal class FPNavMeshVisualizerData
    {
        // Source FP64 data
        public FPNavMesh NavMesh { get; private set; }
        public FPNavMeshQuery Query { get; private set; }
        public FPNavMeshPathfinder Pathfinder { get; private set; }
        public FPNavMeshFunnel Funnel { get; private set; }

        private bool _enableLogs;
        public bool EnableLogs
        {
            get => _enableLogs;

            set
            {
                _enableLogs = value;
                if (!_enableLogs) Logger = null;
            }
        }

        public IKLogger Logger { get; private set; }

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

        /// <summary>
        /// Base mesh + placement list, rebaked from the base every time. Null until a mesh is
        /// loaded; rebuilt whenever the base changes, because the snapshot it caches is a function
        /// of the base alone.
        /// </summary>
        private FPNavMeshPlacementProbe _probe;

        /// <summary>
        /// Installs a rebaked mesh into the SIMULATION side. Set once at wiring; it exists so that
        /// <see cref="InstallRebakedMesh"/> is the only way to adopt a rebake and cannot be done
        /// halfway — the mesh, the render cache and the agents move together or not at all.
        /// </summary>
        public System.Action<FPNavMesh> AgentSwap;

        public int PlacementCount => _probe != null ? _probe.Count : 0;

        public FPBuildingPlacementRules PlacementRules
        {
            get => _probe != null ? _probe.Rules : default;
            set { if (_probe != null) _probe.Rules = value; }
        }

        public FPBuildingPlacement PlacementAt(int index) => _probe.PlacementAt(index);

        /// <summary>
        /// Snap a placement onto the shape's tiling lattice so neighbours meet with no sliver.
        /// On by default; see <c>FPNavMeshPlacementProbe.SnapToTilingLattice</c> for why plain
        /// quantisation is not enough.
        /// </summary>
        public bool SnapPlacementToLattice
        {
            get => _probe == null || _probe.SnapToTilingLattice;
            set { if (_probe != null) _probe.SnapToTilingLattice = value; }
        }

        /// <summary>
        /// Places one building at <paramref name="point"/> and adopts the rebaked mesh. The centre
        /// is quantised by the probe, so a click never produces the malformed-request throw.
        /// </summary>
        public bool TryPlaceBuilding(
            Vector3 point, int shapeId, int orientation, bool retain,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            if (_probe == null) return false;

            if (!_probe.TryPlace(shapeId, orientation,
                    FP64.FromFloat(point.x), FP64.FromFloat(point.z), FP64.FromFloat(point.y),
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

        /// <summary>Rebakes the current list — for a rules change.</summary>
        public bool TryRebakeBuildings(out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            if (_probe == null) return false;
            if (!_probe.TryRebake(out FPNavMesh mesh, out rejection)) return false;
            InstallRebakedMesh(mesh);
            return true;
        }

        /// <summary>Drops every placement and goes back to the loaded mesh.</summary>
        public void RevertBuildings()
        {
            if (_probe == null) return;
            InstallRebakedMesh(_probe.Revert());
        }

        /// <summary>
        /// Adopts a rebaked mesh. **The one entry point**, so that nothing can update half of it.
        ///
        /// <para>Three things go stale on a rebake and all three are handled here. The mesh field
        /// and the render cache are what the overlay draws — miss them and the window draws the old
        /// mesh while the path tool answers on the new one, a contradiction inside one window whose
        /// wrong half is the half the user trusts. The query/pathfinder/funnel are REBOUND rather
        /// than replaced, keeping their identity: the agent system was constructed from these very
        /// instances, so rebinding is what keeps the two sides one truth. And the corridor indexes
        /// the old triangle numbering, so it has to go.</para>
        ///
        /// <para>Deliberately NOT <c>LoadFromNavMesh</c>: that one nulls the pathfinder and funnel
        /// (only the play-mode bridge calls it, and that mode hides the path tool, which is why the
        /// gap has never shown) and it clears the start/end MARKERS as well as the path — and
        /// placing a building between two markers the user just set is the whole point of the tool.</para>
        /// </summary>
        private void InstallRebakedMesh(FPNavMesh mesh)
        {
            if (mesh == null) return;

            NavMesh = mesh;
            FPNavMeshPlacementProbe.RebindQueries(mesh, Query, Pathfinder, Funnel);

            BuildRenderCache();
            InvalidatePathResult();

            // Simulation side, in the same call — see AgentSwap.
            AgentSwap?.Invoke(mesh);
        }

        /// <summary>
        /// Drops the corridor while KEEPING the start and end markers, which
        /// <see cref="ClearPath"/> would also clear. The corridor holds triangle indices from the
        /// mesh that was just replaced; the markers are world positions and stay meaningful.
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

        public void LoadFromNavMesh(FPNavMesh navMesh, FPNavMeshQuery query)
        {
            NavMesh = navMesh;
            Query = query;
            Pathfinder = null;
            Funnel = null;
            BuildRenderCache();
            ClearPath();
            // The play-mode bridge is the only caller, and the game owns the placements there —
            // an edit-mode placement list would be a second, competing account of what is built.
            _probe = null;
        }

        private void CreateLogger()
        {
            if (Logger != null) return;

            // Output to yyyy-MM-dd_*.log, rolling on date change or exceeding 1 MB
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
                logging.AddRollingFile(options =>
                {
                    options.FilePrefix = "FPNavMeshVisualizerWindow";
                    options.RollingSizeKB = 1024 * 1024;
                });
            });

            Logger = loggerFactory.CreateLogger("FPNavMeshVisualizer");
        }

        public bool LoadFromBytes(byte[] data)
        {
            if (EnableLogs)
                CreateLogger();

            try
            {
                
                var reader = new SpanReader(data);
                NavMesh = FPNavMeshSerializer.Deserialize(ref reader);
                Query = new FPNavMeshQuery(NavMesh, Logger);
                Pathfinder = new FPNavMeshPathfinder(NavMesh, Query, Logger);
                Funnel = new FPNavMeshFunnel(NavMesh, Query, Logger);

                BuildRenderCache();
                ClearPath();
                // A new base means a new probe: the snapshot it caches is a function of the base,
                // and the old placement list describes a mesh that is no longer loaded.
                _probe = new FPNavMeshPlacementProbe(NavMesh, FPNavMeshPlacementProbe.ToolCatalog, Logger);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FPNavMeshVisualizer] Load failed: {e.Message}");
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
                    CachedTriangles[i].center.x,
                    (CachedTriangles[i].v0.y + CachedTriangles[i].v1.y + CachedTriangles[i].v2.y) / 3f,
                    CachedTriangles[i].center.z);
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
        // scene repaint). Reveals winding (outer CW vs hole CCW), per-vertex convexity, and any
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
                Debug.LogWarning($"[FPNavMeshVisualizer] Obstacle ring extraction failed (mesh still shown): {ex.Message}");
            }
        }

        /// <summary>
        /// Finds a path under <paramref name="areaMask"/>.
        ///
        /// <para>The mask is a required argument with no default, following
        /// <c>FPNavMeshQuery.MoveAlongSurface</c>: it used to be hardcoded to <c>~0</c> here, which
        /// was harmless while nothing was filtered and is not any more — a retained building
        /// footprint is walkable ground the agents' default mask refuses, so <c>~0</c> and
        /// <c>FPNavAgentSystem.DEFAULT_AREA_MASK</c> now give different answers on the same mesh. A
        /// caller that forgets to pass one should not get the permissive half by accident.</para>
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
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.x), FP64.FromFloat(worldPos.z));
            NavMesh.GetCellCoords(xz, out int col, out int row);
            return (col, row);
        }

        public int FindTriangleAtPosition(Vector3 worldPos)
        {
            if (Query == null) return -1;
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.x), FP64.FromFloat(worldPos.z));
            FP64 y = FP64.FromFloat(worldPos.y);
            return Query.FindTriangle(xz, y);
        }

        public bool RaycastNavMesh(Ray ray, out Vector3 hitPoint, out int triIdx, bool enableLog = false)
        {
            hitPoint = Vector3.zero;
            triIdx = -1;

            if (Query == null) return false;

            FPVector3 origin = ray.origin.ToFPVector3();
            FPVector3 direction = ray.direction.ToFPVector3();

            bool result = Query.Raycast(origin, direction, out FPVector3 fpHit, out triIdx);
            if (!result)
                return false;

            hitPoint = fpHit.ToVector3();
            return true;
        }

        public float SampleHeightAt(Vector3 worldPos, int triIdx)
        {
            if (Query == null || triIdx < 0) return worldPos.y;
            FPVector2 xz = new FPVector2(FP64.FromFloat(worldPos.x), FP64.FromFloat(worldPos.z));
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
