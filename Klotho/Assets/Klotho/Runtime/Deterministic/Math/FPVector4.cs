using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 4D vector implementation.
    /// </summary>
    [Primitive]
    [Serializable]
    public partial struct FPVector4 : IEquatable<FPVector4>
    {
        public FP64 x;
        public FP64 y;
        public FP64 z;
        public FP64 w;

        public static readonly FPVector4 Zero = new FPVector4(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero);
        public static readonly FPVector4 One = new FPVector4(FP64.One, FP64.One, FP64.One, FP64.One);

        public FPVector4(FP64 x, FP64 y, FP64 z, FP64 w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public FPVector4(int x, int y, int z, int w)
        {
            this.x = FP64.FromInt(x);
            this.y = FP64.FromInt(y);
            this.z = FP64.FromInt(z);
            this.w = FP64.FromInt(w);
        }

        public FPVector4(float x, float y, float z, float w)
        {
            this.x = FP64.FromFloat(x);
            this.y = FP64.FromFloat(y);
            this.z = FP64.FromFloat(z);
            this.w = FP64.FromFloat(w);
        }

        public FPVector4(FPVector3 v, FP64 w)
        {
            this.x = v.x;
            this.y = v.y;
            this.z = v.z;
            this.w = w;
        }

        public FPVector4(FPVector2 v, FP64 z, FP64 w)
        {
            this.x = v.x;
            this.y = v.y;
            this.z = z;
            this.w = w;
        }

        public FP64 sqrMagnitude => x * x + y * y + z * z + w * w;

        public FP64 magnitude
        {
            get
            {
                FP64 ax = FP64.Abs(x);
                FP64 ay = FP64.Abs(y);
                FP64 az = FP64.Abs(z);
                FP64 aw = FP64.Abs(w);
                FP64 max = FP64.Max(FP64.Max(ax, ay), FP64.Max(az, aw));
                if (max == FP64.Zero)
                    return FP64.Zero;
                FP64 nx = ax / max;
                FP64 ny = ay / max;
                FP64 nz = az / max;
                FP64 nw = aw / max;
                return max * FP64.Sqrt(nx * nx + ny * ny + nz * nz + nw * nw);
            }
        }

        public FPVector4 normalized
        {
            get
            {
                FP64 mag = magnitude;
                if (mag == FP64.Zero)
                    return Zero;
                return new FPVector4(x / mag, y / mag, z / mag, w / mag);
            }
        }

        /// <summary>
        /// Converts to a float array [x, y, z, w].
        /// </summary>
        public float[] ToFloatArray()
        {
            return new float[] { x.ToFloat(), y.ToFloat(), z.ToFloat(), w.ToFloat() };
        }

        // Operator overloads
        public static FPVector4 operator +(FPVector4 a, FPVector4 b)
        {
            return new FPVector4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        }

        public static FPVector4 operator -(FPVector4 a, FPVector4 b)
        {
            return new FPVector4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        }

        public static FPVector4 operator -(FPVector4 a)
        {
            return new FPVector4(-a.x, -a.y, -a.z, -a.w);
        }

        public static FPVector4 operator *(FPVector4 a, FP64 scalar)
        {
            return new FPVector4(a.x * scalar, a.y * scalar, a.z * scalar, a.w * scalar);
        }

        public static FPVector4 operator *(FP64 scalar, FPVector4 a)
        {
            return new FPVector4(a.x * scalar, a.y * scalar, a.z * scalar, a.w * scalar);
        }

        public static FPVector4 operator /(FPVector4 a, FP64 scalar)
        {
            if (scalar == FP64.Zero)
                return Zero;
            return new FPVector4(a.x / scalar, a.y / scalar, a.z / scalar, a.w / scalar);
        }

        public static bool operator ==(FPVector4 a, FPVector4 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        public static bool operator !=(FPVector4 a, FPVector4 b)
        {
            return !(a == b);
        }

        // Static methods
        public static FP64 Dot(FPVector4 a, FPVector4 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        }

        public static FP64 Distance(FPVector4 a, FPVector4 b)
        {
            return (a - b).magnitude;
        }

        public static FP64 SqrDistance(FPVector4 a, FPVector4 b)
        {
            return (a - b).sqrMagnitude;
        }

        public static FPVector4 Lerp(FPVector4 a, FPVector4 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPVector4(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t),
                FP64.LerpUnclamped(a.z, b.z, t),
                FP64.LerpUnclamped(a.w, b.w, t)
            );
        }

        public static FPVector4 LerpUnclamped(FPVector4 a, FPVector4 b, FP64 t)
        {
            return new FPVector4(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t),
                FP64.LerpUnclamped(a.z, b.z, t),
                FP64.LerpUnclamped(a.w, b.w, t)
            );
        }

        public static FPVector4 MoveTowards(FPVector4 current, FPVector4 target, FP64 maxDistanceDelta)
        {
            FPVector4 diff = target - current;
            FP64 dist = diff.magnitude;

            if (dist <= maxDistanceDelta || dist == FP64.Zero)
                return target;

            return current + diff / dist * maxDistanceDelta;
        }

        public static FPVector4 Project(FPVector4 vector, FPVector4 onNormal)
        {
            FP64 sqrMag = onNormal.sqrMagnitude;
            if (sqrMag == FP64.Zero)
                return Zero;

            FP64 dot = Dot(vector, onNormal);
            return onNormal * dot / sqrMag;
        }

        public static FPVector4 ClampMagnitude(FPVector4 vector, FP64 maxLength)
        {
            FP64 sqrMag = vector.sqrMagnitude;
            if (sqrMag > maxLength * maxLength)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                if (mag == FP64.Zero)
                    return Zero;
                return vector / mag * maxLength;
            }
            return vector;
        }

        public static FPVector4 Min(FPVector4 a, FPVector4 b)
        {
            return new FPVector4(FP64.Min(a.x, b.x), FP64.Min(a.y, b.y), FP64.Min(a.z, b.z), FP64.Min(a.w, b.w));
        }

        public static FPVector4 Max(FPVector4 a, FPVector4 b)
        {
            return new FPVector4(FP64.Max(a.x, b.x), FP64.Max(a.y, b.y), FP64.Max(a.z, b.z), FP64.Max(a.w, b.w));
        }

        public static FPVector4 Scale(FPVector4 a, FPVector4 b)
        {
            return new FPVector4(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
        }

        // Conversion helpers
        public FPVector3 ToVector3()
        {
            return new FPVector3(x, y, z);
        }

        public FPVector2 ToVector2()
        {
            return new FPVector2(x, y);
        }

        // IEquatable implementation
        public bool Equals(FPVector4 other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        public override bool Equals(object obj)
        {
            return obj is FPVector4 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(x, y, z, w);
        }

        public override string ToString()
        {
            return $"({x}, {y}, {z}, {w})";
        }
    }
}
