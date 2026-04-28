using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point box collision shape. Defined by halfExtents, position, and rotation.
    /// </summary>
    [Serializable]
    public struct FPBoxShape : IEquatable<FPBoxShape>
    {
        public FPVector3 halfExtents;
        public FPVector3 position;
        public FPQuaternion rotation;

        public FPBoxShape(FPVector3 halfExtents, FPVector3 position, FPQuaternion rotation)
        {
            this.halfExtents = halfExtents;
            this.position = position;
            this.rotation = rotation;
        }

        public FPBoxShape(FPVector3 halfExtents, FPVector3 position)
        {
            this.halfExtents = halfExtents;
            this.position = position;
            this.rotation = FPQuaternion.Identity;
        }

        public bool IsAxisAligned => rotation == FPQuaternion.Identity;

        public FPVector3 size => halfExtents * FP64.FromInt(2);

        public FPBounds3 GetWorldBounds()
        {
            if (IsAxisAligned)
            {
                return new FPBounds3(position, size);
            }

            FPVector3 axisX = rotation * new FPVector3(halfExtents.x, FP64.Zero, FP64.Zero);
            FPVector3 axisY = rotation * new FPVector3(FP64.Zero, halfExtents.y, FP64.Zero);
            FPVector3 axisZ = rotation * new FPVector3(FP64.Zero, FP64.Zero, halfExtents.z);

            FP64 ex = FP64.Abs(axisX.x) + FP64.Abs(axisY.x) + FP64.Abs(axisZ.x);
            FP64 ey = FP64.Abs(axisX.y) + FP64.Abs(axisY.y) + FP64.Abs(axisZ.y);
            FP64 ez = FP64.Abs(axisX.z) + FP64.Abs(axisY.z) + FP64.Abs(axisZ.z);

            FPVector3 worldExtents = new FPVector3(ex, ey, ez);
            var bounds = default(FPBounds3);
            bounds.center = position;
            bounds.extents = worldExtents;
            return bounds;
        }

        public void GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ)
        {
            axisX = rotation * FPVector3.Right;
            axisY = rotation * FPVector3.Up;
            axisZ = rotation * FPVector3.Forward;
        }

        public void GetVertices(Span<FPVector3> vertices)
        {
            FPVector3 ax = rotation * new FPVector3(halfExtents.x, FP64.Zero, FP64.Zero);
            FPVector3 ay = rotation * new FPVector3(FP64.Zero, halfExtents.y, FP64.Zero);
            FPVector3 az = rotation * new FPVector3(FP64.Zero, FP64.Zero, halfExtents.z);

            vertices[0] = position - ax - ay - az;
            vertices[1] = position + ax - ay - az;
            vertices[2] = position - ax + ay - az;
            vertices[3] = position + ax + ay - az;
            vertices[4] = position - ax - ay + az;
            vertices[5] = position + ax - ay + az;
            vertices[6] = position - ax + ay + az;
            vertices[7] = position + ax + ay + az;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 d = point - position;

            GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

            FPVector3 result = position;
            result = result + axisX * FP64.Clamp(FPVector3.Dot(d, axisX), -halfExtents.x, halfExtents.x);
            result = result + axisY * FP64.Clamp(FPVector3.Dot(d, axisY), -halfExtents.y, halfExtents.y);
            result = result + axisZ * FP64.Clamp(FPVector3.Dot(d, axisZ), -halfExtents.z, halfExtents.z);
            return result;
        }

        public bool Contains(FPVector3 point)
        {
            FPVector3 d = point - position;

            GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

            FP64 px = FP64.Abs(FPVector3.Dot(d, axisX));
            FP64 py = FP64.Abs(FPVector3.Dot(d, axisY));
            FP64 pz = FP64.Abs(FPVector3.Dot(d, axisZ));

            return px <= halfExtents.x && py <= halfExtents.y && pz <= halfExtents.z;
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            FPVector3 closest = ClosestPoint(point);
            return (point - closest).sqrMagnitude;
        }

        public bool Equals(FPBoxShape other)
        {
            return halfExtents == other.halfExtents
                && position == other.position
                && rotation == other.rotation;
        }

        public override bool Equals(object obj) => obj is FPBoxShape other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(halfExtents, position, rotation);
        }

        public static bool operator ==(FPBoxShape a, FPBoxShape b) => a.Equals(b);
        public static bool operator !=(FPBoxShape a, FPBoxShape b) => !a.Equals(b);

        public override string ToString()
        {
            return $"FPBoxShape(halfExtents:{halfExtents}, pos:{position}, rot:{rotation})";
        }
    }
}
