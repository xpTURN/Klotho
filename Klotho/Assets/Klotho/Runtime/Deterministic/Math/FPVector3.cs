using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 3D vector implementation
    /// </summary>
    [Primitive]
    [Serializable]
    public partial struct FPVector3 : IEquatable<FPVector3>
    {
        public FP64 x;
        public FP64 y;
        public FP64 z;

        public static readonly FPVector3 Zero = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
        public static readonly FPVector3 One = new FPVector3(FP64.One, FP64.One, FP64.One);
        public static readonly FPVector3 Up = new FPVector3(FP64.Zero, FP64.One, FP64.Zero);
        public static readonly FPVector3 Down = new FPVector3(FP64.Zero, -FP64.One, FP64.Zero);
        public static readonly FPVector3 Left = new FPVector3(-FP64.One, FP64.Zero, FP64.Zero);
        public static readonly FPVector3 Right = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
        public static readonly FPVector3 Forward = new FPVector3(FP64.Zero, FP64.Zero, FP64.One);
        public static readonly FPVector3 Back = new FPVector3(FP64.Zero, FP64.Zero, -FP64.One);

        public FPVector3(FP64 x, FP64 y, FP64 z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public FPVector3(int x, int y, int z)
        {
            this.x = FP64.FromInt(x);
            this.y = FP64.FromInt(y);
            this.z = FP64.FromInt(z);
        }

        public FPVector3(float x, float y, float z)
        {
            this.x = FP64.FromFloat(x);
            this.y = FP64.FromFloat(y);
            this.z = FP64.FromFloat(z);
        }

        public FP64 sqrMagnitude => x * x + y * y + z * z;

        public FP64 magnitude
        {
            get
            {
                FP64 ax = FP64.Abs(x);
                FP64 ay = FP64.Abs(y);
                FP64 az = FP64.Abs(z);
                FP64 max = FP64.Max(FP64.Max(ax, ay), az);
                if (max == FP64.Zero)
                    return FP64.Zero;
                FP64 nx = ax / max;
                FP64 ny = ay / max;
                FP64 nz = az / max;
                return max * FP64.Sqrt(nx * nx + ny * ny + nz * nz);
            }
        }

        public FPVector3 normalized
        {
            get
            {
                FP64 mag = magnitude;
                if (mag == FP64.Zero)
                    return Zero;
                return new FPVector3(x / mag, y / mag, z / mag);
            }
        }

        /// <summary>
        /// Convert to a float array [x, y, z]
        /// </summary>
        public float[] ToFloatArray()
        {
            return new float[] { x.ToFloat(), y.ToFloat(), z.ToFloat() };
        }

        // operator overloads
        public static FPVector3 operator +(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static FPVector3 operator -(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static FPVector3 operator -(FPVector3 a)
        {
            return new FPVector3(-a.x, -a.y, -a.z);
        }

        public static FPVector3 operator *(FPVector3 a, FP64 scalar)
        {
            return new FPVector3(a.x * scalar, a.y * scalar, a.z * scalar);
        }

        public static FPVector3 operator *(FP64 scalar, FPVector3 a)
        {
            return new FPVector3(a.x * scalar, a.y * scalar, a.z * scalar);
        }

        public static FPVector3 operator /(FPVector3 a, FP64 scalar)
        {
            if (scalar == FP64.Zero)
                return Zero;
            return new FPVector3(a.x / scalar, a.y / scalar, a.z / scalar);
        }

        public static bool operator ==(FPVector3 a, FPVector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        public static bool operator !=(FPVector3 a, FPVector3 b)
        {
            return !(a == b);
        }

        // static methods
        public static FP64 Dot(FPVector3 a, FPVector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        public static FPVector3 Cross(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x
            );
        }

        public static FP64 Distance(FPVector3 a, FPVector3 b)
        {
            return (a - b).magnitude;
        }

        public static FP64 SqrDistance(FPVector3 a, FPVector3 b)
        {
            return (a - b).sqrMagnitude;
        }

        public static FPVector3 Lerp(FPVector3 a, FPVector3 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPVector3(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t),
                FP64.LerpUnclamped(a.z, b.z, t)
            );
        }

        public static FPVector3 LerpUnclamped(FPVector3 a, FPVector3 b, FP64 t)
        {
            return new FPVector3(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t),
                FP64.LerpUnclamped(a.z, b.z, t)
            );
        }

        public static FPVector3 MoveTowards(FPVector3 current, FPVector3 target, FP64 maxDistanceDelta)
        {
            FPVector3 diff = target - current;
            FP64 dist = diff.magnitude;

            if (dist <= maxDistanceDelta || dist == FP64.Zero)
                return target;

            return current + diff / dist * maxDistanceDelta;
        }

        public static FPVector3 Reflect(FPVector3 direction, FPVector3 normal)
        {
            FP64 dot2 = FP64.FromInt(2) * Dot(direction, normal);
            return direction - normal * dot2;
        }

        public static FPVector3 Project(FPVector3 vector, FPVector3 onNormal)
        {
            FP64 sqrMag = onNormal.sqrMagnitude;
            if (sqrMag == FP64.Zero)
                return Zero;

            FP64 dot = Dot(vector, onNormal);
            return onNormal * dot / sqrMag;
        }

        public static FPVector3 ProjectOnPlane(FPVector3 vector, FPVector3 planeNormal)
        {
            return vector - Project(vector, planeNormal);
        }

        public static FP64 Angle(FPVector3 from, FPVector3 to)
        {
            FPVector3 fn = from.normalized;
            FPVector3 tn = to.normalized;
            if (fn == Zero || tn == Zero)
                return FP64.Zero;

            FP64 dot = FP64.Clamp(Dot(fn, tn), -FP64.One, FP64.One);
            return FP64.Acos(dot) * FP64.Rad2Deg;
        }

        public static FPVector3 ClampMagnitude(FPVector3 vector, FP64 maxLength)
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

        public static FPVector3 Min(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(FP64.Min(a.x, b.x), FP64.Min(a.y, b.y), FP64.Min(a.z, b.z));
        }

        public static FPVector3 Max(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(FP64.Max(a.x, b.x), FP64.Max(a.y, b.y), FP64.Max(a.z, b.z));
        }

        public static FPVector3 Scale(FPVector3 a, FPVector3 b)
        {
            return new FPVector3(a.x * b.x, a.y * b.y, a.z * b.z);
        }

        public static FP64 SignedAngle(FPVector3 from, FPVector3 to, FPVector3 axis)
        {
            FP64 unsignedAngle = Angle(from, to);
            FPVector3 cross = Cross(from, to);
            FP64 sign = Dot(axis, cross);
            if (sign < FP64.Zero)
                return -unsignedAngle;
            return unsignedAngle;
        }

        public static FPVector3 Slerp(FPVector3 a, FPVector3 b, FP64 t)
        {
            return SlerpUnclamped(a, b, FP64.Clamp01(t));
        }

        public static FPVector3 SlerpUnclamped(FPVector3 a, FPVector3 b, FP64 t)
        {
            FP64 magA = a.magnitude;
            FP64 magB = b.magnitude;
            if (magA == FP64.Zero || magB == FP64.Zero)
                return LerpUnclamped(a, b, t);

            FPVector3 na = a / magA;
            FPVector3 nb = b / magB;

            FP64 dot = FP64.Clamp(Dot(na, nb), -FP64.One, FP64.One);

            // nearly parallel: fall back to lerp to avoid division by zero
            if (dot > FP64.FromRaw(FP64.ONE - 100))
            {
                FPVector3 result = LerpUnclamped(a, b, t);
                return result;
            }

            FP64 omega = FP64.Acos(dot);
            FP64 sinOmega = FP64.Sin(omega);

            FP64 factorA = FP64.Sin((FP64.One - t) * omega) / sinOmega;
            FP64 factorB = FP64.Sin(t * omega) / sinOmega;

            // interpolate magnitude
            FP64 mag = FP64.LerpUnclamped(magA, magB, t);
            return (na * factorA + nb * factorB) * mag;
        }

        public static FPVector3 RotateTowards(FPVector3 current, FPVector3 target, FP64 maxRadiansDelta, FP64 maxMagnitudeDelta)
        {
            FP64 magCurrent = current.magnitude;
            FP64 magTarget = target.magnitude;

            if (magCurrent == FP64.Zero && magTarget == FP64.Zero)
                return Zero;
            if (magCurrent == FP64.Zero)
                return target.normalized * FP64.Min(maxMagnitudeDelta, magTarget);

            FPVector3 nc = current / magCurrent;
            FPVector3 nt = magTarget == FP64.Zero ? nc : target / magTarget;

            FP64 dot = FP64.Clamp(Dot(nc, nt), -FP64.One, FP64.One);
            FP64 angle = FP64.Acos(dot);

            FP64 newMag = FP64.MoveTowards(magCurrent, magTarget, maxMagnitudeDelta);

            if (angle <= FP64.Epsilon || angle <= maxRadiansDelta)
                return nt * newMag;

            FP64 t = maxRadiansDelta / angle;

            FP64 omega = angle;
            FP64 sinOmega = FP64.Sin(omega);

            FP64 factorA, factorB;
            if (sinOmega > FP64.FromRaw(100))
            {
                factorA = FP64.Sin((FP64.One - t) * omega) / sinOmega;
                factorB = FP64.Sin(t * omega) / sinOmega;
            }
            else
            {
                factorA = FP64.One - t;
                factorB = t;
            }

            FPVector3 dir = nc * factorA + nt * factorB;
            FP64 dirMag = dir.magnitude;
            if (dirMag > FP64.Zero)
                dir = dir / dirMag;

            return dir * newMag;
        }

        public static void OrthoNormalize(ref FPVector3 normal, ref FPVector3 tangent)
        {
            normal = normal.normalized;
            tangent = (tangent - Project(tangent, normal)).normalized;
        }

        public static void OrthoNormalize(ref FPVector3 normal, ref FPVector3 tangent, ref FPVector3 binormal)
        {
            normal = normal.normalized;
            tangent = (tangent - Project(tangent, normal)).normalized;
            binormal = (binormal - Project(binormal, normal) - Project(binormal, tangent)).normalized;
        }

        public static FPVector3 SmoothDamp(FPVector3 current, FPVector3 target, ref FPVector3 currentVelocity,
            FP64 smoothTime, FP64 maxSpeed, FP64 deltaTime)
        {
            FP64 two = FP64.FromInt(2);

            smoothTime = FP64.Max(smoothTime, FP64.FromRaw(FP64.ONE / 10000));
            FP64 omega = two / smoothTime;
            FP64 x = omega * deltaTime;

            FP64 x2 = x * x;
            FP64 x3 = x2 * x;
            FP64 x4 = x2 * x2;
            FP64 exp = FP64.One / (FP64.One + x + x2 * FP64.Half + x3 / FP64.FromInt(6) + x4 / FP64.FromInt(24));

            FPVector3 change = current - target;

            FP64 maxChange = maxSpeed * smoothTime;
            FP64 sqrMaxChange = maxChange * maxChange;
            FP64 sqrMag = change.sqrMagnitude;
            if (sqrMag > sqrMaxChange && sqrMaxChange > FP64.Zero)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                change = change / mag * maxChange;
            }
            FPVector3 clampedTarget = current - change;

            FPVector3 temp = (currentVelocity + change * omega) * deltaTime;
            currentVelocity = (currentVelocity - temp * omega) * exp;
            FPVector3 output = clampedTarget + (change + temp) * exp;

            // prevent overshoot
            FPVector3 toTarget = target - current;
            FPVector3 toOutput = output - target;
            if (Dot(toTarget, toOutput) > FP64.Zero)
            {
                output = target;
                currentVelocity = Zero;
            }

            return output;
        }

        // XY, XZ plane conversion
        public FPVector2 ToXY()
        {
            return new FPVector2(x, y);
        }

        public FPVector2 ToXZ()
        {
            return new FPVector2(x, z);
        }

        // IEquatable implementation
        public bool Equals(FPVector3 other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public override bool Equals(object obj)
        {
            return obj is FPVector3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(x, y, z);
        }

        public override string ToString()
        {
            return $"({x}, {y}, {z})";
        }
    }
}
