using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Deterministic.Math
{
    [Primitive]
    [Serializable]
    public partial struct FPQuaternion : IEquatable<FPQuaternion>
    {
        public FP64 x;
        public FP64 y;
        public FP64 z;
        public FP64 w;

        public static readonly FPQuaternion Identity = new FPQuaternion(FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);

        public FPQuaternion(FP64 x, FP64 y, FP64 z, FP64 w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        #region Properties

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

        public FPQuaternion normalized
        {
            get
            {
                FP64 mag = magnitude;
                if (mag == FP64.Zero)
                    return Identity;
                return new FPQuaternion(x / mag, y / mag, z / mag, w / mag);
            }
        }

        public FPQuaternion conjugate => new FPQuaternion(-x, -y, -z, w);

        public FPVector3 eulerAngles
        {
            get
            {
                FP64 two = FP64.FromInt(2);

                // sinX = 2(wx - yz)
                FP64 sinX = two * (w * x - y * z);

                FP64 ex, ey, ez;

                // gimbal lock threshold
                FP64 threshold = FP64.One - FP64.FromRaw(429497);
                if (FP64.Abs(sinX) > threshold)
                {
                    ex = sinX > FP64.Zero ? FP64.HalfPi : -FP64.HalfPi;
                    ey = FP64.Atan2(two * (x * z - w * y),
                                     FP64.One - two * (y * y + z * z));
                    ez = FP64.Zero;
                }
                else
                {
                    // asin(sinX) = atan2(sinX, sqrt(1 - sinX^2))
                    ex = FP64.Atan2(sinX, FP64.Sqrt(FP64.One - sinX * sinX));
                    ey = FP64.Atan2(two * (w * y + x * z),
                                     FP64.One - two * (x * x + y * y));
                    ez = FP64.Atan2(two * (w * z + x * y),
                                     FP64.One - two * (x * x + z * z));
                }

                ex = ex * FP64.Rad2Deg;
                ey = ey * FP64.Rad2Deg;
                ez = ez * FP64.Rad2Deg;

                FP64 full = FP64.FromInt(360);
                if (ex < FP64.Zero) ex = ex + full;
                if (ey < FP64.Zero) ey = ey + full;
                if (ez < FP64.Zero) ez = ez + full;

                return new FPVector3(ex, ey, ez);
            }
        }

        #endregion

        #region Operators

        public static FPQuaternion operator *(FPQuaternion a, FPQuaternion b)
        {
            return new FPQuaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
            );
        }

        public static FPVector3 operator *(FPQuaternion q, FPVector3 v)
        {
            FP64 two = FP64.FromInt(2);

            FP64 tx = two * (q.y * v.z - q.z * v.y);
            FP64 ty = two * (q.z * v.x - q.x * v.z);
            FP64 tz = two * (q.x * v.y - q.y * v.x);

            return new FPVector3(
                v.x + q.w * tx + (q.y * tz - q.z * ty),
                v.y + q.w * ty + (q.z * tx - q.x * tz),
                v.z + q.w * tz + (q.x * ty - q.y * tx)
            );
        }

        public static bool operator ==(FPQuaternion a, FPQuaternion b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        public static bool operator !=(FPQuaternion a, FPQuaternion b)
        {
            return !(a == b);
        }

        #endregion

        #region Static Methods

        public static FP64 Dot(FPQuaternion a, FPQuaternion b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        }

        public static FP64 Angle(FPQuaternion a, FPQuaternion b)
        {
            FP64 dot = Dot(a, b);
            FP64 absDot = FP64.Abs(dot);
            if (absDot > FP64.One) absDot = FP64.One;
            return FP64.Acos(absDot) * FP64.FromInt(2) * FP64.Rad2Deg;
        }

        public static FPQuaternion Inverse(FPQuaternion q)
        {
            FP64 sqrMag = q.sqrMagnitude;
            if (sqrMag == FP64.Zero)
                return Identity;
            return new FPQuaternion(-q.x / sqrMag, -q.y / sqrMag, -q.z / sqrMag, q.w / sqrMag);
        }

        public static FPQuaternion Normalize(FPQuaternion q)
        {
            return q.normalized;
        }

        public static bool IsIdentity(FPQuaternion q)
        {
            return q.x == FP64.Zero && q.y == FP64.Zero && q.z == FP64.Zero && q.w == FP64.One;
        }

        public static bool IsZero(FPQuaternion q)
        {
            return q.x == FP64.Zero && q.y == FP64.Zero && q.z == FP64.Zero && q.w == FP64.Zero;
        }

        #endregion

        #region Factory Methods

        public static FPQuaternion AngleAxis(FP64 angle, FPVector3 axis)
        {
            FPVector3 normalizedAxis = axis.normalized;
            if (normalizedAxis == FPVector3.Zero)
                return Identity;

            FP64 halfAngleRad = angle * FP64.Deg2Rad * FP64.Half;
            FP64 sinHalf = FP64.Sin(halfAngleRad);
            FP64 cosHalf = FP64.Cos(halfAngleRad);

            return new FPQuaternion(
                normalizedAxis.x * sinHalf,
                normalizedAxis.y * sinHalf,
                normalizedAxis.z * sinHalf,
                cosHalf
            );
        }

        public static FPQuaternion Euler(FP64 x, FP64 y, FP64 z)
        {
            FP64 halfX = x * FP64.Deg2Rad * FP64.Half;
            FP64 halfY = y * FP64.Deg2Rad * FP64.Half;
            FP64 halfZ = z * FP64.Deg2Rad * FP64.Half;

            FP64 sinX = FP64.Sin(halfX);
            FP64 cosX = FP64.Cos(halfX);
            FP64 sinY = FP64.Sin(halfY);
            FP64 cosY = FP64.Cos(halfY);
            FP64 sinZ = FP64.Sin(halfZ);
            FP64 cosZ = FP64.Cos(halfZ);

            // q = qY * qX * qZ (Unity convention)
            return new FPQuaternion(
                cosY * sinX * cosZ + sinY * cosX * sinZ,
                sinY * cosX * cosZ - cosY * sinX * sinZ,
                cosY * cosX * sinZ - sinY * sinX * cosZ,
                cosY * cosX * cosZ + sinY * sinX * sinZ
            );
        }

        public static FPQuaternion Euler(FPVector3 euler)
        {
            return Euler(euler.x, euler.y, euler.z);
        }

        public static FPQuaternion LookRotation(FPVector3 forward, FPVector3 upwards)
        {
            FPVector3 fwd = forward.normalized;
            if (fwd == FPVector3.Zero)
                return Identity;

            FPVector3 right = FPVector3.Cross(upwards, fwd).normalized;
            if (right == FPVector3.Zero)
            {
                right = FPVector3.Cross(FPVector3.Right, fwd).normalized;
                if (right == FPVector3.Zero)
                    right = FPVector3.Cross(FPVector3.Forward, fwd).normalized;
            }
            FPVector3 up = FPVector3.Cross(fwd, right);

            return QuaternionFromBasis(right, up, fwd);
        }

        public static FPQuaternion LookRotation(FPVector3 forward)
        {
            return LookRotation(forward, FPVector3.Up);
        }

        public static FPQuaternion FromToRotation(FPVector3 from, FPVector3 to)
        {
            FPVector3 fn = from.normalized;
            FPVector3 tn = to.normalized;

            FP64 dot = FPVector3.Dot(fn, tn);

            if (dot >= FP64.One)
                return Identity;

            if (dot <= -FP64.One)
            {
                FPVector3 axis = FPVector3.Cross(FPVector3.Right, fn);
                if (axis.sqrMagnitude < FP64.Epsilon)
                    axis = FPVector3.Cross(FPVector3.Up, fn);
                axis = axis.normalized;
                return new FPQuaternion(axis.x, axis.y, axis.z, FP64.Zero);
            }

            FPVector3 half = (fn + tn).normalized;
            FPVector3 cross = FPVector3.Cross(fn, half);
            FP64 w = FPVector3.Dot(fn, half);
            return new FPQuaternion(cross.x, cross.y, cross.z, w).normalized;
        }

        #endregion

        #region Interpolation

        public static FPQuaternion Lerp(FPQuaternion a, FPQuaternion b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return LerpUnclamped(a, b, t);
        }

        public static FPQuaternion LerpUnclamped(FPQuaternion a, FPQuaternion b, FP64 t)
        {
            FP64 dot = Dot(a, b);
            FP64 oneMinusT = FP64.One - t;

            FPQuaternion result;
            if (dot < FP64.Zero)
            {
                result = new FPQuaternion(
                    oneMinusT * a.x - t * b.x,
                    oneMinusT * a.y - t * b.y,
                    oneMinusT * a.z - t * b.z,
                    oneMinusT * a.w - t * b.w
                );
            }
            else
            {
                result = new FPQuaternion(
                    oneMinusT * a.x + t * b.x,
                    oneMinusT * a.y + t * b.y,
                    oneMinusT * a.z + t * b.z,
                    oneMinusT * a.w + t * b.w
                );
            }
            return result.normalized;
        }

        public static FPQuaternion Slerp(FPQuaternion a, FPQuaternion b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return SlerpUnclamped(a, b, t);
        }

        public static FPQuaternion SlerpUnclamped(FPQuaternion a, FPQuaternion b, FP64 t)
        {
            FP64 cosOmega = Dot(a, b);

            bool negated = false;
            if (cosOmega < FP64.Zero)
            {
                cosOmega = -cosOmega;
                negated = true;
            }

            FP64 k0, k1;

            FP64 threshold = FP64.One - FP64.FromRaw(429497);
            if (cosOmega > threshold)
            {
                k0 = FP64.One - t;
                k1 = t;
            }
            else
            {
                FP64 omega = FP64.Acos(cosOmega);
                FP64 sinOmega = FP64.Sin(omega);
                k0 = FP64.Sin((FP64.One - t) * omega) / sinOmega;
                k1 = FP64.Sin(t * omega) / sinOmega;
            }

            if (negated) k1 = -k1;

            return new FPQuaternion(
                k0 * a.x + k1 * b.x,
                k0 * a.y + k1 * b.y,
                k0 * a.z + k1 * b.z,
                k0 * a.w + k1 * b.w
            );
        }

        public static FPQuaternion RotateTowards(FPQuaternion from, FPQuaternion to, FP64 maxDegreesDelta)
        {
            FP64 angle = Angle(from, to);
            if (angle == FP64.Zero)
                return to;
            FP64 t = FP64.Min(FP64.One, maxDegreesDelta / angle);
            return SlerpUnclamped(from, to, t);
        }

        #endregion

        #region Private Helpers

        private static FPQuaternion QuaternionFromBasis(FPVector3 right, FPVector3 up, FPVector3 forward)
        {
            FP64 m00 = right.x;    FP64 m01 = up.x;    FP64 m02 = forward.x;
            FP64 m10 = right.y;    FP64 m11 = up.y;    FP64 m12 = forward.y;
            FP64 m20 = right.z;    FP64 m21 = up.z;    FP64 m22 = forward.z;

            FP64 trace = m00 + m11 + m22;
            FP64 four = FP64.FromInt(4);
            FP64 two = FP64.FromInt(2);

            FPQuaternion q;
            if (trace > FP64.Zero)
            {
                FP64 s = FP64.Sqrt(trace + FP64.One) * two;
                q = new FPQuaternion(
                    (m21 - m12) / s,
                    (m02 - m20) / s,
                    (m10 - m01) / s,
                    s / four
                );
            }
            else if (m00 > m11 && m00 > m22)
            {
                FP64 s = FP64.Sqrt(FP64.One + m00 - m11 - m22) * two;
                q = new FPQuaternion(
                    s / four,
                    (m01 + m10) / s,
                    (m02 + m20) / s,
                    (m21 - m12) / s
                );
            }
            else if (m11 > m22)
            {
                FP64 s = FP64.Sqrt(FP64.One + m11 - m00 - m22) * two;
                q = new FPQuaternion(
                    (m01 + m10) / s,
                    s / four,
                    (m12 + m21) / s,
                    (m02 - m20) / s
                );
            }
            else
            {
                FP64 s = FP64.Sqrt(FP64.One + m22 - m00 - m11) * two;
                q = new FPQuaternion(
                    (m02 + m20) / s,
                    (m12 + m21) / s,
                    s / four,
                    (m10 - m01) / s
                );
            }
            return q.normalized;
        }

        #endregion

        #region IEquatable, GetHashCode, ToString

        public bool Equals(FPQuaternion other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        public override bool Equals(object obj)
        {
            return obj is FPQuaternion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(x, y, z, w);
        }

        public override string ToString()
        {
            return $"({x}, {y}, {z}, {w})";
        }

        #endregion
    }
}
