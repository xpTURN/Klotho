using System;

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
        // Backing storage may be LARGER than the logical content: a rebake can hand the mesh a
        // recycled buffer. Everything outside [0, count) is stale data
        // from a previous mesh, so the arrays are private and every consumer goes through the
        // spans below — a span's Length IS the count, so `.Length`, `foreach` and indexing are
        // correct by construction, while passing the whole array somewhere (the case that would
        // read the stale tail) no longer compiles.
        private readonly FPVector3[] _vertices;
        private readonly FPNavMeshTriangle[] _triangles;
        private readonly int[] _gridCells;
        private readonly int[] _gridTriangles;

        /// <summary>Logical vertex count (≤ backing array length).</summary>
        public readonly int VertexCount;

        /// <summary>Logical triangle count (≤ backing array length).</summary>
        public readonly int TriangleCount;

        /// <summary>Logical length of <see cref="GridTriangles"/> (≤ backing array length).</summary>
        public readonly int GridTriangleCount;

        /// <summary>
        /// 3D vertices (Y = height, XZ = planar coordinates)
        /// </summary>
        public ReadOnlySpan<FPVector3> Vertices
        {
            get { AssertLive(); return new ReadOnlySpan<FPVector3>(_vertices, 0, VertexCount); }
        }

        /// <summary>
        /// Triangle array including adjacency information
        /// </summary>
        public ReadOnlySpan<FPNavMeshTriangle> Triangles
        {
            get { AssertLive(); return new ReadOnlySpan<FPNavMeshTriangle>(_triangles, 0, TriangleCount); }
        }

        /// <summary>
        /// Mutable view for the one thing this data is allowed to change at runtime — the
        /// per-triangle <c>isBlocked</c> / area attributes (see the type summary). Being a
        /// <c>Span</c> it cannot be stored in a field or captured, so it can only be used for the
        /// write it was taken for.
        /// </summary>
        public Span<FPNavMeshTriangle> TrianglesMutable
        {
            get { AssertLive(); return new Span<FPNavMeshTriangle>(_triangles, 0, TriangleCount); }
        }

        // --- Retirement guard ---
        //
        // Once this mesh's storage has been handed back to the pool, another mesh will overwrite
        // it. Poisoning the arrays at retirement only catches a stale reader until that overwrite
        // happens — and since a growing mesh overwrites the whole logical range, the poison is
        // gone exactly in the common case (buildings accumulate). Detection would therefore have
        // depended on which direction the mesh grew.
        //
        // This flag makes it unconditional: reading a retired mesh throws regardless of what the
        // arrays now contain. DEBUG only — the guard call disappears in release builds, so the
        // query hot path pays nothing. That is the right split: a stale holder can only exist
        // where something keeps a mesh across two swaps, and the only such holder in this repo is
        // the Unity editor tooling, which is a DEBUG build.
        private bool _retired;

        internal void MarkRetired() => _retired = true;

        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertLive()
        {
            if (_retired)
                throw new InvalidOperationException(
                    "FPNavMesh: this mesh was retired — its storage has been returned to the rebake " +
                    "buffer pool and belongs to a newer mesh. Something is holding a navmesh across " +
                    "swaps; read the current mesh from the provider instead of caching one.");
        }

        /// <summary>
        /// Overall XZ bounds (fast rejection test)
        /// </summary>
        public readonly FPBounds2 BoundsXZ;

        // --- Spatial search grid (pre-baked, zero GC at runtime) ---

        /// <summary>
        /// [cellIndex * 2] = GridTriangles start index, [cellIndex * 2 + 1] = triangle count.
        /// Logical length is <c>GridWidth * GridHeight * 2</c> — derived, not stored, so there is
        /// only one source of truth for the cell count.
        /// </summary>
        public ReadOnlySpan<int> GridCells
        {
            get { AssertLive(); return new ReadOnlySpan<int>(_gridCells, 0, GridWidth * GridHeight * 2); }
        }

        /// <summary>
        /// Flat array of triangle indices referenced by GridCells
        /// </summary>
        public ReadOnlySpan<int> GridTriangles
        {
            get { AssertLive(); return new ReadOnlySpan<int>(_gridTriangles, 0, GridTriangleCount); }
        }

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
            FP64 bakeAgentClimb = default,
            int vertexCount = -1,
            int triangleCount = -1,
            int gridTriangleCount = -1)
        {
            _vertices = vertices;
            _triangles = triangles;
            _gridCells = gridCells;
            _gridTriangles = gridTriangles;

            // -1 = "the array is exactly its content", which is what every exact-size producer
            // (the editor bake, deserialization) means. Only a recycling producer passes counts.
            VertexCount = vertexCount < 0 ? vertices.Length : vertexCount;
            TriangleCount = triangleCount < 0 ? triangles.Length : triangleCount;
            GridTriangleCount = gridTriangleCount < 0 ? gridTriangles.Length : gridTriangleCount;

            BoundsXZ = boundsXZ;
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
        /// Hands the raw storage to the buffer pool at retirement. Deliberately the only way out
        /// of this class — everything else sees spans over the logical range.
        /// </summary>
        internal void GetBackingArrays(
            out FPVector3[] vertices, out FPNavMeshTriangle[] triangles,
            out int[] gridCells, out int[] gridTriangles)
        {
            vertices = _vertices;
            triangles = _triangles;
            gridCells = _gridCells;
            gridTriangles = _gridTriangles;
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
            // Backing field, not the span property: this is a query hot path.
            AssertLive();
            int cellIndex = row * GridWidth + col;
            start = _gridCells[cellIndex * 2];
            count = _gridCells[cellIndex * 2 + 1];
        }
    }
}
