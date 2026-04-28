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

        public FPNavMesh(
            FPVector3[] vertices,
            FPNavMeshTriangle[] triangles,
            FPBounds2 boundsXZ,
            int[] gridCells,
            int[] gridTriangles,
            int gridWidth,
            int gridHeight,
            FP64 gridCellSize,
            FPVector2 gridOrigin)
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
