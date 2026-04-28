using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Static BVH tree node.
    /// left, right: child node indices (-1 means leaf).
    /// leafIndex: valid only for leaves. &gt;= 0 is a bodies[] index (isStatic=true), &lt; 0 is a staticColliders[] index.
    /// </summary>
    internal struct FPBVHNode
    {
        internal FPBounds3 bounds;
        internal int left;      // Child node index (-1: leaf)
        internal int right;
        internal int leafIndex; // Valid only for leaves.
                                // >= 0: bodies[] index (FPPhysicsBody, isStatic=true)
                                // <  0: ~staticColliders[] index (FPStaticCollider)
    }
}
