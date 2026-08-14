using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Signed 128-bit integer accumulator for exact geometric predicates.
    /// Two's-complement representation (Hi: signed high 64 bits, Lo: unsigned low 64 bits).
    /// Value type, zero GC, pure integer bit operations -- cross-platform bit-identical
    /// by construction (no floating point, no division).
    ///
    /// Named FPInt128 (not Int128) to avoid colliding with System.Int128 on .NET 7+ runtimes
    /// used by the dotnet test projects. Reference implementation: dotnet/runtime Int128 (MIT).
    ///
    /// Minimal surface on purpose: exact signed 64x64->128
    /// multiply, addition, subtraction/negation, and sign. Overflow past 128 bits wraps
    /// (two's complement); predicate inputs are domain-guarded so accumulations stay
    /// far below 2^127.
    /// </summary>
    public readonly struct FPInt128 : IEquatable<FPInt128>
    {
        public readonly long Hi;
        public readonly ulong Lo;

        public static readonly FPInt128 Zero = new FPInt128(0L, 0UL);

        public FPInt128(long hi, ulong lo)
        {
            Hi = hi;
            Lo = lo;
        }

        /// <summary>
        /// Exact signed 64x64 -> 128-bit product. No truncation, no saturation
        /// (contrast: FP64.SafeMultiply folds to Q32.32 and saturates).
        /// long.MinValue operands are outside the supported domain (sign-split would
        /// overflow); predicate inputs are bounded far below that by the domain assert.
        /// </summary>
        public static FPInt128 Mul64(long a, long b)
        {
            System.Diagnostics.Debug.Assert(a != long.MinValue && b != long.MinValue,
                "FPInt128.Mul64: long.MinValue operand is outside the supported domain");

            bool negative = (a < 0) ^ (b < 0);
            ulong ua = (ulong)(a < 0 ? -a : a);
            ulong ub = (ulong)(b < 0 ? -b : b);

            // Schoolbook 32-bit split: exact unsigned 64x64 -> 128.
            ulong aLo = ua & 0xFFFFFFFFUL;
            ulong aHi = ua >> 32;
            ulong bLo = ub & 0xFFFFFFFFUL;
            ulong bHi = ub >> 32;

            ulong ll = aLo * bLo;
            ulong lh = aLo * bHi;
            ulong hl = aHi * bLo;
            ulong hh = aHi * bHi;

            // mid = lh + hl contributes at bit offset 32; its 64-bit wraparound is worth 2^96.
            ulong mid = lh + hl;
            ulong midCarry = mid < lh ? 1UL << 32 : 0UL;

            ulong lo = ll + (mid << 32);
            ulong loCarry = lo < ll ? 1UL : 0UL;
            ulong hi = hh + (mid >> 32) + midCarry + loCarry;

            var magnitude = new FPInt128((long)hi, lo);
            return negative ? Negate(magnitude) : magnitude;
        }

        public static FPInt128 Add(FPInt128 a, FPInt128 b)
        {
            ulong lo = a.Lo + b.Lo;
            long carry = lo < a.Lo ? 1L : 0L;
            return new FPInt128(a.Hi + b.Hi + carry, lo);
        }

        public static FPInt128 Sub(FPInt128 a, FPInt128 b)
        {
            return Add(a, Negate(b));
        }

        /// <summary>Two's-complement negation (~v + 1).</summary>
        public static FPInt128 Negate(FPInt128 v)
        {
            ulong lo = ~v.Lo + 1UL;
            long hi = ~v.Hi + (lo == 0UL ? 1L : 0L);
            return new FPInt128(hi, lo);
        }

        /// <summary>-1, 0, +1 by two's-complement value. Predicates consume only this.</summary>
        public int Sign()
        {
            if (Hi < 0)
                return -1;
            if (Hi > 0 || Lo != 0UL)
                return 1;
            return 0;
        }

        public bool Equals(FPInt128 other)
        {
            return Hi == other.Hi && Lo == other.Lo;
        }

        public override bool Equals(object obj)
        {
            return obj is FPInt128 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Hi.GetHashCode() * 397) ^ Lo.GetHashCode();
        }
    }
}
