using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Hinge joint that allows two bodies to rotate around an axis.
    /// </summary>
    [Serializable]
    public struct FPHingeJoint
    {
        public int bodyIndexA;
        public int bodyIndexB;
        public FPVector3 pivotA;
        public FPVector3 pivotB;
        public FPVector3 axisA;
        public FPVector3 axisB;
        public bool useLimits;
        public FP64 lowerAngle;
        public FP64 upperAngle;
        public FPVector3 refAxisA;
        public FPVector3 refAxisB;
    }
}
