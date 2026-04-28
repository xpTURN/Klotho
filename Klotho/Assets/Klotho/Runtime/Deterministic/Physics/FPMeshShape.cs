using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point mesh collision shape. Defined by position and rotation.
    /// </summary>
    [Serializable]
    public struct FPMeshShape
    {
        public FPVector3 position;
        public FPQuaternion rotation;

        public FPMeshShape(FPVector3 position, FPQuaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public FPBounds3 GetWorldBounds(FPMeshData data)
        {
            FPVector3 localExtents = data.localBounds.extents;

            FPVector3 axisX = rotation * new FPVector3(localExtents.x, FP64.Zero, FP64.Zero);
            FPVector3 axisY = rotation * new FPVector3(FP64.Zero, localExtents.y, FP64.Zero);
            FPVector3 axisZ = rotation * new FPVector3(FP64.Zero, FP64.Zero, localExtents.z);

            FP64 ex = FP64.Abs(axisX.x) + FP64.Abs(axisY.x) + FP64.Abs(axisZ.x);
            FP64 ey = FP64.Abs(axisX.y) + FP64.Abs(axisY.y) + FP64.Abs(axisZ.y);
            FP64 ez = FP64.Abs(axisX.z) + FP64.Abs(axisY.z) + FP64.Abs(axisZ.z);

            FPVector3 worldCenter = position + rotation * data.localBounds.center;
            FPVector3 worldExtents = new FPVector3(ex, ey, ez);

            var bounds = default(FPBounds3);
            bounds.center = worldCenter;
            bounds.extents = worldExtents;
            return bounds;
        }
    }
}
