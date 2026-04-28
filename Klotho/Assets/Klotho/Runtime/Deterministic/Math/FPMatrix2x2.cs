using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 2x2 matrix implementation (row-major)
    /// </summary>
    [Serializable]
    public partial struct FPMatrix2x2 : IEquatable<FPMatrix2x2>
    {
        // row 0
        public FP64 m00;
        public FP64 m01;
        // row 1
        public FP64 m10;
        public FP64 m11;

        public static readonly FPMatrix2x2 Zero = new FPMatrix2x2(
            FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero);

        public static readonly FPMatrix2x2 Identity = new FPMatrix2x2(
            FP64.One, FP64.Zero,
            FP64.Zero, FP64.One);

        public FPMatrix2x2(FP64 m00, FP64 m01, FP64 m10, FP64 m11)
        {
            this.m00 = m00;
            this.m01 = m01;
            this.m10 = m10;
            this.m11 = m11;
        }

        /// <summary>
        /// Determinant: m00*m11 - m01*m10
        /// </summary>
        public FP64 determinant => m00 * m11 - m01 * m10;

        /// <summary>
        /// Transpose: swap m01 and m10
        /// </summary>
        public FPMatrix2x2 transpose => new FPMatrix2x2(m00, m10, m01, m11);

        /// <summary>
        /// Trace: m00 + m11
        /// </summary>
        public FP64 trace => m00 + m11;

        /// <summary>
        /// Build a 2D rotation matrix from an angle in degrees
        /// </summary>
        public static FPMatrix2x2 Rotate(FP64 degrees)
        {
            FP64 rad = degrees * FP64.Deg2Rad;
            FP64 c = FP64.Cos(rad);
            FP64 s = FP64.Sin(rad);
            return new FPMatrix2x2(c, -s, s, c);
        }

        /// <summary>
        /// Build a 2D scale matrix
        /// </summary>
        public static FPMatrix2x2 Scale(FP64 sx, FP64 sy)
        {
            return new FPMatrix2x2(sx, FP64.Zero, FP64.Zero, sy);
        }

        /// <summary>
        /// Build a 2D scale matrix from a vector
        /// </summary>
        public static FPMatrix2x2 Scale(FPVector2 s)
        {
            return new FPMatrix2x2(s.x, FP64.Zero, FP64.Zero, s.y);
        }

        /// <summary>
        /// Inverse: 1/det * adj(M). Returns Identity if singular.
        /// </summary>
        public static FPMatrix2x2 Inverse(FPMatrix2x2 m)
        {
            FP64 det = m.determinant;
            if (det == FP64.Zero)
                return Identity;

            FP64 invDet = FP64.One / det;
            return new FPMatrix2x2(
                m.m11 * invDet, -m.m01 * invDet,
                -m.m10 * invDet, m.m00 * invDet);
        }

        // operator overloads
        public static FPMatrix2x2 operator +(FPMatrix2x2 a, FPMatrix2x2 b)
        {
            return new FPMatrix2x2(
                a.m00 + b.m00, a.m01 + b.m01,
                a.m10 + b.m10, a.m11 + b.m11);
        }

        public static FPMatrix2x2 operator -(FPMatrix2x2 a, FPMatrix2x2 b)
        {
            return new FPMatrix2x2(
                a.m00 - b.m00, a.m01 - b.m01,
                a.m10 - b.m10, a.m11 - b.m11);
        }

        public static FPMatrix2x2 operator -(FPMatrix2x2 a)
        {
            return new FPMatrix2x2(-a.m00, -a.m01, -a.m10, -a.m11);
        }

        /// <summary>
        /// Matrix-matrix multiplication
        /// </summary>
        public static FPMatrix2x2 operator *(FPMatrix2x2 a, FPMatrix2x2 b)
        {
            return new FPMatrix2x2(
                a.m00 * b.m00 + a.m01 * b.m10,
                a.m00 * b.m01 + a.m01 * b.m11,
                a.m10 * b.m00 + a.m11 * b.m10,
                a.m10 * b.m01 + a.m11 * b.m11);
        }

        /// <summary>
        /// Matrix-scalar multiplication
        /// </summary>
        public static FPMatrix2x2 operator *(FPMatrix2x2 a, FP64 scalar)
        {
            return new FPMatrix2x2(
                a.m00 * scalar, a.m01 * scalar,
                a.m10 * scalar, a.m11 * scalar);
        }

        public static FPMatrix2x2 operator *(FP64 scalar, FPMatrix2x2 a)
        {
            return a * scalar;
        }

        /// <summary>
        /// Matrix-vector multiplication: M * v
        /// </summary>
        public static FPVector2 operator *(FPMatrix2x2 m, FPVector2 v)
        {
            return new FPVector2(
                m.m00 * v.x + m.m01 * v.y,
                m.m10 * v.x + m.m11 * v.y);
        }

        public static bool operator ==(FPMatrix2x2 a, FPMatrix2x2 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m10 == b.m10 && a.m11 == b.m11;
        }

        public static bool operator !=(FPMatrix2x2 a, FPMatrix2x2 b)
        {
            return !(a == b);
        }

        /// <summary>
        /// Linear interpolation between two matrices
        /// </summary>
        public static FPMatrix2x2 Lerp(FPMatrix2x2 a, FPMatrix2x2 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPMatrix2x2(
                FP64.LerpUnclamped(a.m00, b.m00, t),
                FP64.LerpUnclamped(a.m01, b.m01, t),
                FP64.LerpUnclamped(a.m10, b.m10, t),
                FP64.LerpUnclamped(a.m11, b.m11, t));
        }

        // row/column access
        public FPVector2 GetRow(int index)
        {
            switch (index)
            {
                case 0: return new FPVector2(m00, m01);
                case 1: return new FPVector2(m10, m11);
                default: throw new IndexOutOfRangeException("Row index must be 0 or 1");
            }
        }

        public FPVector2 GetColumn(int index)
        {
            switch (index)
            {
                case 0: return new FPVector2(m00, m10);
                case 1: return new FPVector2(m01, m11);
                default: throw new IndexOutOfRangeException("Column index must be 0 or 1");
            }
        }

        // IEquatable implementation
        public bool Equals(FPMatrix2x2 other)
        {
            return m00 == other.m00 && m01 == other.m01 && m10 == other.m10 && m11 == other.m11;
        }

        public override bool Equals(object obj)
        {
            return obj is FPMatrix2x2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(m00, m01, m10, m11);
        }

        public override string ToString()
        {
            return $"[({m00}, {m01}), ({m10}, {m11})]";
        }
    }
}
