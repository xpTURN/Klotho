using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Simple Stupid Funnel Algorithm (SSFA).
    /// Converts a corridor (triangle sequence) into a smooth waypoint path.
    /// </summary>
    public class FPNavMeshFunnel
    {
        private readonly FPNavMesh _navMesh;

        private static readonly FP64 FUNNEL_EPSILON = FP64.FromDouble(0.0001);

        // Pre-allocated buffers
        private readonly FPVector3[] _portalLeft;
        private readonly FPVector3[] _portalRight;
        private readonly FPVector3[] _waypoints;

        public const int MAX_PORTALS = 128;
        public const int MAX_WAYPOINTS = 64;

        private ILogger _logger;

        public FPNavMeshFunnel(FPNavMesh navMesh, FPNavMeshQuery query, ILogger logger)
        {
            _navMesh = navMesh;
            _logger = logger;
            
            _portalLeft = new FPVector3[MAX_PORTALS];
            _portalRight = new FPVector3[MAX_PORTALS];
            _waypoints = new FPVector3[MAX_WAYPOINTS];
        }

        /// <summary>
        /// Convert a corridor into a waypoint path.
        /// </summary>
        /// <param name="corridor">Triangle index sequence</param>
        /// <param name="corridorLength">Corridor length</param>
        /// <param name="start">Start position (XZ)</param>
        /// <param name="end">Target position (XZ)</param>
        /// <param name="waypoints">Resulting waypoint array</param>
        /// <param name="waypointCount">Number of waypoints</param>
        /// Warning: the returned waypoints array is a reference to the internal buffer.
        /// It is overwritten on the next Funnel() call, so consume it immediately after the call.
        public void Funnel(int[] corridor, int corridorLength,
            FPVector3 start, FPVector3 end,
            out FPVector3[] waypoints, out int waypointCount)
        {
            waypoints = _waypoints;
            waypointCount = 0;

            if (corridorLength <= 0)
                return;

            // Single triangle - straight line from start to end
            if (corridorLength == 1)
            {
                _waypoints[0] = start;
                _waypoints[1] = end;
                waypointCount = 2;
                return;
            }

            // 1. Build portal sequence
            int portalCount = BuildPortals(corridor, corridorLength, start, end);

            // 2. Trace funnel
            waypointCount = TraceFunnel(portalCount, start, end);
        }

        /// <summary>
        /// Build a portal (left/right vertices) sequence from the corridor.
        /// First portal = start, last portal = end.
        /// </summary>
        private int BuildPortals(int[] corridor, int corridorLength,
            FPVector3 start, FPVector3 end)
        {
            int portalCount = 0;

            // First portal: start
            _portalLeft[0] = start;
            _portalRight[0] = start;
            portalCount++;

            // Shared edges between corridor[i] -> corridor[i+1]
            // start(1) + shared edges(corridorLength-1) + end(1) = corridorLength+1 portals required
            // If MAX_PORTALS is exceeded, the corridor tail is truncated and the end portal is appended after the last processed edge
            for (int i = 0; i < corridorLength - 1 && portalCount < MAX_PORTALS - 1; i++)
            {
                int curTri = corridor[i];
                int nextTri = corridor[i + 1];

                FindSharedPortal(curTri, nextTri,
                    out FPVector3 left, out FPVector3 right);

                _portalLeft[portalCount] = left;
                _portalRight[portalCount] = right;
                portalCount++;
            }

            // Last portal: end
            if (portalCount < MAX_PORTALS)
            {
                _portalLeft[portalCount] = end;
                _portalRight[portalCount] = end;
                portalCount++;
            }

            return portalCount;
        }

        /// <summary>
        /// Returns the precomputed portal vertices (3D) between two adjacent triangles.
        /// Left/right is determined during editor baking via cross product based on the opposite vertex.
        /// </summary>
        private void FindSharedPortal(int triIdx, int nextTriIdx,
            out FPVector3 left, out FPVector3 right)
        {
            ref FPNavMeshTriangle tri = ref _navMesh.Triangles[triIdx];

            for (int e = 0; e < 3; e++)
            {
                if (tri.GetNeighbor(e) == nextTriIdx)
                {
                    tri.GetPortal(e, out int leftIdx, out int rightIdx);
                    left = _navMesh.Vertices[leftIdx];
                    right = _navMesh.Vertices[rightIdx];
                    return;
                }
            }

            // Fallback (does not occur for a valid corridor)
            FPVector2 center = _navMesh.Triangles[triIdx].centerXZ;
            FP64 centerY = _navMesh.Triangles[triIdx].centerY;
            left = new FPVector3(center.x, centerY, center.y);
            right = new FPVector3(center.x, centerY, center.y);
        }

        public FPVector3[] Corners => _waypoints;

        private static readonly FP64 MIN_TARGET_DIST_SQR = FP64.FromDouble(0.01 * 0.01);

        public int FindCorners(int[] corridor, int corridorLength,
            FPVector3 currentPos, FPVector3 target, int maxCorners)
        {
            if (corridorLength <= 0)
                return 0;

            if (corridorLength == 1)
            {
                _waypoints[0] = target;
                return 1;
            }

            int portalCount = BuildPortals(corridor, corridorLength, currentPos, target);
            int cornerCount = TraceFunnelLimited(portalCount, currentPos, target, maxCorners);
            cornerCount = PruneNearCorners(currentPos, cornerCount);
            return cornerCount;
        }

        private int TraceFunnelLimited(int portalCount, FPVector3 start, FPVector3 end,
            int maxCorners)
        {
            int wpCount = 0;

            FPVector2 apex = start.ToXZ();
            FPVector2 funnelLeft = start.ToXZ();
            FPVector2 funnelRight = start.ToXZ();
            int apexIndex = 0;
            int leftIndex = 0;
            int rightIndex = 0;

            for (int i = 1; i < portalCount && wpCount < maxCorners; i++)
            {
                FPVector2 newLeft = _portalLeft[i].ToXZ();
                FPVector2 newRight = _portalRight[i].ToXZ();

                if (FPVector2.Cross(funnelRight - apex, newRight - apex) <= FUNNEL_EPSILON)
                {
                    if (apex == funnelRight || FPVector2.Cross(funnelLeft - apex, newRight - apex) > -FUNNEL_EPSILON)
                    {
                        funnelRight = newRight;
                        rightIndex = i;
                    }
                    else
                    {
                        apex = funnelLeft;
                        FP64 y = _portalLeft[leftIndex].y;
                        _waypoints[wpCount++] = new FPVector3(apex.x, y, apex.y);

                        apexIndex = leftIndex;
                        funnelLeft = apex;
                        funnelRight = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }

                if (FPVector2.Cross(funnelLeft - apex, newLeft - apex) >= -FUNNEL_EPSILON)
                {
                    if (apex == funnelLeft || FPVector2.Cross(funnelRight - apex, newLeft - apex) < FUNNEL_EPSILON)
                    {
                        funnelLeft = newLeft;
                        leftIndex = i;
                    }
                    else
                    {
                        apex = funnelRight;
                        FP64 y = _portalRight[rightIndex].y;
                        _waypoints[wpCount++] = new FPVector3(apex.x, y, apex.y);

                        apexIndex = rightIndex;
                        funnelLeft = apex;
                        funnelRight = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }
            }

            if (wpCount < maxCorners)
            {
                _waypoints[wpCount++] = new FPVector3(end.x, end.y, end.z);
            }

            return wpCount;
        }

        private int PruneNearCorners(FPVector3 currentPos, int count)
        {
            FPVector2 posXZ = currentPos.ToXZ();
            int start = 0;
            while (start < count - 1)
            {
                FPVector2 cpXZ = _waypoints[start].ToXZ();
                if (FPVector2.SqrDistance(posXZ, cpXZ) > MIN_TARGET_DIST_SQR)
                    break;
                start++;
            }
            if (start > 0)
            {
                Array.Copy(_waypoints, start, _waypoints, 0, count - start);
                count -= start;
            }
            return count;
        }

        /// <summary>
        /// SSFA funnel tracing.
        /// </summary>
        private int TraceFunnel(int portalCount, FPVector3 start, FPVector3 end)
        {
            int wpCount = 0;

            FPVector2 apex = start.ToXZ();
            FPVector2 funnelLeft = start.ToXZ();
            FPVector2 funnelRight = start.ToXZ();
            int apexIndex = 0;
            int leftIndex = 0;
            int rightIndex = 0;

            // start waypoint
            _waypoints[wpCount++] = new FPVector3(start.x, start.y, start.z);

            for (int i = 1; i < portalCount && wpCount < MAX_WAYPOINTS - 1; i++)
            {
                FPVector2 newLeft = _portalLeft[i].ToXZ();
                FPVector2 newRight = _portalRight[i].ToXZ();

                // Tighten on the right side
                if (FPVector2.Cross(funnelRight - apex, newRight - apex) <= FUNNEL_EPSILON)
                {
                    // Note: apex == funnelRight is a bit-exact comparison (FP64 raw values match).
                    // apex is always assigned directly from the portal buffer, so exact equality holds.
                    if (apex == funnelRight || FPVector2.Cross(funnelLeft - apex, newRight - apex) > -FUNNEL_EPSILON)
                    {
                        funnelRight = newRight;
                        rightIndex = i;
                    }
                    else
                    {
                        // Left vertex becomes a waypoint
                        apex = funnelLeft;
                        FP64 y = _portalLeft[leftIndex].y;
                        _waypoints[wpCount++] = new FPVector3(apex.x, y, apex.y);

                        apexIndex = leftIndex;
                        funnelLeft = apex;
                        funnelRight = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }

                // Tighten on the left side
                if (FPVector2.Cross(funnelLeft - apex, newLeft - apex) >= -FUNNEL_EPSILON)
                {
                    // Note: apex == funnelLeft is a bit-exact comparison - see the comment above
                    if (apex == funnelLeft || FPVector2.Cross(funnelRight - apex, newLeft - apex) < FUNNEL_EPSILON)
                    {
                        funnelLeft = newLeft;
                        leftIndex = i;
                    }
                    else
                    {
                        // Right vertex becomes a waypoint
                        apex = funnelRight;
                        FP64 y = _portalRight[rightIndex].y;
                        _waypoints[wpCount++] = new FPVector3(apex.x, y, apex.y);

                        apexIndex = rightIndex;
                        funnelLeft = apex;
                        funnelRight = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }
            }

            // end waypoint
            if (wpCount < MAX_WAYPOINTS)
            {
                _waypoints[wpCount++] = new FPVector3(end.x, end.y, end.z);
            }

            return wpCount;
        }
    }
}
