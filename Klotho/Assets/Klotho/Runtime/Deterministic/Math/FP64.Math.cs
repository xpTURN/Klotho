using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    public partial struct FP64
    {
        public static FP64 Abs(FP64 value)
        {
            return value._rawValue < 0 ? new FP64(-value._rawValue) : value;
        }

        public static FP64 Floor(FP64 value)
        {
            return new FP64(value._rawValue & ~(ONE - 1));
        }

        public static FP64 Ceiling(FP64 value)
        {
            long fractional = value._rawValue & (ONE - 1);
            if (fractional == 0)
                return value;
            return new FP64((value._rawValue & ~(ONE - 1)) + ONE);
        }

        public static FP64 Round(FP64 value)
        {
            long fractional = value._rawValue & (ONE - 1);
            if (fractional >= HALF)
                return Ceiling(value);
            return Floor(value);
        }

        public static FP64 Min(FP64 a, FP64 b)
        {
            return a._rawValue < b._rawValue ? a : b;
        }

        public static FP64 Max(FP64 a, FP64 b)
        {
            return a._rawValue > b._rawValue ? a : b;
        }

        public static FP64 Clamp(FP64 value, FP64 min, FP64 max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static FP64 Clamp01(FP64 value)
        {
            return Clamp(value, Zero, One);
        }

        public static FP64 Lerp(FP64 a, FP64 b, FP64 t)
        {
            return a + (b - a) * Clamp01(t);
        }

        public static FP64 LerpUnclamped(FP64 a, FP64 b, FP64 t)
        {
            return a + (b - a) * t;
        }

        public static int Sign(FP64 value)
        {
            if (value._rawValue > 0) return 1;
            if (value._rawValue < 0) return -1;
            return 0;
        }

        public static FP64 Sqrt(FP64 value)
        {
            if (value._rawValue < 0)
                throw new ArgumentException("Cannot calculate square root of negative number");

            if (value._rawValue == 0)
                return Zero;

            return new FP64((long)SqrtInternal((ulong)value._rawValue));
        }

        // ln(2) in Q32.32
        private static readonly FP64 Ln2 = FromRaw(2977044472L);    // 0.693147180559945...
        // 1/ln(2) in Q32.32
        private static readonly FP64 InvLn2 = FromRaw(6196328019L); // 1.442695040888963...

        /// <summary>
        /// Binary logarithm (base 2). Input must be positive.
        /// Algorithm: integer part via bit scan + fractional part via repeated squaring.
        /// </summary>
        public static FP64 Log2(FP64 value)
        {
            if (value._rawValue <= 0)
                throw new ArgumentException("Logarithm of non-positive number");

            // Integer part: find the most-significant set bit position in Q32.32 format
            // value = raw / 2^32, so log2(value) = log2(raw) - 32
            ulong raw = (ulong)value._rawValue;
            int intPart = 0;

            // Find the most-significant set bit position
            ulong b = raw;
            while (b >= (2UL * (ulong)ONE))
            {
                b >>= 1;
                intPart++;
            }
            while (b < (ulong)ONE)
            {
                b <<= 1;
                intPart--;
            }

            // Now b is in [ONE, 2*ONE), i.e. the mantissa is normalized to [1, 2)
            // Compute the fractional part of log2 via repeated squaring
            long fracResult = 0;
            ulong x = b;
            for (int i = 0; i < FRACTIONAL_BITS; i++)
            {
                // Square x and right-shift by 32 bits to keep Q32.32
                // x * x >> 32 -- x can reach ~2*ONE, so a 128-bit intermediate is needed
                ulong xhi = x >> 16;
                ulong xlo = x & 0xFFFF;
                ulong sq = xhi * xhi * (1UL << 32 >> 32)
                          + 2 * xhi * xlo
                          + ((xlo * xlo) >> 32);
                // Simpler: x is in [ONE, 2*ONE), so it fits in 33 bits
                // Overflow can be avoided with sq = (x >> 1) * (x >> 1) >> 30
                // Exact implementation:
                x = (ulong)(((x >> 1) * (x >> 1)) >> 30);

                if (x >= (2UL * (ulong)ONE))
                {
                    fracResult |= (1L << (FRACTIONAL_BITS - 1 - i));
                    x >>= 1;
                }
            }

            long result = ((long)intPart << FRACTIONAL_BITS) + fracResult;
            return new FP64(result);
        }

        /// <summary>
        /// Natural logarithm (base e). Input must be positive.
        /// ln(x) = log2(x) * ln(2)
        /// </summary>
        public static FP64 Ln(FP64 value)
        {
            return Log2(value) * Ln2;
        }

        /// <summary>
        /// Alias for Ln (natural logarithm).
        /// </summary>
        public static FP64 Log(FP64 value)
        {
            return Ln(value);
        }

        /// <summary>
        /// Base-2 exponential function: 2^x.
        /// Integer part via shift; fractional part via a 5th-order minimax polynomial.
        /// Effective range: approximately [-31, 30] (Q32.32 range limit).
        /// </summary>
        public static FP64 Exp2(FP64 value)
        {
            if (value._rawValue == 0)
                return One;

            // Clamp to the effective range
            if (value >= FromInt(31))
                return MaxValue;
            if (value <= FromInt(-32))
                return Zero;

            // Split into integer and fractional parts
            // Negative values need floor rather than truncation toward zero
            long rawFloor = value._rawValue & ~(ONE - 1);
            if (value._rawValue < 0 && (value._rawValue & (ONE - 1)) != 0)
                rawFloor -= ONE;

            int intPart = (int)(rawFloor >> FRACTIONAL_BITS);
            long fracRaw = value._rawValue - rawFloor; // always in [0, ONE)

            // 2^intPart
            FP64 intResult;
            if (intPart >= 0)
                intResult = new FP64(ONE << intPart);
            else if (intPart >= -FRACTIONAL_BITS)
                intResult = new FP64(ONE >> (-intPart));
            else
                return Zero;

            if (fracRaw == 0)
                return intResult;

            // For frac in [0, 1), compute 2^frac with a 5th-order minimax polynomial
            // 2^f ~= 1 + f*ln2 * (1 + f*ln2/2 * (1 + f*ln2/3 * (1 + f*ln2/4 * (1 + f*ln2/5))))
            // Horner form of the Taylor series for exp(f*ln2)
            FP64 f = new FP64(fracRaw);
            FP64 fln2 = f * Ln2;
            FP64 fln2sq = fln2 * fln2;

            // exp(y) ~= 1 + y + y^2/2 + y^3/6 + y^4/24 + y^5/120  (y = f*ln2)
            FP64 fracResult = One + fln2 + fln2sq * Half
                + fln2sq * fln2 / FromInt(6)
                + fln2sq * fln2sq / FromInt(24)
                + fln2sq * fln2sq * fln2 / FromInt(120);

            return intResult * fracResult;
        }

        /// <summary>
        /// Natural exponential function: e^x.
        /// exp(x) = 2^(x / ln(2)) = 2^(x * log2(e))
        /// </summary>
        public static FP64 Exp(FP64 value)
        {
            return Exp2(value * InvLn2);
        }

        /// <summary>
        /// Power function: base^exponent.
        /// pow(b, e) = 2^(e * log2(b)). The base must be positive.
        /// Special cases: pow(0, e>0) = 0, pow(x, 0) = 1.
        /// </summary>
        public static FP64 Pow(FP64 b, FP64 e)
        {
            if (e == Zero)
                return One;
            if (b == One)
                return One;
            if (b._rawValue <= 0)
            {
                if (b._rawValue == 0)
                    return e > Zero ? Zero : MaxValue;
                throw new ArgumentException("Pow with negative base requires integer exponent");
            }

            return Exp2(e * Log2(b));
        }

        /// <summary>
        /// Reciprocal: 1 / x.
        /// </summary>
        public static FP64 Rcp(FP64 value)
        {
            return One / value;
        }

        /// <summary>
        /// Reciprocal square root: 1 / sqrt(x). Input must be positive.
        /// </summary>
        public static FP64 RSqrt(FP64 value)
        {
            return One / Sqrt(value);
        }

        /// <summary>
        /// Floating-point remainder (truncated division): a - trunc(a/b) * b.
        /// The result has the same sign as the dividend. Equivalent to C's fmod().
        /// </summary>
        public static FP64 Fmod(FP64 a, FP64 b)
        {
            if (b._rawValue == 0)
                return Zero;

            FP64 q = a / b;
            FP64 trunc = q._rawValue >= 0 ? Floor(q) : Ceiling(q);
            return a - trunc * b;
        }

        /// <summary>
        /// IEEE remainder: a - round(a/b) * b.
        /// The result may be negative even when both operands are positive.
        /// </summary>
        public static FP64 Remainder(FP64 a, FP64 b)
        {
            if (b._rawValue == 0)
                return Zero;

            return a - Round(a / b) * b;
        }

        /// <summary>
        /// Cube root. Supports negative input: cbrt(-x) = -cbrt(x).
        /// Uses Newton's method: x_{n+1} = (2*x_n + v / x_n^2) / 3.
        /// </summary>
        public static FP64 Cbrt(FP64 value)
        {
            if (value._rawValue == 0)
                return Zero;

            bool negative = value._rawValue < 0;
            FP64 v = negative ? Abs(value) : value;

            // Initial estimate for pow(v, 1/3) via bit manipulation
            // log2(v) / 3 is a rough initial estimate
            ulong raw = (ulong)v._rawValue;
            int leadingZeros = CountLeadingZeroes(raw);
            int bitPos = 63 - leadingZeros; // most-significant set bit position
            // In Q32.32, log2(v) ~= bitPos - 32
            // cbrt estimate: 2^((bitPos - 32) / 3) in Q32.32 -> raw = 1L << (32 + (bitPos - 32) / 3)
            int estShift = 32 + (bitPos - 32) / 3;
            FP64 x;
            if (estShift >= 0 && estShift < 63)
                x = new FP64(1L << estShift);
            else
                x = One;

            FP64 three = FromInt(3);
            FP64 two = FromInt(2);

            // Newton iteration: x = (2*x + v / (x*x)) / 3
            for (int i = 0; i < 20; i++)
            {
                FP64 x2 = x * x;
                if (x2 == Zero)
                {
                    x = One;
                    x2 = One;
                }
                FP64 xNew = (two * x + v / x2) / three;
                if (xNew == x)
                    break;
                x = xNew;
            }

            return negative ? -x : x;
        }

        /// <summary>
        /// Inverse interpolation: returns t such that Lerp(a, b, t) == value.
        /// The result is clamped to [0, 1]. Returns 0 when a == b.
        /// </summary>
        public static FP64 InverseLerp(FP64 a, FP64 b, FP64 value)
        {
            if (a == b)
                return Zero;
            return Clamp01((value - a) / (b - a));
        }

        /// <summary>
        /// Repeats the value within [0, length). Similar to fmod but always non-negative.
        /// Equivalent to Unity Mathf.Repeat.
        /// </summary>
        public static FP64 Repeat(FP64 t, FP64 length)
        {
            return t - Floor(t / length) * length;
        }

        /// <summary>
        /// PingPong: the value oscillates between 0 and length.
        /// Equivalent to Unity Mathf.PingPong.
        /// </summary>
        public static FP64 PingPong(FP64 t, FP64 length)
        {
            FP64 two = FromInt(2);
            t = Repeat(t, length * two);
            return length - Abs(t - length);
        }

        /// <summary>
        /// Hermite interpolation (SmoothStep): 3t^2 - 2t^3, clamped to [0, 1].
        /// Equivalent to Unity Mathf.SmoothStep.
        /// </summary>
        public static FP64 SmoothStep(FP64 from, FP64 to, FP64 t)
        {
            t = Clamp01((t - from) / (to - from));
            return t * t * (FromInt(3) - FromInt(2) * t);
        }

        /// <summary>
        /// Moves current toward target by at most maxDelta.
        /// Equivalent to Unity Mathf.MoveTowards.
        /// </summary>
        public static FP64 MoveTowards(FP64 current, FP64 target, FP64 maxDelta)
        {
            FP64 diff = target - current;
            if (Abs(diff) <= maxDelta)
                return target;
            return current + new FP64((long)Sign(diff) << FRACTIONAL_BITS) * maxDelta;
        }

        private static readonly FP64 F180 = FromInt(180);
        private static readonly FP64 F360 = FromInt(360);

        /// <summary>
        /// Shortest angle difference (in degrees), wrapped to [-180, 180).
        /// Equivalent to Unity Mathf.DeltaAngle.
        /// </summary>
        public static FP64 DeltaAngle(FP64 current, FP64 target)
        {
            FP64 delta = Repeat(target - current, F360);
            if (delta > F180)
                delta -= F360;
            return delta;
        }

        /// <summary>
        /// Moves the angle current toward target by at most maxDelta (degrees).
        /// Equivalent to Unity Mathf.MoveTowardsAngle.
        /// </summary>
        public static FP64 MoveTowardsAngle(FP64 current, FP64 target, FP64 maxDelta)
        {
            FP64 delta = DeltaAngle(current, target);
            if (-maxDelta < delta && delta < maxDelta)
                return target;
            target = current + delta;
            return MoveTowards(current, target, maxDelta);
        }

        /// <summary>
        /// Critically damped spring smoothing (same algorithm as Unity Mathf.SmoothDamp).
        /// Uses a 4th-order Taylor expansion of exp(-omega*dt) for determinism.
        /// </summary>
        public static FP64 SmoothDamp(FP64 current, FP64 target, ref FP64 currentVelocity,
            FP64 smoothTime, FP64 maxSpeed, FP64 deltaTime)
        {
            FP64 two = FromInt(2);

            smoothTime = Max(smoothTime, FromRaw(ONE / 10000));
            FP64 omega = two / smoothTime;
            FP64 x = omega * deltaTime;

            // exp(-x) ~= 1/(1 + x + x^2/2 + x^3/6 + x^4/24) -- reciprocal of 4th-order Taylor
            FP64 x2 = x * x;
            FP64 x3 = x2 * x;
            FP64 x4 = x2 * x2;
            FP64 exp = One / (One + x + x2 * Half + x3 / FromInt(6) + x4 / FromInt(24));

            FP64 change = current - target;

            // Clamp to maximum change
            FP64 maxChange = maxSpeed * smoothTime;
            change = Clamp(change, -maxChange, maxChange);
            FP64 clampedTarget = current - change;

            FP64 temp = (currentVelocity + omega * change) * deltaTime;
            currentVelocity = (currentVelocity - omega * temp) * exp;
            FP64 output = clampedTarget + (change + temp) * exp;

            // Prevent overshoot
            if ((target - current > Zero) == (output > target))
            {
                output = target;
                currentVelocity = Zero;
            }

            return output;
        }
    }
}
