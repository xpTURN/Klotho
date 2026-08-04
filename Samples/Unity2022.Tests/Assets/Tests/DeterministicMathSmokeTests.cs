using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for the deterministic layer.
    ///
    /// Every expected value below is the raw fixed-point bit pattern produced by the same source on
    /// .NET 8 (the server mirror / Godot build closure). Cross-runtime bit-equality is the whole
    /// premise of the framework, so a mismatch here means Unity 2022's Mono runtime — its 64-bit
    /// multiply/shift codegen, its intrinsics, or its handling of the checked/unchecked paths inside
    /// FP64 — diverges from the runtime the goldens were baked on. That is a hard compatibility
    /// failure, not a tolerance question: comparisons are exact.
    /// </summary>
    [TestFixture]
    public class DeterministicMathSmokeTests
    {
        [Test]
        public void FP64_Arithmetic_MatchesDotnetGoldens()
        {
            Assert.AreEqual(1431655765L, FP64.FromDouble(1.0 / 3.0).RawValue, "FromDouble(1/3)");
            Assert.AreEqual(10021590357L, (FP64.FromInt(7) / FP64.FromInt(3)).RawValue, "7/3");
            Assert.AreEqual(4398046511104L, FP64.Pow(FP64.FromInt(2), FP64.FromInt(10)).RawValue, "2^10");
        }

        [Test]
        public void FP64_Transcendental_MatchesDotnetGoldens()
        {
            Assert.AreEqual(6074001000L, FP64.Sqrt(FP64.FromInt(2)).RawValue, "Sqrt(2)");
            Assert.AreEqual(2147483388L, FP64.Sin(FP64.Pi / FP64.FromInt(6)).RawValue, "Sin(pi/6)");
            Assert.AreEqual(2147483476L, FP64.Cos(FP64.Pi / FP64.FromInt(3)).RawValue, "Cos(pi/3)");
            Assert.AreEqual(3373259429L, FP64.Atan2(FP64.One, FP64.One).RawValue, "Atan2(1,1)");
            Assert.AreEqual(11674921136L, FP64.Exp(FP64.One).RawValue, "Exp(1)");
            Assert.AreEqual(9889527671L, FP64.Ln(FP64.FromInt(10)).RawValue, "Ln(10)");
        }

        [Test]
        public void FPVector3_MatchesDotnetGoldens()
        {
            var normalized = new FPVector3(1, 2, 3).normalized;
            Assert.AreEqual(1147878293L, normalized.x.RawValue, "normalized.x");
            Assert.AreEqual(2295756587L, normalized.y.RawValue, "normalized.y");
            Assert.AreEqual(3443634880L, normalized.z.RawValue, "normalized.z");

            Assert.AreEqual(22317304722L,
                FPVector3.Distance(new FPVector3(1, 2, 3), new FPVector3(4, 5, 6)).RawValue, "Distance");
        }

        [Test]
        public void DeterministicRandom_SequenceMatchesDotnetGoldens()
        {
            var rng = new DeterministicRandom(12345);

            Assert.AreEqual(1496797897L, rng.NextInt(), "NextInt #1");
            Assert.AreEqual(1196265139L, rng.NextInt(), "NextInt #2");
            Assert.AreEqual(982638457L, rng.NextInt(), "NextInt #3");
            Assert.AreEqual(2277752969L, rng.NextFixed().RawValue, "NextFixed #4");

            var dir = rng.NextDirection3D();
            Assert.AreEqual(-3993280488L, dir.x.RawValue, "NextDirection3D.x");
            Assert.AreEqual(912794299L, dir.y.RawValue, "NextDirection3D.y");
            Assert.AreEqual(1291223534L, dir.z.RawValue, "NextDirection3D.z");
        }
    }
}
