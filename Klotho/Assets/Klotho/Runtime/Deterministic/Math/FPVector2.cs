using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 2D vector implementation
    /// </summary>
    [Primitive]
    [Serializable]
    public partial struct FPVector2 : IEquatable<FPVector2>
    {
        public FP64 x;
        public FP64 y;

        public static readonly FPVector2 Zero = new FPVector2(FP64.Zero, FP64.Zero);
        public static readonly FPVector2 One = new FPVector2(FP64.One, FP64.One);
        public static readonly FPVector2 Up = new FPVector2(FP64.Zero, FP64.One);
        public static readonly FPVector2 Down = new FPVector2(FP64.Zero, -FP64.One);
        public static readonly FPVector2 Left = new FPVector2(-FP64.One, FP64.Zero);
        public static readonly FPVector2 Right = new FPVector2(FP64.One, FP64.Zero);

        public FPVector2(FP64 x, FP64 y)
        {
            this.x = x;
            this.y = y;
        }

        public FPVector2(int x, int y)
        {
            this.x = FP64.FromInt(x);
            this.y = FP64.FromInt(y);
        }

        public FPVector2(float x, float y)
        {
            this.x = FP64.FromFloat(x);
            this.y = FP64.FromFloat(y);
        }

        public FP64 sqrMagnitude => x * x + y * y;

        public FP64 magnitude
        {
            get
            {
                FP64 ax = FP64.Abs(x);
                FP64 ay = FP64.Abs(y);
                FP64 max = FP64.Max(ax, ay);
                if (max == FP64.Zero)
                    return FP64.Zero;
                FP64 nx = ax / max;
                FP64 ny = ay / max;
                return max * FP64.Sqrt(nx * nx + ny * ny);
            }
        }

        public FPVector2 normalized
        {
            get
            {
                FP64 mag = magnitude;
                if (mag == FP64.Zero)
                    return Zero;
                return new FPVector2(x / mag, y / mag);
            }
        }

        /// <summary>
        /// Convert to a float array [x, y]
        /// </summary>
        public float[] ToFloatArray()
        {
            return new float[] { x.ToFloat(), y.ToFloat() };
        }

        // operator overloads
        public static FPVector2 operator +(FPVector2 a, FPVector2 b)
        {
            return new FPVector2(a.x + b.x, a.y + b.y);
        }

        public static FPVector2 operator -(FPVector2 a, FPVector2 b)
        {
            return new FPVector2(a.x - b.x, a.y - b.y);
        }

        public static FPVector2 operator -(FPVector2 a)
        {
            return new FPVector2(-a.x, -a.y);
        }

        public static FPVector2 operator *(FPVector2 a, FP64 scalar)
        {
            return new FPVector2(a.x * scalar, a.y * scalar);
        }

        public static FPVector2 operator *(FP64 scalar, FPVector2 a)
        {
            return new FPVector2(a.x * scalar, a.y * scalar);
        }

        public static FPVector2 operator /(FPVector2 a, FP64 scalar)
        {
            return new FPVector2(a.x / scalar, a.y / scalar);
        }

        public static bool operator ==(FPVector2 a, FPVector2 b)
        {
            return a.x == b.x && a.y == b.y;
        }

        public static bool operator !=(FPVector2 a, FPVector2 b)
        {
            return !(a == b);
        }

        // static methods
        public static FP64 Dot(FPVector2 a, FPVector2 b)
        {
            return a.x * b.x + a.y * b.y;
        }

        public static FP64 Cross(FPVector2 a, FPVector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        public static FP64 Distance(FPVector2 a, FPVector2 b)
        {
            return (a - b).magnitude;
        }

        public static FP64 SqrDistance(FPVector2 a, FPVector2 b)
        {
            return (a - b).sqrMagnitude;
        }

        public static FPVector2 Lerp(FPVector2 a, FPVector2 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPVector2(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t)
            );
        }

        public static FPVector2 LerpUnclamped(FPVector2 a, FPVector2 b, FP64 t)
        {
            return new FPVector2(
                FP64.LerpUnclamped(a.x, b.x, t),
                FP64.LerpUnclamped(a.y, b.y, t)
            );
        }

        public static FPVector2 MoveTowards(FPVector2 current, FPVector2 target, FP64 maxDistanceDelta)
        {
            FPVector2 diff = target - current;
            FP64 dist = diff.magnitude;

            if (dist <= maxDistanceDelta || dist == FP64.Zero)
                return target;

            return current + diff / dist * maxDistanceDelta;
        }

        public static FPVector2 Reflect(FPVector2 direction, FPVector2 normal)
        {
            FP64 dot2 = FP64.FromInt(2) * Dot(direction, normal);
            return direction - normal * dot2;
        }

        public static FPVector2 Perpendicular(FPVector2 direction)
        {
            return new FPVector2(-direction.y, direction.x);
        }

        public static FP64 Angle(FPVector2 from, FPVector2 to)
        {
            FPVector2 fn = from.normalized;
            FPVector2 tn = to.normalized;
            if (fn == Zero || tn == Zero)
                return FP64.Zero;

            FP64 dot = FP64.Clamp(Dot(fn, tn), -FP64.One, FP64.One);
            return FP64.Acos(dot) * FP64.Rad2Deg;
        }

        public static FP64 SignedAngle(FPVector2 from, FPVector2 to)
        {
            FP64 angle = Angle(from, to);
            FP64 cross = Cross(from, to);
            return angle * FP64.FromInt(FP64.Sign(cross) == 0 ? 1 : FP64.Sign(cross));
        }

        public static FPVector2 ClampMagnitude(FPVector2 vector, FP64 maxLength)
        {
            FP64 sqrMag = vector.sqrMagnitude;
            if (sqrMag > maxLength * maxLength)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                return vector / mag * maxLength;
            }
            return vector;
        }

        public static FPVector2 Min(FPVector2 a, FPVector2 b)
        {
            return new FPVector2(FP64.Min(a.x, b.x), FP64.Min(a.y, b.y));
        }

        public static FPVector2 Max(FPVector2 a, FPVector2 b)
        {
            return new FPVector2(FP64.Max(a.x, b.x), FP64.Max(a.y, b.y));
        }

        public static FPVector2 SmoothDamp(FPVector2 current, FPVector2 target, ref FPVector2 currentVelocity,
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

            FPVector2 change = current - target;

            FP64 maxChange = maxSpeed * smoothTime;
            FP64 sqrMaxChange = maxChange * maxChange;
            FP64 sqrMag = change.sqrMagnitude;
            if (sqrMag > sqrMaxChange && sqrMaxChange > FP64.Zero)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                change = change / mag * maxChange;
            }
            FPVector2 clampedTarget = current - change;

            FPVector2 temp = (currentVelocity + change * omega) * deltaTime;
            currentVelocity = (currentVelocity - temp * omega) * exp;
            FPVector2 output = clampedTarget + (change + temp) * exp;

            // prevent overshoot
            FPVector2 toTarget = target - current;
            FPVector2 toOutput = output - target;
            if (Dot(toTarget, toOutput) > FP64.Zero)
            {
                output = target;
                currentVelocity = Zero;
            }

            return output;
        }

        // IEquatable implementation
        public bool Equals(FPVector2 other)
        {
            return x == other.x && y == other.y;
        }

        public override bool Equals(object obj)
        {
            return obj is FPVector2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(x, y);
        }

        public override string ToString()
        {
            return $"({x}, {y})";
        }
    }
}
