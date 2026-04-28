using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// NavMesh query functions.
    /// Holds an FPNavMesh reference and provides triangle lookup, height sampling, closest point, etc.
    /// Math primitives are kept as static methods.
    /// </summary>
    public class FPNavMeshQuery
    {
        private readonly FPNavMesh _navMesh;
        private readonly ILogger _logger;

        // Generation counter to avoid duplicate triangle checks
        private readonly int[] _triVisited;
        private int _queryGeneration;

        private readonly int[] _raycastVisited;
        private int _raycastGeneration;

        // Pre-allocations for MoveAlongSurface
        private const int MOVE_MAX_QUEUE = 48;
        private readonly int[] _moveQueue;
        private readonly int[] _moveVisitedGen;
        private int _moveGeneration;
        private readonly int[] _moveParent;
        private readonly int[] _moveVisitedPath;

        public FPNavMeshQuery(FPNavMesh navMesh, ILogger logger)
        {
            _navMesh = navMesh;
            _logger = logger;

            int triCount = navMesh.Triangles.Length;
            _triVisited = new int[triCount];
            _raycastVisited = new int[triCount];
            _moveQueue = new int[MOVE_MAX_QUEUE];
            _moveVisitedGen = new int[triCount];
            _moveParent = new int[triCount];
            _moveVisitedPath = new int[MOVE_MAX_QUEUE];
        }

        #region NavMesh query functions

        /// <summary>
        /// Returns the triangle index containing the point (-1 = outside the NavMesh).
        /// O(1) spatial grid cell lookup -> PointInTriangle2D against candidate triangles.
        /// </summary>
        public int FindTriangle(FPVector2 xz)
        {
            if (!_navMesh.BoundsXZ.Contains(xz))
                return -1;

            _navMesh.GetCellCoords(xz, out int col, out int row);
            if (!_navMesh.IsCellValid(col, row))
                return -1;

            _navMesh.GetCellTriangles(col, row, out int start, out int count);

            for (int i = 0; i < count; i++)
            {
                int triIdx = _navMesh.GridTriangles[start + i];
                ref FPNavMeshTriangle tri = ref _navMesh.Triangles[triIdx];

                FPVector2 a = _navMesh.Vertices[tri.v0].ToXZ();
                FPVector2 b = _navMesh.Vertices[tri.v1].ToXZ();
                FPVector2 c = _navMesh.Vertices[tri.v2].ToXZ();

                if (PointInTriangle2D(xz, a, b, c))
                    return triIdx;
            }

            return -1;
        }

        /// <summary>
        /// Triangle lookup that considers Y height (multi-floor support).
        /// When multiple triangles overlap in the same XZ region, returns the triangle nearest to agentY.
        /// </summary>
        public int FindTriangle(FPVector2 xz, FP64 agentY)
        {
            if (!_navMesh.BoundsXZ.Contains(xz))
                return -1;

            _navMesh.GetCellCoords(xz, out int col, out int row);
            if (!_navMesh.IsCellValid(col, row))
                return -1;

            _navMesh.GetCellTriangles(col, row, out int start, out int count);

            int bestTriIdx = -1;
            FP64 bestDist = FP64.MaxValue;

            for (int i = 0; i < count; i++)
            {
                int triIdx = _navMesh.GridTriangles[start + i];
                ref FPNavMeshTriangle tri = ref _navMesh.Triangles[triIdx];

                FPVector2 a = _navMesh.Vertices[tri.v0].ToXZ();
                FPVector2 b = _navMesh.Vertices[tri.v1].ToXZ();
                FPVector2 c = _navMesh.Vertices[tri.v2].ToXZ();

                if (!PointInTriangle2D(xz, a, b, c))
                    continue;

                FP64 surfaceY = SampleHeight(xz, triIdx);
                FP64 dist = FP64.Abs(surfaceY - agentY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestTriIdx = triIdx;
                }
            }

            return bestTriIdx;
        }

        /// <summary>
        /// Returns the Y height via barycentric interpolation.
        /// triIdx must be valid.
        /// Clamps to prevent extreme values caused by barycentric error on thin triangles.
        /// </summary>
        public FP64 SampleHeight(FPVector2 xz, int triIdx)
        {
            ref FPNavMeshTriangle tri = ref _navMesh.Triangles[triIdx];

            FPVector3 va = _navMesh.Vertices[tri.v0];
            FPVector3 vb = _navMesh.Vertices[tri.v1];
            FPVector3 vc = _navMesh.Vertices[tri.v2];

            BarycentricCoords2D(xz, va.ToXZ(), vb.ToXZ(), vc.ToXZ(),
                out FP64 u, out FP64 v, out FP64 w);

            FP64 height = va.y * u + vb.y * v + vc.y * w;

            FP64 minY = FP64.Min(FP64.Min(va.y, vb.y), vc.y);
            FP64 maxY = FP64.Max(FP64.Max(va.y, vb.y), vc.y);
            return FP64.Clamp(height, minY, maxY);
        }

        /// <summary>
        /// Move along the NavMesh surface (recast moveAlongSurface approach).
        /// Performs BFS (FIFO) from startTri across adjacent polygons and returns the
        /// reachable point closest to endPos. On a multi-floor NavMesh, neighboring
        /// triangles whose Y differs beyond the threshold are treated as walls.
        /// </summary>
        /// <returns>Constrained position (with height correction) and the corresponding triangle index</returns>
        public (FPVector3 resultPos, int resultTri) MoveAlongSurface(
            FPVector3 startPos, FPVector3 endPos, int startTri, FP64 multiFloorYThreshold)
        {
            return MoveAlongSurfaceWithVisited(startPos, endPos, startTri,
                multiFloorYThreshold, null, out _);
        }

        public (FPVector3 resultPos, int resultTri) MoveAlongSurfaceWithVisited(
            FPVector3 startPos, FPVector3 endPos, int startTri, FP64 multiFloorYThreshold,
            int[] outVisited, out int visitedCount)
        {
            visitedCount = 0;

            if (startTri < 0)
            {
                _logger?.ZLogDebug($"[MoveAlongSurface] startTri<0, skip. startPos={startPos}");
                return (startPos, startTri);
            }

            FPVector2 startXZ = startPos.ToXZ();
            FPVector2 endXZ = endPos.ToXZ();
            FP64 startCenterY = _navMesh.Triangles[startTri].centerY;

            _logger?.ZLogDebug($"[MoveAlongSurface] start={startPos} end={endPos} startTri={startTri} startCenterY={startCenterY}");

            // BFS initialization
            FPVector2 bestPos = startXZ;
            FP64 bestDist = FP64.MaxValue;
            int bestTri = startTri;
            bool reachedEnd = false;

            // Update generation counter
            _moveGeneration++;
            if (_moveGeneration == int.MaxValue)
            {
                Array.Clear(_moveVisitedGen, 0, _moveVisitedGen.Length);
                _moveGeneration = 1;
            }

            int queueHead = 0, queueTail = 0;
            _moveQueue[queueTail++] = startTri;
            _moveVisitedGen[startTri] = _moveGeneration;
            _moveParent[startTri] = -1;

            while (queueHead < queueTail)
            {
                int curTri = _moveQueue[queueHead++];
                ref FPNavMeshTriangle tri = ref _navMesh.Triangles[curTri];

                // Terminate immediately if endPos is inside the current triangle
                FPVector2 a = _navMesh.Vertices[tri.v0].ToXZ();
                FPVector2 b = _navMesh.Vertices[tri.v1].ToXZ();
                FPVector2 c = _navMesh.Vertices[tri.v2].ToXZ();

                if (PointInTriangle2D(endXZ, a, b, c))
                {
                    bestPos = endXZ;
                    bestTri = curTri;
                    reachedEnd = true;
                    _logger?.ZLogDebug($"[MoveAlongSurface] endPos inside tri={curTri}, reached destination");
                    break;
                }

                // Iterate the 3 edges
                for (int e = 0; e < 3; e++)
                {
                    tri.GetEdgeVertices(e, out int va, out int vb);
                    FPVector2 ea = _navMesh.Vertices[va].ToXZ();
                    FPVector2 eb = _navMesh.Vertices[vb].ToXZ();
                    int neighbor = tri.GetNeighbor(e);

                    if (neighbor >= 0
                        && !_navMesh.Triangles[neighbor].isBlocked
                        && _moveVisitedGen[neighbor] != _moveGeneration)
                    {
                        // Multi-floor validation: based on the starting triangle
                        FP64 yStep = FP64.Abs(
                            _navMesh.Triangles[neighbor].centerY - startCenterY);
                        if (yStep <= multiFloorYThreshold)
                        {
                            // Passable -> expand the queue (search range bounded by MAX_QUEUE)
                            _moveVisitedGen[neighbor] = _moveGeneration;
                            _moveParent[neighbor] = curTri;
                            if (queueTail < MOVE_MAX_QUEUE)
                                _moveQueue[queueTail++] = neighbor;
                            continue;
                        }
                        else
                        {
                            _logger?.ZLogDebug($"[MoveAlongSurface] tri={curTri} e={e} neighbor={neighbor} yStep={yStep} > threshold={multiFloorYThreshold} → wall");
                        }
                    }

                    // Wall edge (boundary / different floor / blocked / visited) → closest point to endPos
                    FPVector2 cp = ClosestPointOnSegment2D(endXZ, ea, eb);
                    FP64 dist = FPVector2.SqrDistance(endXZ, cp);
                    if (dist < bestDist)
                    {
                        bestPos = cp;
                        bestDist = dist;
                        bestTri = curTri;
                    }
                }
            }

            // Trace back parent chain from bestTri → visited path (startTri→bestTri order)
            int pathLen = 0;
            int node = bestTri;
            while (node != -1 && pathLen < MOVE_MAX_QUEUE
                   && _moveVisitedGen[node] == _moveGeneration)
            {
                _moveVisitedPath[pathLen++] = node;
                node = _moveParent[node];
            }
            Array.Reverse(_moveVisitedPath, 0, pathLen);
            visitedCount = pathLen;
            if (outVisited != null)
            {
                int copyCount = pathLen < outVisited.Length ? pathLen : outVisited.Length;
                Array.Copy(_moveVisitedPath, 0, outVisited, 0, copyCount);
                visitedCount = copyCount;
            }

            // Height correction
            FP64 height = SampleHeight(bestPos, bestTri);
            FPVector3 result = new FPVector3(bestPos.x, height, bestPos.y);
            _logger?.ZLogDebug($"[MoveAlongSurface] result={result} resultTri={bestTri} reached={reachedEnd} visited={visitedCount} bestDist={bestDist}");
            return (result, bestTri);
        }

        /// <summary>
        /// Nearest point on the NavMesh + triangle index.
        /// Returns the point as-is if FindTriangle succeeds.
        /// Falls back to searching edges/vertices in surrounding cells on failure.
        /// </summary>
        public FPVector2 ClosestPointOnNavMesh(FPVector2 xz, out int triIdx)
        {
            triIdx = FindTriangle(xz);
            if (triIdx >= 0)
                return xz;

            // Outside NavMesh → search surrounding cells for nearest edge/vertex
            _navMesh.GetCellCoords(xz, out int centerCol, out int centerRow);
            _queryGeneration++;

            FP64 bestSqrDist = FP64.MaxValue;
            FPVector2 bestPoint = xz;
            int bestTriIdx = -1;

            // Search center cell + 8 adjacent cells
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    int col = centerCol + dc;
                    int row = centerRow + dr;
                    if (!_navMesh.IsCellValid(col, row))
                        continue;

                    _navMesh.GetCellTriangles(col, row, out int start, out int count);

                    for (int i = 0; i < count; i++)
                    {
                        int tIdx = _navMesh.GridTriangles[start + i];

                        // Prevent duplicate checks for triangles shared between adjacent cells
                        if (_triVisited[tIdx] == _queryGeneration)
                            continue;
                        _triVisited[tIdx] = _queryGeneration;

                        ref FPNavMeshTriangle tri = ref _navMesh.Triangles[tIdx];

                        FPVector2 a = _navMesh.Vertices[tri.v0].ToXZ();
                        FPVector2 b = _navMesh.Vertices[tri.v1].ToXZ();
                        FPVector2 c = _navMesh.Vertices[tri.v2].ToXZ();

                        // Return immediately if point is inside triangle
                        if (PointInTriangle2D(xz, a, b, c))
                        {
                            triIdx = tIdx;
                            return xz;
                        }

                        // Check closest point on all 3 edges
                        CheckEdgeClosest(xz, a, b, tIdx, ref bestSqrDist, ref bestPoint, ref bestTriIdx);
                        CheckEdgeClosest(xz, b, c, tIdx, ref bestSqrDist, ref bestPoint, ref bestTriIdx);
                        CheckEdgeClosest(xz, c, a, tIdx, ref bestSqrDist, ref bestPoint, ref bestTriIdx);
                    }
                }
            }

            triIdx = bestTriIdx;
            return bestPoint;
        }

        /// <summary>
        /// Snaps to the NavMesh within maxDist.
        /// Returns the snapped XZ coordinate with triIdx set on success.
        /// Returns (-1, original coordinate) on failure.
        /// </summary>
        public FPVector2 ProjectToNavMesh(FPVector2 xz, FP64 maxDist, out int triIdx)
        {
            FPVector2 closest = ClosestPointOnNavMesh(xz, out triIdx);

            if (triIdx < 0)
                return xz;

            FP64 sqrDist = FPVector2.SqrDistance(xz, closest);
            if (sqrDist > maxDist * maxDist)
            {
                triIdx = -1;
                return xz;
            }

            return closest;
        }

        /// <summary>
        /// Ray intersection test against NavMesh triangles (DDA grid acceleration).
        /// Returns the closest triangle from origin and its intersection point.
        /// </summary>
        public bool Raycast(FPVector3 origin, FPVector3 direction,
            out FPVector3 hitPoint, out int triIdx)
        {
            hitPoint = FPVector3.Zero;
            triIdx = -1;

            _raycastGeneration++;

            FP64 cellSize    = _navMesh.GridCellSize;
            FP64 gridOriginX = _navMesh.GridOrigin.x;
            FP64 gridOriginZ = _navMesh.GridOrigin.y;
            FP64 gridMaxX    = gridOriginX + FP64.FromInt(_navMesh.GridWidth)  * cellSize;
            FP64 gridMaxZ    = gridOriginZ + FP64.FromInt(_navMesh.GridHeight) * cellSize;

            FPVector2 originXZ = origin.ToXZ();
            FPVector2 dirXZ    = direction.ToXZ();

            // _logger?.ZLogTrace(
            //     $"[Raycast] origin={origin} dir={direction}" +
            //     $" originXZ={originXZ} dirXZ={dirXZ}" +
            //     $" grid=[({gridOriginX},{gridOriginZ})~({gridMaxX},{gridMaxZ})]" +
            //     $" {_navMesh.GridWidth}x{_navMesh.GridHeight} cell={cellSize}");

            FP64 tStart = FP64.Zero;
            if (!IsInsideGrid(originXZ, gridOriginX, gridOriginZ, gridMaxX, gridMaxZ))
            {
                if (!RayAABB2D(originXZ, dirXZ,
                        gridOriginX, gridOriginZ, gridMaxX, gridMaxZ, out tStart))
                {
                    // _logger?.ZLogTrace($"[Raycast] RayAABB2D miss — ray does not intersect grid XZ bounds");
                    return false;
                }
                // _logger?.ZLogTrace($"[Raycast] origin outside grid. tStart={tStart}");
            }
            else
            {
                // _logger?.ZLogTrace($"[Raycast] origin inside grid. tStart=0");
            }

            FPVector2 startXZ = originXZ + dirXZ * tStart;

            _navMesh.GetCellCoords(startXZ, out int col, out int row);
            int colRaw = col, rowRaw = row;
            if (col < 0) col = 0;
            if (col >= _navMesh.GridWidth)  col = _navMesh.GridWidth  - 1;
            if (row < 0) row = 0;
            if (row >= _navMesh.GridHeight) row = _navMesh.GridHeight - 1;

            // _logger?.ZLogTrace(
            //     $"[Raycast] startXZ={startXZ} col={colRaw}->{col} row={rowRaw}->{row}");

            int stepCol = dirXZ.x >= FP64.Zero ? 1 : -1;
            int stepRow = dirXZ.y >= FP64.Zero ? 1 : -1;

            FP64 eps = FP64.FromDouble(1e-6);
            FP64 tDeltaX = FP64.Abs(dirXZ.x) > eps ? cellSize / FP64.Abs(dirXZ.x) : FP64.MaxValue;
            FP64 tDeltaZ = FP64.Abs(dirXZ.y) > eps ? cellSize / FP64.Abs(dirXZ.y) : FP64.MaxValue;

            FP64 boundX = gridOriginX + FP64.FromInt(col + (stepCol > 0 ? 1 : 0)) * cellSize;
            FP64 boundZ = gridOriginZ + FP64.FromInt(row + (stepRow > 0 ? 1 : 0)) * cellSize;

            FP64 tMaxX = FP64.Abs(dirXZ.x) > eps ? (boundX - startXZ.x) / dirXZ.x : FP64.MaxValue;
            FP64 tMaxZ = FP64.Abs(dirXZ.y) > eps ? (boundZ - startXZ.y) / dirXZ.y : FP64.MaxValue;

            // _logger?.ZLogTrace(
            //     $"[Raycast] step=({stepCol},{stepRow}) tDelta=({tDeltaX},{tDeltaZ}) tMax=({tMaxX},{tMaxZ})");

            FP64 bestDist = FP64.MaxValue;
            int bestTri = -1;

            while (_navMesh.IsCellValid(col, row))
            {
                _navMesh.GetCellTriangles(col, row, out int start, out int count);

                FP64 tExit = FP64.Min(tMaxX, tMaxZ);

                // _logger?.ZLogTrace(
                //     $"[Raycast] cell=({col},{row}) triCount={count} tExit={tExit}");

                for (int i = 0; i < count; i++)
                {
                    int tIdx = _navMesh.GridTriangles[start + i];
                    if (_raycastVisited[tIdx] == _raycastGeneration)
                    {
                        // _logger?.ZLogTrace($"[Raycast]   tri[{tIdx}] skip (visited)");
                        continue;
                    }
                    _raycastVisited[tIdx] = _raycastGeneration;

                    ref FPNavMeshTriangle tri = ref _navMesh.Triangles[tIdx];
                    if (tri.isBlocked)
                    {
                        // _logger?.ZLogTrace($"[Raycast]   tri[{tIdx}] skip (blocked)");
                        continue;
                    }

                    FPVector3 a = _navMesh.Vertices[tri.v0];
                    FPVector3 b = _navMesh.Vertices[tri.v1];
                    FPVector3 c = _navMesh.Vertices[tri.v2];

                    bool intersect = RayTriangleIntersect(origin, direction, a, b, c, out FP64 dist);
                    // _logger?.ZLogTrace(
                    //     $"[Raycast]   tri[{tIdx}] intersect={intersect} dist={dist}" +
                    //     $" v=({a}|{b}|{c})");

                    if (intersect && dist < bestDist)
                    {
                        bestDist = dist;
                        bestTri = tIdx;
                    }
                }

                if (bestTri >= 0)
                {
                    FP64 cmp = bestDist - tStart;
                    bool confirm = cmp <= tExit + FP64.FromDouble(1e-4);
                    // _logger?.ZLogTrace(
                    //     $"[Raycast]   best tri[{bestTri}] dist={bestDist}" +
                    //     $" cmp(dist-tStart)={cmp} tExit={tExit} confirm={confirm}");

                    if (confirm)
                    {
                        hitPoint = origin + direction * bestDist;
                        triIdx = bestTri;
                        // _logger?.ZLogTrace($"[Raycast] HIT tri={triIdx} hitPoint={hitPoint}");
                        return true;
                    }
                }
                else
                {
                    // _logger?.ZLogDebug($"[Raycast]   no hit in cell ({col},{row})");
                }

                if (tMaxX < tMaxZ) { col += stepCol; tMaxX += tDeltaX; }
                else               { row += stepRow; tMaxZ += tDeltaZ; }
            }

            // _logger?.ZLogDebug($"[Raycast] MISS — grid exhausted");
            return false;
        }

        #endregion

        #region Math primitives (static)

        private static readonly FP64 PIT_EPSILON = FP64.FromDouble(0.0001);

        /// <summary>
        /// Point-in-triangle test (2D XZ).
        /// Uses cross product sign — point is inside if all three edges have the same orientation.
        /// Epsilon applied to tolerate floating-point error for points on thin triangle edges.
        /// </summary>
        public static bool PointInTriangle2D(FPVector2 p, FPVector2 a, FPVector2 b, FPVector2 c)
        {
            FP64 d1 = FPVector2.Cross(b - a, p - a);
            FP64 d2 = FPVector2.Cross(c - b, p - b);
            FP64 d3 = FPVector2.Cross(a - c, p - c);

            bool hasNeg = (d1 < -PIT_EPSILON) || (d2 < -PIT_EPSILON) || (d3 < -PIT_EPSILON);
            bool hasPos = (d1 > PIT_EPSILON) || (d2 > PIT_EPSILON) || (d3 > PIT_EPSILON);

            return !(hasNeg && hasPos);
        }

        private static readonly FP64 BARY_DENOM_EPSILON = FP64.FromDouble(0.0001);

        /// <summary>
        /// Barycentric coordinates (2D XZ) — used for height interpolation.
        /// Falls back to equal weights for degenerate triangles (denominator ≈ 0).
        /// </summary>
        public static void BarycentricCoords2D(FPVector2 p, FPVector2 a, FPVector2 b, FPVector2 c,
            out FP64 u, out FP64 v, out FP64 w)
        {
            FPVector2 v0 = b - a;
            FPVector2 v1 = c - a;
            FPVector2 v2 = p - a;

            FP64 d00 = FPVector2.Dot(v0, v0);
            FP64 d01 = FPVector2.Dot(v0, v1);
            FP64 d11 = FPVector2.Dot(v1, v1);
            FP64 d20 = FPVector2.Dot(v2, v0);
            FP64 d21 = FPVector2.Dot(v2, v1);

            FP64 denom = d00 * d11 - d01 * d01;

            if (FP64.Abs(denom) < BARY_DENOM_EPSILON)
            {
                u = v = w = FP64.One / FP64.FromInt(3);
                return;
            }

            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = FP64.One - v - w;
        }

        /// <summary>
        /// Closest point on a 2D segment.
        /// </summary>
        public static FPVector2 ClosestPointOnSegment2D(FPVector2 p, FPVector2 a, FPVector2 b)
        {
            FPVector2 ab = b - a;
            FP64 sqrLen = ab.sqrMagnitude;

            if (sqrLen == FP64.Zero)
                return a;

            FP64 t = FPVector2.Dot(p - a, ab) / sqrLen;
            t = FP64.Clamp01(t);

            return a + ab * t;
        }

        /// <summary>
        /// Signed area (orientation test).
        /// Positive for CCW winding, negative for CW.
        /// </summary>
        public static FP64 TriangleArea2D(FPVector2 a, FPVector2 b, FPVector2 c)
        {
            return FPVector2.Cross(b - a, c - a) * FP64.Half;
        }

        #endregion

        #region Private

        private static bool IsInsideGrid(FPVector2 p,
            FP64 minX, FP64 minZ, FP64 maxX, FP64 maxZ)
        {
            return p.x >= minX && p.x <= maxX && p.y >= minZ && p.y <= maxZ;
        }

        private static bool RayAABB2D(FPVector2 origin, FPVector2 dir,
            FP64 minX, FP64 minZ, FP64 maxX, FP64 maxZ, out FP64 tEnter)
        {
            tEnter = FP64.Zero;
            FP64 tExit = FP64.MaxValue;
            FP64 eps = FP64.FromDouble(1e-6);

            for (int axis = 0; axis < 2; axis++)
            {
                FP64 o  = axis == 0 ? origin.x : origin.y;
                FP64 d  = axis == 0 ? dir.x    : dir.y;
                FP64 mn = axis == 0 ? minX     : minZ;
                FP64 mx = axis == 0 ? maxX     : maxZ;

                if (FP64.Abs(d) < eps)
                {
                    if (o < mn || o > mx) return false;
                }
                else
                {
                    FP64 t1 = (mn - o) / d;
                    FP64 t2 = (mx - o) / d;
                    if (t1 > t2) { FP64 tmp = t1; t1 = t2; t2 = tmp; }
                    tEnter = FP64.Max(tEnter, t1);
                    tExit  = FP64.Min(tExit,  t2);
                    if (tEnter > tExit) return false;
                }
            }

            return tExit >= FP64.Zero;
        }

        private static bool RayTriangleIntersect(FPVector3 origin, FPVector3 dir,
            FPVector3 a, FPVector3 b, FPVector3 c, out FP64 dist)
        {
            dist = FP64.Zero;
            FP64 eps = FP64.FromDouble(1e-6);

            FPVector3 edge1 = b - a;
            FPVector3 edge2 = c - a;
            FPVector3 h = FPVector3.Cross(dir, edge2);
            FP64 det = FPVector3.Dot(edge1, h);

            if (FP64.Abs(det) < eps) return false;

            FP64 invDet = FP64.One / det;
            FPVector3 s = origin - a;
            FP64 u = invDet * FPVector3.Dot(s, h);
            if (u < FP64.Zero || u > FP64.One) return false;

            FPVector3 q = FPVector3.Cross(s, edge1);
            FP64 v = invDet * FPVector3.Dot(dir, q);
            if (v < FP64.Zero || u + v > FP64.One) return false;

            dist = invDet * FPVector3.Dot(edge2, q);
            return dist > eps;
        }

        private static void CheckEdgeClosest(FPVector2 p, FPVector2 edgeA, FPVector2 edgeB,
            int triIdx, ref FP64 bestSqrDist, ref FPVector2 bestPoint, ref int bestTriIdx)
        {
            FPVector2 closest = ClosestPointOnSegment2D(p, edgeA, edgeB);
            FP64 sqrDist = FPVector2.SqrDistance(p, closest);
            if (sqrDist < bestSqrDist)
            {
                bestSqrDist = sqrDist;
                bestPoint = closest;
                bestTriIdx = triIdx;
            }
        }

        #endregion
    }
}
