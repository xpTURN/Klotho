using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    public partial struct FP64
    {
        /// <summary>
        /// Binary restoring square root.
        /// Two-pass: avoids 128-bit intermediates while remaining accurate to the last bit.
        /// </summary>
        private static ulong SqrtInternal(ulong num)
        {
            ulong result = 0UL;

            // Start from the second-most-significant bit
            ulong bit = 1UL << 62;
            while (bit > num)
                bit >>= 2;

            // Two passes to compute sqrt(num * 2^32) within 64-bit arithmetic
            for (int i = 0; i < 2; i++)
            {
                while (bit != 0)
                {
                    if (num >= result + bit)
                    {
                        num -= result + bit;
                        result = (result >> 1) + bit;
                    }
                    else
                    {
                        result >>= 1;
                    }
                    bit >>= 2;
                }

                if (i == 0)
                {
                    // Between passes: left-shift remainder and result by 32 bits to
                    // precisely compute the lower 16 bits of the answer
                    if (num > (1UL << 32) - 1)
                    {
                        // Remainder is too large for a direct 32-bit left shift
                        // Adjustment: num = num - result - 0.5 (in Q32.32)
                        num -= result;
                        num = (num << 32) - 0x80000000UL;
                        result = (result << 32) + 0x80000000UL;
                    }
                    else
                    {
                        num <<= 32;
                        result <<= 32;
                    }
                    bit = 1UL << 30;
                }
            }

            // Round up if the next bit would have been 1
            if (num > result)
                ++result;

            return result;
        }

        private static int CountLeadingZeroes(ulong x)
        {
            int result = 0;
            while ((x & 0xF000000000000000) == 0) { result += 4; x <<= 4; }
            while ((x & 0x8000000000000000) == 0) { result += 1; x <<= 1; }
            return result;
        }

        /// <summary>
        /// Fixed-point division with a safe-range fast path and a Shift-and-Divide slow path.
        /// Zero GC -- no BigInteger allocation.
        /// </summary>
        private static FP64 SafeDivide(FP64 a, FP64 b)
        {
            if (b._rawValue == 0)
                throw new DivideByZeroException();

            long aRaw = a._rawValue;
            long bRaw = b._rawValue;

            // Fast path: |a| < 2^31 -> (a << 32) fits in a long
            long aAbs = aRaw >= 0 ? aRaw : -aRaw;
            if (aAbs < 0x80000000L)
            {
                return new FP64((aRaw << FRACTIONAL_BITS) / bRaw);
            }

            // Slow path: Shift-and-Divide, zero GC
            ulong remainder = (ulong)(aRaw >= 0 ? aRaw : -aRaw);
            ulong divider = (ulong)(bRaw >= 0 ? bRaw : -bRaw);
            ulong quotient = 0UL;
            int bitPos = FRACTIONAL_BITS + 1; // = 33

            while ((divider & 0xF) == 0 && bitPos >= 4)
            {
                divider >>= 4;
                bitPos -= 4;
            }

            while (remainder != 0 && bitPos >= 0)
            {
                int shift = CountLeadingZeroes(remainder);
                if (shift > bitPos) shift = bitPos;
                remainder <<= shift;
                bitPos -= shift;

                ulong div = remainder / divider;
                remainder = remainder % divider;
                quotient += div << bitPos;

                if ((div & ~(0xFFFFFFFFFFFFFFFF >> bitPos)) != 0)
                    return ((aRaw ^ bRaw) & long.MinValue) == 0 ? MaxValue : MinValue;

                remainder <<= 1;
                --bitPos;
            }

            ++quotient;
            var result = (long)(quotient >> 1);
            if (((aRaw ^ bRaw) & long.MinValue) != 0)
                result = -result;

            return new FP64(result);
        }

        /// <summary>
        /// Fixed-point multiplication with a safe-range fast path and a hi/lo 4-product decomposition slow path.
        /// Zero GC -- no BigInteger allocation.
        /// </summary>
        private static FP64 SafeMultiply(FP64 a, FP64 b)
        {
            long aRaw = a._rawValue;
            long bRaw = b._rawValue;

            long aAbs = aRaw >= 0 ? aRaw : -aRaw;
            long bAbs = bRaw >= 0 ? bRaw : -bRaw;

            // Fast path: |a| < 2^31 AND |b| < 2^31 -> intermediate fits in a long
            if (aAbs < 0x80000000L && bAbs < 0x80000000L)
            {
                return new FP64((aRaw * bRaw) >> FRACTIONAL_BITS);
            }

            // Slow path: Hi/Lo 4-product decomposition, zero GC
            ulong xlo = (ulong)(aRaw & 0xFFFFFFFFL);
            long  xhi = aRaw >> FRACTIONAL_BITS;
            ulong ylo = (ulong)(bRaw & 0xFFFFFFFFL);
            long  yhi = bRaw >> FRACTIONAL_BITS;

            ulong lolo = xlo * ylo;
            long  lohi = (long)xlo * yhi;
            long  hilo = xhi * (long)ylo;
            long  hihi = xhi * yhi;

            long loResult = (long)(lolo >> FRACTIONAL_BITS);
            long hiResult = hihi << FRACTIONAL_BITS;

            bool overflow = false;
            long sum = AddOverflowHelper(loResult, lohi, ref overflow);
            sum = AddOverflowHelper(sum, hilo, ref overflow);
            sum = AddOverflowHelper(sum, hiResult, ref overflow);

            // Saturation clamp
            bool sameSign = ((aRaw ^ bRaw) & long.MinValue) == 0;
            if (sameSign && (sum < 0 || (overflow && aRaw > 0)))
                return MaxValue;
            if (!sameSign && sum > 0)
                return MinValue;

            long topCarry = hihi >> FRACTIONAL_BITS;
            if (topCarry != 0 && topCarry != -1)
                return sameSign ? MaxValue : MinValue;

            return new FP64(sum);
        }

        private static long AddOverflowHelper(long x, long y, ref bool overflow)
        {
            long sum = x + y;
            overflow |= ((x ^ y ^ sum) & long.MinValue) != 0;
            return sum;
        }
    }
}
