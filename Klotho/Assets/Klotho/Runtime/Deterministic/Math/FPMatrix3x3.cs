using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 3x3 matrix implementation (row-major)
    /// </summary>
    [Serializable]
    public partial struct FPMatrix3x3 : IEquatable<FPMatrix3x3>
    {
        // row 0
        public FP64 m00;
        public FP64 m01;
        public FP64 m02;
        // row 1
        public FP64 m10;
        public FP64 m11;
        public FP64 m12;
        // row 2
        public FP64 m20;
        public FP64 m21;
        public FP64 m22;

        public static readonly FPMatrix3x3 Zero = new FPMatrix3x3(
            FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero);

        public static readonly FPMatrix3x3 Identity = new FPMatrix3x3(
            FP64.One, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.One, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.One);

        public FPMatrix3x3(
            FP64 m00, FP64 m01, FP64 m02,
            FP64 m10, FP64 m11, FP64 m12,
            FP64 m20, FP64 m21, FP64 m22)
        {
            this.m00 = m00; this.m01 = m01; this.m02 = m02;
            this.m10 = m10; this.m11 = m11; this.m12 = m12;
            this.m20 = m20; this.m21 = m21; this.m22 = m22;
        }

        /// <summary>
        /// Determinant (Sarrus / cofactor expansion)
        /// </summary>
        public FP64 determinant =>
            m00 * (m11 * m22 - m12 * m21) -
            m01 * (m10 * m22 - m12 * m20) +
            m02 * (m10 * m21 - m11 * m20);

        public FPMatrix3x3 transpose => new FPMatrix3x3(
            m00, m10, m20,
            m01, m11, m21,
            m02, m12, m22);

        /// <summary>
        /// Trace: m00 + m11 + m22
        /// </summary>
        public FP64 trace => m00 + m11 + m22;

        #region Factory Methods

        /// <summary>
        /// Build a 3D rotation matrix around the X axis (degrees)
        /// </summary>
        public static FPMatrix3x3 RotateX(FP64 degrees)
        {
            FP64 rad = degrees * FP64.Deg2Rad;
            FP64 c = FP64.Cos(rad);
            FP64 s = FP64.Sin(rad);
            return new FPMatrix3x3(
                FP64.One, FP64.Zero, FP64.Zero,
                FP64.Zero, c, -s,
                FP64.Zero, s, c);
        }

        /// <summary>
        /// Build a 3D rotation matrix around the Y axis (degrees)
        /// </summary>
        public static FPMatrix3x3 RotateY(FP64 degrees)
        {
            FP64 rad = degrees * FP64.Deg2Rad;
            FP64 c = FP64.Cos(rad);
            FP64 s = FP64.Sin(rad);
            return new FPMatrix3x3(
                c, FP64.Zero, s,
                FP64.Zero, FP64.One, FP64.Zero,
                -s, FP64.Zero, c);
        }

        /// <summary>
        /// Build a 3D rotation matrix around the Z axis (degrees)
        /// </summary>
        public static FPMatrix3x3 RotateZ(FP64 degrees)
        {
            FP64 rad = degrees * FP64.Deg2Rad;
            FP64 c = FP64.Cos(rad);
            FP64 s = FP64.Sin(rad);
            return new FPMatrix3x3(
                c, -s, FP64.Zero,
                s, c, FP64.Zero,
                FP64.Zero, FP64.Zero, FP64.One);
        }

        /// <summary>
        /// Build a rotation matrix around an arbitrary axis (degrees)
        /// </summary>
        public static FPMatrix3x3 RotateAxis(FP64 degrees, FPVector3 axis)
        {
            FPVector3 n = axis.normalized;
            if (n == FPVector3.Zero)
                return Identity;

            FP64 rad = degrees * FP64.Deg2Rad;
            FP64 c = FP64.Cos(rad);
            FP64 s = FP64.Sin(rad);
            FP64 t = FP64.One - c;

            return new FPMatrix3x3(
                t * n.x * n.x + c,         t * n.x * n.y - s * n.z,   t * n.x * n.z + s * n.y,
                t * n.x * n.y + s * n.z,   t * n.y * n.y + c,         t * n.y * n.z - s * n.x,
                t * n.x * n.z - s * n.y,   t * n.y * n.z + s * n.x,   t * n.z * n.z + c);
        }

        /// <summary>
        /// Build a 3D scale matrix
        /// </summary>
        public static FPMatrix3x3 Scale(FP64 sx, FP64 sy, FP64 sz)
        {
            return new FPMatrix3x3(
                sx, FP64.Zero, FP64.Zero,
                FP64.Zero, sy, FP64.Zero,
                FP64.Zero, FP64.Zero, sz);
        }

        /// <summary>
        /// Build a 3D scale matrix from a vector
        /// </summary>
        public static FPMatrix3x3 Scale(FPVector3 s)
        {
            return Scale(s.x, s.y, s.z);
        }

        /// <summary>
        /// Build a rotation matrix from a quaternion
        /// </summary>
        public static FPMatrix3x3 FromQuaternion(FPQuaternion q)
        {
            FP64 two = FP64.FromInt(2);
            FP64 xx = q.x * q.x;
            FP64 yy = q.y * q.y;
            FP64 zz = q.z * q.z;
            FP64 xy = q.x * q.y;
            FP64 xz = q.x * q.z;
            FP64 yz = q.y * q.z;
            FP64 wx = q.w * q.x;
            FP64 wy = q.w * q.y;
            FP64 wz = q.w * q.z;

            return new FPMatrix3x3(
                FP64.One - two * (yy + zz),   two * (xy - wz),             two * (xz + wy),
                two * (xy + wz),               FP64.One - two * (xx + zz),   two * (yz - wx),
                two * (xz - wy),               two * (yz + wx),             FP64.One - two * (xx + yy));
        }

        /// <summary>
        /// Convert this rotation matrix to a quaternion (Shepperd's method)
        /// </summary>
        public FPQuaternion ToQuaternion()
        {
            FP64 tr = trace;
            FP64 four = FP64.FromInt(4);
            FP64 two = FP64.FromInt(2);

            FP64 qx, qy, qz, qw;
            if (tr > FP64.Zero)
            {
                FP64 s = FP64.Sqrt(tr + FP64.One) * two;
                qw = s / four;
                qx = (m21 - m12) / s;
                qy = (m02 - m20) / s;
                qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                FP64 s = FP64.Sqrt(FP64.One + m00 - m11 - m22) * two;
                qw = (m21 - m12) / s;
                qx = s / four;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                FP64 s = FP64.Sqrt(FP64.One + m11 - m00 - m22) * two;
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = s / four;
                qz = (m12 + m21) / s;
            }
            else
            {
                FP64 s = FP64.Sqrt(FP64.One + m22 - m00 - m11) * two;
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = s / four;
            }

            FP64 mag = FP64.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (mag > FP64.Zero)
                return new FPQuaternion(qx / mag, qy / mag, qz / mag, qw / mag);
            return FPQuaternion.Identity;
        }

        #endregion

        #region Inverse

        /// <summary>
        /// Compute the inverse via adjugate / determinant. Returns Identity if singular.
        /// </summary>
        public static FPMatrix3x3 Inverse(FPMatrix3x3 m)
        {
            FP64 det = m.determinant;
            if (det == FP64.Zero)
                return Identity;

            FP64 invDet = FP64.One / det;

            return new FPMatrix3x3(
                (m.m11 * m.m22 - m.m12 * m.m21) * invDet,
                (m.m02 * m.m21 - m.m01 * m.m22) * invDet,
                (m.m01 * m.m12 - m.m02 * m.m11) * invDet,
                (m.m12 * m.m20 - m.m10 * m.m22) * invDet,
                (m.m00 * m.m22 - m.m02 * m.m20) * invDet,
                (m.m02 * m.m10 - m.m00 * m.m12) * invDet,
                (m.m10 * m.m21 - m.m11 * m.m20) * invDet,
                (m.m01 * m.m20 - m.m00 * m.m21) * invDet,
                (m.m00 * m.m11 - m.m01 * m.m10) * invDet);
        }

        #endregion

        #region Operators

        public static FPMatrix3x3 operator +(FPMatrix3x3 a, FPMatrix3x3 b)
        {
            return new FPMatrix3x3(
                a.m00 + b.m00, a.m01 + b.m01, a.m02 + b.m02,
                a.m10 + b.m10, a.m11 + b.m11, a.m12 + b.m12,
                a.m20 + b.m20, a.m21 + b.m21, a.m22 + b.m22);
        }

        public static FPMatrix3x3 operator -(FPMatrix3x3 a, FPMatrix3x3 b)
        {
            return new FPMatrix3x3(
                a.m00 - b.m00, a.m01 - b.m01, a.m02 - b.m02,
                a.m10 - b.m10, a.m11 - b.m11, a.m12 - b.m12,
                a.m20 - b.m20, a.m21 - b.m21, a.m22 - b.m22);
        }

        public static FPMatrix3x3 operator -(FPMatrix3x3 a)
        {
            return new FPMatrix3x3(
                -a.m00, -a.m01, -a.m02,
                -a.m10, -a.m11, -a.m12,
                -a.m20, -a.m21, -a.m22);
        }

        /// <summary>
        /// Matrix-matrix multiplication (27 multiplies + 18 adds)
        /// </summary>
        public static FPMatrix3x3 operator *(FPMatrix3x3 a, FPMatrix3x3 b)
        {
            return new FPMatrix3x3(
                a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20,
                a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21,
                a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22,

                a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20,
                a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21,
                a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22,

                a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20,
                a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21,
                a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22);
        }

        /// <summary>
        /// Matrix-scalar multiplication
        /// </summary>
        public static FPMatrix3x3 operator *(FPMatrix3x3 a, FP64 scalar)
        {
            return new FPMatrix3x3(
                a.m00 * scalar, a.m01 * scalar, a.m02 * scalar,
                a.m10 * scalar, a.m11 * scalar, a.m12 * scalar,
                a.m20 * scalar, a.m21 * scalar, a.m22 * scalar);
        }

        public static FPMatrix3x3 operator *(FP64 scalar, FPMatrix3x3 a)
        {
            return a * scalar;
        }

        /// <summary>
        /// Matrix-vector multiplication: M * v
        /// </summary>
        public static FPVector3 operator *(FPMatrix3x3 m, FPVector3 v)
        {
            return new FPVector3(
                m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
                m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
                m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);
        }

        public static bool operator ==(FPMatrix3x3 a, FPMatrix3x3 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 &&
                   a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 &&
                   a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22;
        }

        public static bool operator !=(FPMatrix3x3 a, FPMatrix3x3 b)
        {
            return !(a == b);
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Linear interpolation between two matrices
        /// </summary>
        public static FPMatrix3x3 Lerp(FPMatrix3x3 a, FPMatrix3x3 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPMatrix3x3(
                FP64.LerpUnclamped(a.m00, b.m00, t), FP64.LerpUnclamped(a.m01, b.m01, t), FP64.LerpUnclamped(a.m02, b.m02, t),
                FP64.LerpUnclamped(a.m10, b.m10, t), FP64.LerpUnclamped(a.m11, b.m11, t), FP64.LerpUnclamped(a.m12, b.m12, t),
                FP64.LerpUnclamped(a.m20, b.m20, t), FP64.LerpUnclamped(a.m21, b.m21, t), FP64.LerpUnclamped(a.m22, b.m22, t));
        }

        #endregion

        #region Row/Column Access

        public FPVector3 GetRow(int index)
        {
            switch (index)
            {
                case 0: return new FPVector3(m00, m01, m02);
                case 1: return new FPVector3(m10, m11, m12);
                case 2: return new FPVector3(m20, m21, m22);
                default: throw new IndexOutOfRangeException("Row index must be 0, 1, or 2");
            }
        }

        public FPVector3 GetColumn(int index)
        {
            switch (index)
            {
                case 0: return new FPVector3(m00, m10, m20);
                case 1: return new FPVector3(m01, m11, m21);
                case 2: return new FPVector3(m02, m12, m22);
                default: throw new IndexOutOfRangeException("Column index must be 0, 1, or 2");
            }
        }

        #endregion

        #region IEquatable, GetHashCode, ToString

        public bool Equals(FPMatrix3x3 other)
        {
            return this == other;
        }

        public override bool Equals(object obj)
        {
            return obj is FPMatrix3x3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(m00); h.Add(m01); h.Add(m02);
            h.Add(m10); h.Add(m11); h.Add(m12);
            h.Add(m20); h.Add(m21); h.Add(m22);
            return h.ToHashCode();
        }

        public override string ToString()
        {
            return $"[({m00}, {m01}, {m02}), ({m10}, {m11}, {m12}), ({m20}, {m21}, {m22})]";
        }

        #endregion
    }
}
