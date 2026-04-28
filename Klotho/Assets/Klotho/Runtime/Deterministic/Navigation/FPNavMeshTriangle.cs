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
        // vertex indices (reference to FPNavMesh.Vertices)
        public int v0;
        public int v1;
        public int v2;

        // adjacent triangle indices (-1 = boundary edge)
        // neighbor[i] shares edge (v[i], v[(i+1)%3])
        public int neighbor0;       // edge v0-v1
        public int neighbor1;       // edge v1-v2
        public int neighbor2;       // edge v2-v0

        // portal vertex indices for funnel algorithm (left, right)
        public int portal0Left;
        public int portal0Right;
        public int portal1Left;
        public int portal1Right;
        public int portal2Left;
        public int portal2Right;

        // precomputed centroid for A* heuristic (XZ)
        public FPVector2 centerXZ;

        // triangle area (validity check + containment test)
        public FP64 area;

        // area mask (walkable, water, cost zones, etc.)
        public int areaMask;

        // cost multiplier (1.0 = default, e.g. swamp = 2.0)
        public FP64 costMultiplier;

        // dynamic obstacle blocking flag
        public bool isBlocked;

        // Y-axis height range (multi-floor support)
        public FP64 minY;       // minimum of the three vertices
        public FP64 maxY;       // maximum of the three vertices
        public FP64 centerY;    // (minY + maxY) * 0.5

        /// <summary>
        /// Returns the adjacent triangle index by edge local index (0, 1, 2).
        /// </summary>
        public int GetNeighbor(int edgeIndex)
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
        public void GetEdgeVertices(int edgeIndex, out int va, out int vb)
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
        /// Returns the portal vertex index pair.
        /// </summary>
        public void GetPortal(int edgeIndex, out int left, out int right)
        {
            switch (edgeIndex)
            {
                case 0: left = portal0Left; right = portal0Right; return;
                case 1: left = portal1Left; right = portal1Right; return;
                case 2: left = portal2Left; right = portal2Right; return;
                default: left = -1; right = -1; return;
            }
        }

        /// <summary>
        /// Sets the portal vertex indices.
        /// </summary>
        public void SetPortal(int edgeIndex, int left, int right)
        {
            switch (edgeIndex)
            {
                case 0: portal0Left = left; portal0Right = right; break;
                case 1: portal1Left = left; portal1Right = right; break;
                case 2: portal2Left = left; portal2Right = right; break;
            }
        }
    }
}
