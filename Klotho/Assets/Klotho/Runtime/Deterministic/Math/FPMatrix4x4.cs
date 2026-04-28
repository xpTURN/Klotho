using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Fixed-point 4x4 matrix implementation (row-major)
    /// </summary>
    [Serializable]
    public partial struct FPMatrix4x4 : IEquatable<FPMatrix4x4>
    {
        // row 0
        public FP64 m00; public FP64 m01; public FP64 m02; public FP64 m03;
        // row 1
        public FP64 m10; public FP64 m11; public FP64 m12; public FP64 m13;
        // row 2
        public FP64 m20; public FP64 m21; public FP64 m22; public FP64 m23;
        // row 3
        public FP64 m30; public FP64 m31; public FP64 m32; public FP64 m33;

        public static readonly FPMatrix4x4 Zero = new FPMatrix4x4(
            FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero);

        public static readonly FPMatrix4x4 Identity = new FPMatrix4x4(
            FP64.One, FP64.Zero, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.One, FP64.Zero, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.One, FP64.Zero,
            FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);

        public FPMatrix4x4(
            FP64 m00, FP64 m01, FP64 m02, FP64 m03,
            FP64 m10, FP64 m11, FP64 m12, FP64 m13,
            FP64 m20, FP64 m21, FP64 m22, FP64 m23,
            FP64 m30, FP64 m31, FP64 m32, FP64 m33)
        {
            this.m00 = m00; this.m01 = m01; this.m02 = m02; this.m03 = m03;
            this.m10 = m10; this.m11 = m11; this.m12 = m12; this.m13 = m13;
            this.m20 = m20; this.m21 = m21; this.m22 = m22; this.m23 = m23;
            this.m30 = m30; this.m31 = m31; this.m32 = m32; this.m33 = m33;
        }

        #region Properties

        /// <summary>
        /// Compute the determinant via cofactor expansion along the first row
        /// </summary>
        public FP64 determinant
        {
            get
            {
                FP64 c00 = m11 * (m22 * m33 - m23 * m32) - m12 * (m21 * m33 - m23 * m31) + m13 * (m21 * m32 - m22 * m31);
                FP64 c01 = m10 * (m22 * m33 - m23 * m32) - m12 * (m20 * m33 - m23 * m30) + m13 * (m20 * m32 - m22 * m30);
                FP64 c02 = m10 * (m21 * m33 - m23 * m31) - m11 * (m20 * m33 - m23 * m30) + m13 * (m20 * m31 - m21 * m30);
                FP64 c03 = m10 * (m21 * m32 - m22 * m31) - m11 * (m20 * m32 - m22 * m30) + m12 * (m20 * m31 - m21 * m30);
                return m00 * c00 - m01 * c01 + m02 * c02 - m03 * c03;
            }
        }

        public FPMatrix4x4 transpose => new FPMatrix4x4(
            m00, m10, m20, m30,
            m01, m11, m21, m31,
            m02, m12, m22, m32,
            m03, m13, m23, m33);

        public FP64 trace => m00 + m11 + m22 + m33;

        #endregion

        #region Factory Methods — TRS

        /// <summary>
        /// Build a translation matrix
        /// </summary>
        public static FPMatrix4x4 Translate(FPVector3 t)
        {
            return new FPMatrix4x4(
                FP64.One, FP64.Zero, FP64.Zero, t.x,
                FP64.Zero, FP64.One, FP64.Zero, t.y,
                FP64.Zero, FP64.Zero, FP64.One, t.z,
                FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);
        }

        /// <summary>
        /// Build a rotation matrix from a quaternion
        /// </summary>
        public static FPMatrix4x4 Rotate(FPQuaternion q)
        {
            FP64 two = FP64.FromInt(2);
            FP64 xx = q.x * q.x; FP64 yy = q.y * q.y; FP64 zz = q.z * q.z;
            FP64 xy = q.x * q.y; FP64 xz = q.x * q.z; FP64 yz = q.y * q.z;
            FP64 wx = q.w * q.x; FP64 wy = q.w * q.y; FP64 wz = q.w * q.z;

            return new FPMatrix4x4(
                FP64.One - two * (yy + zz), two * (xy - wz),             two * (xz + wy),             FP64.Zero,
                two * (xy + wz),             FP64.One - two * (xx + zz), two * (yz - wx),             FP64.Zero,
                two * (xz - wy),             two * (yz + wx),             FP64.One - two * (xx + yy), FP64.Zero,
                FP64.Zero,                   FP64.Zero,                   FP64.Zero,                   FP64.One);
        }

        /// <summary>
        /// Build a scale matrix
        /// </summary>
        public static FPMatrix4x4 Scale(FPVector3 s)
        {
            return new FPMatrix4x4(
                s.x, FP64.Zero, FP64.Zero, FP64.Zero,
                FP64.Zero, s.y, FP64.Zero, FP64.Zero,
                FP64.Zero, FP64.Zero, s.z, FP64.Zero,
                FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);
        }

        /// <summary>
        /// Build a TRS (translation * rotation * scale) matrix
        /// </summary>
        public static FPMatrix4x4 TRS(FPVector3 pos, FPQuaternion rot, FPVector3 scale)
        {
            // Inline T * R * S for efficiency
            FP64 two = FP64.FromInt(2);
            FP64 xx = rot.x * rot.x; FP64 yy = rot.y * rot.y; FP64 zz = rot.z * rot.z;
            FP64 xy = rot.x * rot.y; FP64 xz = rot.x * rot.z; FP64 yz = rot.y * rot.z;
            FP64 wx = rot.w * rot.x; FP64 wy = rot.w * rot.y; FP64 wz = rot.w * rot.z;

            FP64 r00 = FP64.One - two * (yy + zz);
            FP64 r01 = two * (xy - wz);
            FP64 r02 = two * (xz + wy);
            FP64 r10 = two * (xy + wz);
            FP64 r11 = FP64.One - two * (xx + zz);
            FP64 r12 = two * (yz - wx);
            FP64 r20 = two * (xz - wy);
            FP64 r21 = two * (yz + wx);
            FP64 r22 = FP64.One - two * (xx + yy);

            return new FPMatrix4x4(
                r00 * scale.x, r01 * scale.y, r02 * scale.z, pos.x,
                r10 * scale.x, r11 * scale.y, r12 * scale.z, pos.y,
                r20 * scale.x, r21 * scale.y, r22 * scale.z, pos.z,
                FP64.Zero,     FP64.Zero,     FP64.Zero,     FP64.One);
        }

        #endregion

        #region Factory Methods — Projection

        /// <summary>
        /// Build an orthographic projection matrix
        /// </summary>
        public static FPMatrix4x4 Ortho(FP64 left, FP64 right, FP64 bottom, FP64 top, FP64 zNear, FP64 zFar)
        {
            FP64 two = FP64.FromInt(2);
            FP64 rl = right - left;
            FP64 tb = top - bottom;
            FP64 fn = zFar - zNear;

            return new FPMatrix4x4(
                two / rl,  FP64.Zero,  FP64.Zero, -(right + left) / rl,
                FP64.Zero, two / tb,   FP64.Zero, -(top + bottom) / tb,
                FP64.Zero, FP64.Zero, -two / fn,  -(zFar + zNear) / fn,
                FP64.Zero, FP64.Zero,  FP64.Zero, FP64.One);
        }

        /// <summary>
        /// Build a perspective projection matrix (fovY in degrees)
        /// </summary>
        public static FPMatrix4x4 Perspective(FP64 fovYDegrees, FP64 aspect, FP64 zNear, FP64 zFar)
        {
            FP64 halfRad = fovYDegrees * FP64.Deg2Rad * FP64.Half;
            FP64 tanHalf = FP64.Sin(halfRad) / FP64.Cos(halfRad);

            FP64 two = FP64.FromInt(2);
            FP64 fn = zFar - zNear;

            return new FPMatrix4x4(
                FP64.One / (aspect * tanHalf), FP64.Zero,           FP64.Zero,                      FP64.Zero,
                FP64.Zero,                      FP64.One / tanHalf, FP64.Zero,                      FP64.Zero,
                FP64.Zero,                      FP64.Zero,          -(zFar + zNear) / fn,           -(two * zFar * zNear) / fn,
                FP64.Zero,                      FP64.Zero,          -FP64.One,                      FP64.Zero);
        }

        /// <summary>
        /// Build a view (look-at) matrix
        /// </summary>
        public static FPMatrix4x4 LookAt(FPVector3 eye, FPVector3 target, FPVector3 up)
        {
            FPVector3 zAxis = (eye - target).normalized;
            FPVector3 xAxis = FPVector3.Cross(up, zAxis).normalized;
            FPVector3 yAxis = FPVector3.Cross(zAxis, xAxis);

            return new FPMatrix4x4(
                xAxis.x, xAxis.y, xAxis.z, -FPVector3.Dot(xAxis, eye),
                yAxis.x, yAxis.y, yAxis.z, -FPVector3.Dot(yAxis, eye),
                zAxis.x, zAxis.y, zAxis.z, -FPVector3.Dot(zAxis, eye),
                FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);
        }

        #endregion

        #region Inverse

        /// <summary>
        /// General 4x4 inverse via cofactor / adjugate. Returns Identity if singular.
        /// </summary>
        public static FPMatrix4x4 Inverse(FPMatrix4x4 m)
        {
            // 2x2 sub-determinants (pairs from upper-left and lower-right rows)
            FP64 s0 = m.m00 * m.m11 - m.m10 * m.m01;
            FP64 s1 = m.m00 * m.m12 - m.m10 * m.m02;
            FP64 s2 = m.m00 * m.m13 - m.m10 * m.m03;
            FP64 s3 = m.m01 * m.m12 - m.m11 * m.m02;
            FP64 s4 = m.m01 * m.m13 - m.m11 * m.m03;
            FP64 s5 = m.m02 * m.m13 - m.m12 * m.m03;

            FP64 c5 = m.m22 * m.m33 - m.m32 * m.m23;
            FP64 c4 = m.m21 * m.m33 - m.m31 * m.m23;
            FP64 c3 = m.m21 * m.m32 - m.m31 * m.m22;
            FP64 c2 = m.m20 * m.m33 - m.m30 * m.m23;
            FP64 c1 = m.m20 * m.m32 - m.m30 * m.m22;
            FP64 c0 = m.m20 * m.m31 - m.m30 * m.m21;

            FP64 det = s0 * c5 - s1 * c4 + s2 * c3 + s3 * c2 - s4 * c1 + s5 * c0;
            if (det == FP64.Zero)
                return Identity;

            FP64 invDet = FP64.One / det;

            return new FPMatrix4x4(
                ( m.m11 * c5 - m.m12 * c4 + m.m13 * c3) * invDet,
                (-m.m01 * c5 + m.m02 * c4 - m.m03 * c3) * invDet,
                ( m.m31 * s5 - m.m32 * s4 + m.m33 * s3) * invDet,
                (-m.m21 * s5 + m.m22 * s4 - m.m23 * s3) * invDet,

                (-m.m10 * c5 + m.m12 * c2 - m.m13 * c1) * invDet,
                ( m.m00 * c5 - m.m02 * c2 + m.m03 * c1) * invDet,
                (-m.m30 * s5 + m.m32 * s2 - m.m33 * s1) * invDet,
                ( m.m20 * s5 - m.m22 * s2 + m.m23 * s1) * invDet,

                ( m.m10 * c4 - m.m11 * c2 + m.m13 * c0) * invDet,
                (-m.m00 * c4 + m.m01 * c2 - m.m03 * c0) * invDet,
                ( m.m30 * s4 - m.m31 * s2 + m.m33 * s0) * invDet,
                (-m.m20 * s4 + m.m21 * s2 - m.m23 * s0) * invDet,

                (-m.m10 * c3 + m.m11 * c1 - m.m12 * c0) * invDet,
                ( m.m00 * c3 - m.m01 * c1 + m.m02 * c0) * invDet,
                (-m.m30 * s3 + m.m31 * s1 - m.m32 * s0) * invDet,
                ( m.m20 * s3 - m.m21 * s1 + m.m22 * s0) * invDet);
        }

        /// <summary>
        /// Fast inverse for affine matrices (rotation + translation + uniform/non-uniform scale in upper 3x3).
        /// Assumes the last row is (0,0,0,1).
        /// </summary>
        public static FPMatrix4x4 InverseAffine(FPMatrix4x4 m)
        {
            // Compute the upper 3x3 inverse via FPMatrix3x3
            var upper = new FPMatrix3x3(
                m.m00, m.m01, m.m02,
                m.m10, m.m11, m.m12,
                m.m20, m.m21, m.m22);

            var invUpper = FPMatrix3x3.Inverse(upper);

            // Translation: -invUpper * t
            FP64 tx = -(invUpper.m00 * m.m03 + invUpper.m01 * m.m13 + invUpper.m02 * m.m23);
            FP64 ty = -(invUpper.m10 * m.m03 + invUpper.m11 * m.m13 + invUpper.m12 * m.m23);
            FP64 tz = -(invUpper.m20 * m.m03 + invUpper.m21 * m.m13 + invUpper.m22 * m.m23);

            return new FPMatrix4x4(
                invUpper.m00, invUpper.m01, invUpper.m02, tx,
                invUpper.m10, invUpper.m11, invUpper.m12, ty,
                invUpper.m20, invUpper.m21, invUpper.m22, tz,
                FP64.Zero, FP64.Zero, FP64.Zero, FP64.One);
        }

        #endregion

        #region Operators

        public static FPMatrix4x4 operator +(FPMatrix4x4 a, FPMatrix4x4 b)
        {
            return new FPMatrix4x4(
                a.m00 + b.m00, a.m01 + b.m01, a.m02 + b.m02, a.m03 + b.m03,
                a.m10 + b.m10, a.m11 + b.m11, a.m12 + b.m12, a.m13 + b.m13,
                a.m20 + b.m20, a.m21 + b.m21, a.m22 + b.m22, a.m23 + b.m23,
                a.m30 + b.m30, a.m31 + b.m31, a.m32 + b.m32, a.m33 + b.m33);
        }

        public static FPMatrix4x4 operator -(FPMatrix4x4 a, FPMatrix4x4 b)
        {
            return new FPMatrix4x4(
                a.m00 - b.m00, a.m01 - b.m01, a.m02 - b.m02, a.m03 - b.m03,
                a.m10 - b.m10, a.m11 - b.m11, a.m12 - b.m12, a.m13 - b.m13,
                a.m20 - b.m20, a.m21 - b.m21, a.m22 - b.m22, a.m23 - b.m23,
                a.m30 - b.m30, a.m31 - b.m31, a.m32 - b.m32, a.m33 - b.m33);
        }

        public static FPMatrix4x4 operator -(FPMatrix4x4 a)
        {
            return new FPMatrix4x4(
                -a.m00, -a.m01, -a.m02, -a.m03,
                -a.m10, -a.m11, -a.m12, -a.m13,
                -a.m20, -a.m21, -a.m22, -a.m23,
                -a.m30, -a.m31, -a.m32, -a.m33);
        }

        /// <summary>
        /// Matrix-matrix multiplication (64 multiplies + 48 adds)
        /// </summary>
        public static FPMatrix4x4 operator *(FPMatrix4x4 a, FPMatrix4x4 b)
        {
            return new FPMatrix4x4(
                a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30,
                a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31,
                a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32,
                a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33,

                a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30,
                a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31,
                a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32,
                a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33,

                a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30,
                a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31,
                a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32,
                a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33,

                a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30,
                a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31,
                a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32,
                a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33);
        }

        public static FPMatrix4x4 operator *(FPMatrix4x4 a, FP64 scalar)
        {
            return new FPMatrix4x4(
                a.m00 * scalar, a.m01 * scalar, a.m02 * scalar, a.m03 * scalar,
                a.m10 * scalar, a.m11 * scalar, a.m12 * scalar, a.m13 * scalar,
                a.m20 * scalar, a.m21 * scalar, a.m22 * scalar, a.m23 * scalar,
                a.m30 * scalar, a.m31 * scalar, a.m32 * scalar, a.m33 * scalar);
        }

        public static FPMatrix4x4 operator *(FP64 scalar, FPMatrix4x4 a)
        {
            return a * scalar;
        }

        /// <summary>
        /// Matrix-vector4 multiplication: M * v
        /// </summary>
        public static FPVector4 operator *(FPMatrix4x4 m, FPVector4 v)
        {
            return new FPVector4(
                m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03 * v.w,
                m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13 * v.w,
                m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23 * v.w,
                m.m30 * v.x + m.m31 * v.y + m.m32 * v.z + m.m33 * v.w);
        }

        public static bool operator ==(FPMatrix4x4 a, FPMatrix4x4 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 &&
                   a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13 &&
                   a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 &&
                   a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;
        }

        public static bool operator !=(FPMatrix4x4 a, FPMatrix4x4 b)
        {
            return !(a == b);
        }

        #endregion

        #region Point/Direction Transform

        /// <summary>
        /// Point transform (w=1): applies rotation, scale, and translation
        /// </summary>
        public FPVector3 MultiplyPoint(FPVector3 p)
        {
            return new FPVector3(
                m00 * p.x + m01 * p.y + m02 * p.z + m03,
                m10 * p.x + m11 * p.y + m12 * p.z + m13,
                m20 * p.x + m21 * p.y + m22 * p.z + m23);
        }

        /// <summary>
        /// Direction transform (w=0): applies rotation and scale only, no translation
        /// </summary>
        public FPVector3 MultiplyVector(FPVector3 v)
        {
            return new FPVector3(
                m00 * v.x + m01 * v.y + m02 * v.z,
                m10 * v.x + m11 * v.y + m12 * v.z,
                m20 * v.x + m21 * v.y + m22 * v.z);
        }

        /// <summary>
        /// Point transform with perspective divide (full w division)
        /// </summary>
        public FPVector3 MultiplyPointPerspective(FPVector3 p)
        {
            FP64 w = m30 * p.x + m31 * p.y + m32 * p.z + m33;
            if (w == FP64.Zero)
                return FPVector3.Zero;

            FP64 invW = FP64.One / w;
            return new FPVector3(
                (m00 * p.x + m01 * p.y + m02 * p.z + m03) * invW,
                (m10 * p.x + m11 * p.y + m12 * p.z + m13) * invW,
                (m20 * p.x + m21 * p.y + m22 * p.z + m23) * invW);
        }

        #endregion

        #region Decompose

        /// <summary>
        /// Extract translation from the 3rd column (assumes affine matrix)
        /// </summary>
        public FPVector3 GetTranslation()
        {
            return new FPVector3(m03, m13, m23);
        }

        /// <summary>
        /// Extract scale from the magnitudes of the upper 3x3 columns
        /// </summary>
        public FPVector3 GetScale()
        {
            FP64 sx = new FPVector3(m00, m10, m20).magnitude;
            FP64 sy = new FPVector3(m01, m11, m21).magnitude;
            FP64 sz = new FPVector3(m02, m12, m22).magnitude;
            return new FPVector3(sx, sy, sz);
        }

        /// <summary>
        /// Extract the rotation quaternion (remove scale from upper 3x3, then convert)
        /// </summary>
        public FPQuaternion GetRotation()
        {
            FPVector3 s = GetScale();
            FP64 invSx = s.x > FP64.Zero ? FP64.One / s.x : FP64.Zero;
            FP64 invSy = s.y > FP64.Zero ? FP64.One / s.y : FP64.Zero;
            FP64 invSz = s.z > FP64.Zero ? FP64.One / s.z : FP64.Zero;

            var rot = new FPMatrix3x3(
                m00 * invSx, m01 * invSy, m02 * invSz,
                m10 * invSx, m11 * invSy, m12 * invSz,
                m20 * invSx, m21 * invSy, m22 * invSz);

            return rot.ToQuaternion();
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Linear interpolation between two matrices
        /// </summary>
        public static FPMatrix4x4 Lerp(FPMatrix4x4 a, FPMatrix4x4 b, FP64 t)
        {
            t = FP64.Clamp01(t);
            return new FPMatrix4x4(
                FP64.LerpUnclamped(a.m00, b.m00, t), FP64.LerpUnclamped(a.m01, b.m01, t), FP64.LerpUnclamped(a.m02, b.m02, t), FP64.LerpUnclamped(a.m03, b.m03, t),
                FP64.LerpUnclamped(a.m10, b.m10, t), FP64.LerpUnclamped(a.m11, b.m11, t), FP64.LerpUnclamped(a.m12, b.m12, t), FP64.LerpUnclamped(a.m13, b.m13, t),
                FP64.LerpUnclamped(a.m20, b.m20, t), FP64.LerpUnclamped(a.m21, b.m21, t), FP64.LerpUnclamped(a.m22, b.m22, t), FP64.LerpUnclamped(a.m23, b.m23, t),
                FP64.LerpUnclamped(a.m30, b.m30, t), FP64.LerpUnclamped(a.m31, b.m31, t), FP64.LerpUnclamped(a.m32, b.m32, t), FP64.LerpUnclamped(a.m33, b.m33, t));
        }

        #endregion

        #region Row/Column Access

        public FPVector4 GetRow(int index)
        {
            switch (index)
            {
                case 0: return new FPVector4(m00, m01, m02, m03);
                case 1: return new FPVector4(m10, m11, m12, m13);
                case 2: return new FPVector4(m20, m21, m22, m23);
                case 3: return new FPVector4(m30, m31, m32, m33);
                default: throw new IndexOutOfRangeException("Row index must be 0..3");
            }
        }

        public FPVector4 GetColumn(int index)
        {
            switch (index)
            {
                case 0: return new FPVector4(m00, m10, m20, m30);
                case 1: return new FPVector4(m01, m11, m21, m31);
                case 2: return new FPVector4(m02, m12, m22, m32);
                case 3: return new FPVector4(m03, m13, m23, m33);
                default: throw new IndexOutOfRangeException("Column index must be 0..3");
            }
        }

        #endregion

        #region IEquatable, GetHashCode, ToString

        public bool Equals(FPMatrix4x4 other)
        {
            return this == other;
        }

        public override bool Equals(object obj)
        {
            return obj is FPMatrix4x4 other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(m00); h.Add(m01); h.Add(m02); h.Add(m03);
            h.Add(m10); h.Add(m11); h.Add(m12); h.Add(m13);
            h.Add(m20); h.Add(m21); h.Add(m22); h.Add(m23);
            h.Add(m30); h.Add(m31); h.Add(m32); h.Add(m33);
            return h.ToHashCode();
        }

        public override string ToString()
        {
            return $"[({m00}, {m01}, {m02}, {m03}), ({m10}, {m11}, {m12}, {m13}), ({m20}, {m21}, {m22}, {m23}), ({m30}, {m31}, {m32}, {m33})]";
        }

        #endregion
    }
}
