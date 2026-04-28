using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;
using ZLogger.Unity;
using ZLogger.Providers;
using Utf8StringInterpolation;

using System.Collections.Generic;

using UnityEngine;

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

        public ILogger Logger { get; private set; }

        // Visualization cache
        public Vector3[] CachedVertices;
        public TriangleRenderData[] CachedTriangles;
        public List<(Vector3 a, Vector3 b)> BoundaryEdges = new List<(Vector3, Vector3)>();
        public List<(Vector3 a, Vector3 b)> InternalEdges = new List<(Vector3, Vector3)>();

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

        public void LoadFromNavMesh(FPNavMesh navMesh, FPNavMeshQuery query)
        {
            NavMesh = navMesh;
            Query = query;
            Pathfinder = null;
            Funnel = null;
            BuildRenderCache();
            ClearPath();
        }

        private void CreateLogger()
        {
            if (Logger != null) return;

            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();

                // Output to yyyy-MM-dd_*.log, rolling on date change or exceeding 1 MB
                logging.AddZLoggerRollingFile(options =>
                {
                    options.FilePathSelector = (dt, index) => $"Logs/FPNavMeshVisualizerWindow_{dt:yyyy-MM-dd-HH-mm-ss}_{index:000}.log";
                    options.RollingInterval = RollingInterval.Day;
                    options.RollingSizeKB = 1024 * 1024;
                    options.UsePlainTextFormatter(formatter =>
                    {
                        formatter.SetPrefixFormatter($"{0}|{1:short}|", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel));
                        formatter.SetExceptionFormatter((writer, ex) => Utf8String.Format(writer, $"{ex.Message}"));
                    });
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
            ClearPath();
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
                ref FPNavMeshTriangle tri = ref NavMesh.Triangles[i];
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
                ref FPNavMeshTriangle tri = ref NavMesh.Triangles[t];
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
        }

        public bool FindPath(Vector3 start, Vector3 end)
        {
            if (NavMesh == null) return false;

            FPVector3 startFP = start.ToFPVector3();
            FPVector3 endFP = end.ToFPVector3();

            bool found = Pathfinder.FindPath(startFP, endFP, ~0,
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
                ref FPNavMeshTriangle tri = ref NavMesh.Triangles[curTri];

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
