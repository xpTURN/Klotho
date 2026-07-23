using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Static obstacle vertex (RVO2 Obstacle equivalent), flat-array representation.
    /// Each vertex i is the start of segment [i -> nextIndex]. Rings may be convex or non-convex
    /// (the isConvex flag classifies each vertex); winding convention: free space on the right of
    /// each edge's unitDir — solid blocks CCW, walkable-boundary loops CW.
    /// </summary>
    [Serializable]
    public struct FPObstacleVertex
    {
        /// <summary>Vertex position (XZ plane).</summary>
        public FPVector2 point;

        /// <summary>Normalized direction of the segment [i -> nextIndex].</summary>
        public FPVector2 unitDir;

        /// <summary>Convex vertex flag (CCW ring; collinear counts as convex).</summary>
        public bool isConvex;

        /// <summary>Index of the next vertex in the same ring (wraps within the ring).</summary>
        public int nextIndex;

        /// <summary>Index of the previous vertex in the same ring (wraps within the ring).</summary>
        public int prevIndex;

        /// <summary>Owning polygon index (debug/log only; not used by the simulation).</summary>
        public int polygonIndex;
    }
}
