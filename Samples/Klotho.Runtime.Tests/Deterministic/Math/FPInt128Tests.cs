using System;
using System.Numerics;
using SysRandom = System.Random;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// FPInt128 exact 128-bit integer accumulator tests.
    /// Every operation is pinned against a System.Numerics.BigInteger oracle --
    /// bit-identical alone cannot distinguish "all runtimes identically wrong",
    /// so the oracle proves correctness (test-only; the runtime type stays GC-0).
    /// </summary>
    [TestFixture]
    public class FPInt128Tests
    {
        private static BigInteger ToBig(FPInt128 v)
        {
            // Two's complement composition: value = Hi * 2^64 + Lo (Hi signed, Lo unsigned).
            return ((BigInteger)v.Hi << 64) + v.Lo;
        }

        #region Mul64 vs BigInteger oracle

        [Test]
        public void Mul64_RandomFullRange_MatchesBigInteger()
        {
            var rng = new SysRandom(96001);
            for (int i = 0; i < 2000; i++)
            {
                long a = rng.NextInt64(long.MinValue + 1, long.MaxValue);
                long b = rng.NextInt64(long.MinValue + 1, long.MaxValue);

                BigInteger expected = (BigInteger)a * b;
                BigInteger actual = ToBig(FPInt128.Mul64(a, b));
                Assert.AreEqual(expected, actual, $"Mul64({a}, {b}) seed-iter {i}");
            }
        }

        [Test]
        public void Mul64_EdgeValues_MatchesBigInteger()
        {
            long[] edges =
            {
                0L, 1L, -1L, 2L, -2L,
                int.MaxValue, int.MinValue + 1L,
                0x7FFFFFFFL, 0x80000000L, 0xFFFFFFFFL, 0x100000000L,
                (1L << 56), -(1L << 56), (1L << 62), -(1L << 62),
                long.MaxValue, -long.MaxValue,
            };

            foreach (long a in edges)
            {
                foreach (long b in edges)
                {
                    BigInteger expected = (BigInteger)a * b;
                    BigInteger actual = ToBig(FPInt128.Mul64(a, b));
                    Assert.AreEqual(expected, actual, $"Mul64({a}, {b})");
                }
            }
        }

        #endregion

        #region Add / Sub / Negate vs BigInteger oracle

        [Test]
        public void AddSub_RandomProducts_MatchesBigInteger()
        {
            // Operands are products of |x| <= 2^60 values -> |sum| < 2^121, no 128-bit wrap.
            var rng = new SysRandom(96002);
            for (int i = 0; i < 2000; i++)
            {
                long a = rng.NextInt64(-(1L << 60), (1L << 60) + 1);
                long b = rng.NextInt64(-(1L << 60), (1L << 60) + 1);
                long c = rng.NextInt64(-(1L << 60), (1L << 60) + 1);
                long d = rng.NextInt64(-(1L << 60), (1L << 60) + 1);

                FPInt128 p = FPInt128.Mul64(a, b);
                FPInt128 q = FPInt128.Mul64(c, d);
                BigInteger bp = (BigInteger)a * b;
                BigInteger bq = (BigInteger)c * d;

                Assert.AreEqual(bp + bq, ToBig(FPInt128.Add(p, q)), $"Add iter {i}");
                Assert.AreEqual(bp - bq, ToBig(FPInt128.Sub(p, q)), $"Sub iter {i}");
                Assert.AreEqual(-bp, ToBig(FPInt128.Negate(p)), $"Negate iter {i}");
            }
        }

        [Test]
        public void Add_LowWordCarry_PropagatesToHigh()
        {
            var nearCarry = new FPInt128(0L, ulong.MaxValue);
            var one = new FPInt128(0L, 1UL);

            FPInt128 sum = FPInt128.Add(nearCarry, one);
            Assert.AreEqual(1L, sum.Hi);
            Assert.AreEqual(0UL, sum.Lo);
            Assert.AreEqual(((BigInteger)ulong.MaxValue) + 1, ToBig(sum));
        }

        [Test]
        public void Negate_TwosComplementEdges()
        {
            Assert.IsTrue(FPInt128.Negate(FPInt128.Zero).Equals(FPInt128.Zero));

            // -(2^64): hi flips to -1 with lo staying 0.
            var pow64 = new FPInt128(1L, 0UL);
            FPInt128 neg = FPInt128.Negate(pow64);
            Assert.AreEqual(-((BigInteger)1 << 64), ToBig(neg));
            Assert.AreEqual(-1, neg.Sign());

            // Negative one is all bits set.
            var minusOne = FPInt128.Negate(new FPInt128(0L, 1UL));
            Assert.AreEqual(-1L, minusOne.Hi);
            Assert.AreEqual(ulong.MaxValue, minusOne.Lo);
            Assert.AreEqual(BigInteger.MinusOne, ToBig(minusOne));
        }

        #endregion

        #region Sign

        [Test]
        public void Sign_MatchesBigInteger()
        {
            var rng = new SysRandom(96003);
            for (int i = 0; i < 1000; i++)
            {
                long a = rng.NextInt64(long.MinValue + 1, long.MaxValue);
                long b = rng.NextInt64(long.MinValue + 1, long.MaxValue);
                FPInt128 p = FPInt128.Mul64(a, b);
                Assert.AreEqual(((BigInteger)a * b).Sign, p.Sign(), $"Sign iter {i}");
            }

            Assert.AreEqual(0, FPInt128.Zero.Sign());
            Assert.AreEqual(1, new FPInt128(0L, 1UL).Sign());
            Assert.AreEqual(1, new FPInt128(1L, 0UL).Sign());
            Assert.AreEqual(-1, new FPInt128(-1L, ulong.MaxValue).Sign());
            Assert.AreEqual(-1, new FPInt128(-1L, 0UL).Sign());
        }

        #endregion
    }
}
