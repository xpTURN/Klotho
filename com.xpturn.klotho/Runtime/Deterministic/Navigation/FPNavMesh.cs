using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// FP64-based deterministic NavMesh data.
    /// Pre-baked in the editor by converting a Unity NavMesh.
    /// Read-only at runtime (except for the isBlocked flag).
    /// </summary>
    public class FPNavMesh
    {
        /// <summary>
        /// 3D vertices (Y = height, XZ = planar coordinates)
        /// </summary>
        public readonly FPVector3[] Vertices;

        /// <summary>
        /// Triangle array including adjacency information
        /// </summary>
        public readonly FPNavMeshTriangle[] Triangles;

        /// <summary>
        /// Overall XZ bounds (fast rejection test)
        /// </summary>
        public readonly FPBounds2 BoundsXZ;

        // --- Spatial search grid (pre-baked, zero GC at runtime) ---

        /// <summary>
        /// [cellIndex * 2] = GridTriangles start index, [cellIndex * 2 + 1] = triangle count
        /// </summary>
        public readonly int[] GridCells;

        /// <summary>
        /// Flat array of triangle indices referenced by GridCells
        /// </summary>
        public readonly int[] GridTriangles;

        public readonly int GridWidth;
        public readonly int GridHeight;
        public readonly FP64 GridCellSize;
        public readonly FPVector2 GridOrigin;

        // --- Bake settings block (recorded at export, VERSION 3) ---
        // The asset is self-describing about how it was baked; riding these on the asset keeps
        // lockstep peers symmetric by construction (no hand-synced constants) and lets the JSON
        // sidecar identify a stage baked with the wrong settings.

        /// <summary>
        /// Agent Radius the source NavMesh was baked with — the boundary sits this far inside the
        /// physical walls. Consumer: FPNavAgentSystem.LoadNavMeshObstacles applies it as
        /// FPNavAvoidance.ObstacleRadiusInset (clearance not double-charged). 0 = unknown/uninset.
        /// </summary>
        public readonly FP64 BakeAgentRadius;

        /// <summary>
        /// Max walkable slope the source NavMesh was baked with, in degrees. Consumer: the
        /// graph-local obstacle query auto-derives its sound climb cap from this
        /// (obstRange × FP64.Sin(deg × Deg2Rad) — see FPNavAgentSystem.MaxClimbWithinHorizon).
        /// 0 = unknown (no auto cap).
        /// </summary>
        public readonly FP64 BakeMaxSlopeDeg;

        /// <summary>
        /// Agent Height the source NavMesh was baked with (meters). No runtime consumer —
        /// recorded so the bake settings block is complete (vertical clearance is bake-time only).
        /// </summary>
        public readonly FP64 BakeAgentHeight;

        /// <summary>
        /// Agent step/climb height the source NavMesh was baked with (meters). No runtime
        /// consumer — climbability is already baked into the mesh connectivity.
        /// </summary>
        public readonly FP64 BakeAgentClimb;

        public FPNavMesh(
            FPVector3[] vertices,
            FPNavMeshTriangle[] triangles,
            FPBounds2 boundsXZ,
            int[] gridCells,
            int[] gridTriangles,
            int gridWidth,
            int gridHeight,
            FP64 gridCellSize,
            FPVector2 gridOrigin,
            FP64 bakeAgentRadius = default,
            FP64 bakeMaxSlopeDeg = default,
            FP64 bakeAgentHeight = default,
            FP64 bakeAgentClimb = default)
        {
            Vertices = vertices;
            Triangles = triangles;
            BoundsXZ = boundsXZ;
            GridCells = gridCells;
            GridTriangles = gridTriangles;
            GridWidth = gridWidth;
            GridHeight = gridHeight;
            GridCellSize = gridCellSize;
            GridOrigin = gridOrigin;
            BakeAgentRadius = bakeAgentRadius;
            BakeMaxSlopeDeg = bakeMaxSlopeDeg;
            BakeAgentHeight = bakeAgentHeight;
            BakeAgentClimb = bakeAgentClimb;
        }

        /// <summary>
        /// Compute grid cell coordinates (XZ -> col, row)
        /// </summary>
        public void GetCellCoords(FPVector2 xz, out int col, out int row)
        {
            FPVector2 local = xz - GridOrigin;
            col = (local.x / GridCellSize).ToInt();
            row = (local.y / GridCellSize).ToInt();
        }

        /// <summary>
        /// Validate whether the cell coordinates are valid.
        /// </summary>
        public bool IsCellValid(int col, int row)
        {
            return col >= 0 && col < GridWidth && row >= 0 && row < GridHeight;
        }

        /// <summary>
        /// Iterates the triangle indices contained in the cell.
        /// Returns start/count as out parameters so callers can access GridTriangles[start..start+count-1].
        /// </summary>
        public void GetCellTriangles(int col, int row, out int start, out int count)
        {
            int cellIndex = row * GridWidth + col;
            start = GridCells[cellIndex * 2];
            count = GridCells[cellIndex * 2 + 1];
        }
    }
}
