using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Joint that keeps a constant distance between two bodies.
    /// </summary>
    [Serializable]
    public struct FPDistanceJoint
    {
        public int bodyIndexA;
        public int bodyIndexB;
        public FPVector3 anchorA;
        public FPVector3 anchorB;
        public FP64 distance;
    }
}
