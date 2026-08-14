using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// NavMesh triangle data.
    /// Contains vertex indices, adjacency, portal info, and precomputed values.
    /// </summary>
    [Serializable]
    public struct FPNavMeshTriangle
    {
        // The three GETTERS below are `readonly`; the two setters are not, and the split is load
        // bearing rather than decorative. This is a mutable struct that consumers reach through
        // read-only references — FPNavMesh.Triangles is a ReadOnlySpan and the query, funnel,
        // agent and rebaker paths all bind `ref readonly FPNavMeshTriangle`. Calling a
        // non-`readonly` instance method on such a reference makes the compiler copy the whole
        // struct defensively, and this struct is 88 bytes (pinned by FPNavMeshTriangleSizeTests);
        // the A* edge loop alone would take two copies per edge, three edges per triangle expanded.
        //
        // Measured, today, that costs nothing: the pathfinding sweep moves 0.55% — noise — because
        // the JIT inlines these and scalarises the copy away. `readonly` is here because that is an
        // observation about the current JIT and the current bodies, not a contract. It stops being
        // true if an accessor grows past the inliner's budget, and nothing in the source would
        // change when it does.

        // vertex indices (reference to FPNavMesh.Vertices)
        public int v0;
        public int v1;
        public int v2;

        // adjacent triangle indices (-1 = boundary edge)
        // neighbor[i] shares edge (v[i], v[(i+1)%3])
        public int neighbor0;       // edge v0-v1
        public int neighbor1;       // edge v1-v2
        public int neighbor2;       // edge v2-v0

        // precomputed centroid for A* heuristic (XZ)
        public FPVector2 centerXZ;

        // triangle area (validity check + containment test)
        public FP64 area;

        // area mask (walkable, water, cost zones, etc.)
        public int areaMask;

        // dynamic obstacle blocking flag
        public bool isBlocked;

        // Funnel portal orientation, one bit per edge (bit e = edge e).
        // The baker's left/right portal pair is ALWAYS a permutation of that edge's own vertex
        // pair, so the only information it carries is "is it flipped?" — bit clear = (va, vb),
        // bit set = (vb, va). See GetPortal/SetPortal; boundary edges (neighbor < 0) carry no
        // portal and their bit is meaningless.
        //
        // The sign that decides it is Cross(edgeMid - opposite, va - vb) = -2 * signedArea, which
        // is the same for all three edges of a triangle — so in a consistently wound mesh this
        // byte is either 0x00 (CCW) or the triangle's interior-edge mask (CW). It is kept per
        // triangle rather than per mesh because global winding consistency is a test assertion,
        // not a contract of this struct.
        //
        // Placed next to areaMask/isBlocked so it lands in existing alignment padding: removing
        // the six portal ints took 120 -> 96 B and this byte costs nothing on top.
        public byte portalFlip;

        // cost multiplier (1.0 = default, e.g. swamp = 2.0)
        public FP64 costMultiplier;

        // Y-axis height range (multi-floor support)
        public FP64 minY;       // minimum of the three vertices
        public FP64 maxY;       // maximum of the three vertices
        public FP64 centerY;    // (minY + maxY) * 0.5

        /// <summary>
        /// Returns the adjacent triangle index by edge local index (0, 1, 2).
        /// </summary>
        public readonly int GetNeighbor(int edgeIndex)
        {
            switch (edgeIndex)
            {
                case 0: return neighbor0;
                case 1: return neighbor1;
                case 2: return neighbor2;
                default: return -1;
            }
        }

        /// <summary>
        /// Sets the adjacent triangle index by edge local index (0, 1, 2).
        /// </summary>
        public void SetNeighbor(int edgeIndex, int triIndex)
        {
            switch (edgeIndex)
            {
                case 0: neighbor0 = triIndex; break;
                case 1: neighbor1 = triIndex; break;
                case 2: neighbor2 = triIndex; break;
            }
        }

        /// <summary>
        /// Returns the vertex index pair for an edge.
        /// edgeIndex: 0 = (v0,v1), 1 = (v1,v2), 2 = (v2,v0)
        /// </summary>
        public readonly void GetEdgeVertices(int edgeIndex, out int va, out int vb)
        {
            switch (edgeIndex)
            {
                case 0: va = v0; vb = v1; return;
                case 1: va = v1; vb = v2; return;
                case 2: va = v2; vb = v0; return;
                default: va = -1; vb = -1; return;
            }
        }

        /// <summary>
        /// Returns the portal vertex index pair, or (-1, -1) for a boundary edge.
        ///
        /// Decoded from <see cref="portalFlip"/>: the pair is always this edge's own vertices,
        /// ordered by that edge's bit. Boundary detection comes from <see cref="GetNeighbor"/> —
        /// neighbor is the single source of truth for "this edge has no portal", which is what
        /// lets the six stored indices collapse into three bits.
        /// </summary>
        public readonly void GetPortal(int edgeIndex, out int left, out int right)
        {
            if (GetNeighbor(edgeIndex) < 0)
            {
                left = -1;
                right = -1;
                return;
            }

            GetEdgeVertices(edgeIndex, out int va, out int vb);
            if ((portalFlip & (1 << edgeIndex)) != 0)
            {
                left = vb;
                right = va;
            }
            else
            {
                left = va;
                right = vb;
            }
        }

        /// <summary>
        /// Records the portal orientation for an edge.
        ///
        /// <paramref name="left"/>/<paramref name="right"/> must be that edge's own vertex pair in
        /// either order (or (-1, -1) for a boundary edge) — anything else cannot be represented and
        /// means the caller derived the pair from something other than this triangle's edge.
        ///
        /// The contract check is NOT compiled out in release builds: the server runs release, and a
        /// violation that passes silently there would leave the flag at its previous value — the
        /// quiet-wrong-answer class this encoding exists to avoid. It costs nothing to keep, since
        /// the only callers are the two adjacency builders and they run at bake time, not per tick.
        /// </summary>
        public void SetPortal(int edgeIndex, int left, int right)
        {
            if (edgeIndex < 0 || edgeIndex > 2)
                throw new ArgumentOutOfRangeException(nameof(edgeIndex));

            int bit = 1 << edgeIndex;
            GetEdgeVertices(edgeIndex, out int va, out int vb);

            if (left == va && right == vb)
                portalFlip &= (byte)~bit;
            else if (left == vb && right == va)
                portalFlip |= (byte)bit;
            else if (left < 0 && right < 0)
                portalFlip &= (byte)~bit;   // boundary edge — the bit carries no meaning
            else
                throw new ArgumentException(
                    $"SetPortal(e{edgeIndex}, {left}, {right}): not a permutation of edge ({va},{vb})");
        }
    }
}
