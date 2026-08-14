using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The in-memory size baseline for <c>FPNavMeshTriangle</c> on <b>CoreCLR</b>.
    ///
    /// The cross-runtime size matrix (<c>SizeOfCrossRuntimeTests</c>) is Unity PlayMode only, so it
    /// covers Mono and IL2CPP. The main beneficiary of shrinking this struct — about 2.4 MB of
    /// resident memory — is the server, which runs on CoreCLR, and that runtime was missing from
    /// the matrix. This fills it in.
    ///
    /// Replacing the six portal ints with a <c>byte portalFlip</c> and moving <c>isBlocked</c>
    /// took it from <b>120 to 88 bytes</b>.
    /// </summary>
    [TestFixture]
    public class FPNavMeshTriangleSizeTests
    {
        // 88 B, down from 120 B. Layout:
        //   0  v0 v1 v2 (12) / 12 neighbor0..2 (12) / 24 centerXZ (16)
        //  40  area (8) / 48 areaMask (4) / 52 isBlocked (1) / 53 portalFlip (1) / 54 pad(2)
        //  56  costMultiplier (8) / 64 minY maxY centerY (24)  -> 88
        // Where the 32 bytes went: dropping the six portal ints -24, the portalFlip byte is
        // absorbed by existing padding (0), and moving isBlocked into that same word -8.
        private const int ExpectedTriangleSize = 88;

        [Test]
        public void FPNavMeshTriangle_SizeIsStable_CoreClr()
        {
            int actual = Unsafe.SizeOf<FPNavMeshTriangle>();
            TestContext.Out.WriteLine($"Unsafe.SizeOf<FPNavMeshTriangle>() = {actual} (CoreCLR)");
            Assert.AreEqual(ExpectedTriangleSize, actual,
                "the FPNavMeshTriangle layout changed. If that was intended, update " +
                "ExpectedTriangleSize and check the new size against the field layout above.");
        }

        /// <summary>
        /// Component baselines, to narrow down the cause when the total is unexpected:
        /// <c>FP64</c> must be a single <c>long</c>, and <c>FPVector2</c>/<c>FPVector3</c> multiples
        /// of it.
        /// </summary>
        [Test]
        public void ComponentSizes_AreAsAssumed()
        {
            TestContext.Out.WriteLine($"FP64      = {Unsafe.SizeOf<FP64>()}");
            TestContext.Out.WriteLine($"FPVector2 = {Unsafe.SizeOf<FPVector2>()}");
            TestContext.Out.WriteLine($"FPVector3 = {Unsafe.SizeOf<FPVector3>()}");

            Assert.AreEqual(8, Unsafe.SizeOf<FP64>(), "FP64 must be a single long field");
            Assert.AreEqual(16, Unsafe.SizeOf<FPVector2>(), "FPVector2 = FP64 × 2");
            Assert.AreEqual(24, Unsafe.SizeOf<FPVector3>(), "FPVector3 = FP64 × 3");
        }

        [Test]
        public void EveryGetter_IsReadonly_SoAReadOnlyRefDoesNotCopy()
        {
            // Consumers bind this struct through read-only references — FPNavMesh.Triangles is a
            // ReadOnlySpan, and the query, funnel, agent and rebaker paths all take
            // `ref readonly FPNavMeshTriangle`. A non-`readonly` instance method called on such a
            // reference makes the compiler copy the whole struct first, and the struct is the 88
            // bytes pinned above; the A* edge loop would take two copies per edge.
            //
            // Today that costs nothing measurable (a pathfinding sweep moves 0.55%, noise) because
            // the JIT inlines these and scalarises the copy. This gate exists precisely because
            // that is a property of the current JIT and the current method bodies rather than
            // anything the source guarantees — a getter that grows past the inliner's budget, or a
            // NEW getter added without the keyword, would reintroduce the copy with nothing in the
            // diff to show it. Checking the attribute is what makes the intent enforceable.
            //
            // Scoped to Get*: setters mutate and must NOT be readonly, and that contrast is the
            // other half of what this pins.
            var getters = typeof(FPNavMeshTriangle)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            int checkedGetters = 0;
            foreach (var m in getters)
            {
                if (!m.Name.StartsWith("Get", StringComparison.Ordinal))
                    continue;
                checkedGetters++;
                bool isReadOnly = m.GetCustomAttributes()
                    .Any(a => a.GetType().Name == "IsReadOnlyAttribute");
                Assert.IsTrue(isReadOnly,
                    $"{m.Name} is not `readonly`, so every call through a `ref readonly` "
                    + $"FPNavMeshTriangle copies {ExpectedTriangleSize} bytes first");
            }

            Assert.GreaterOrEqual(checkedGetters, 3,
                "the scan found fewer getters than this struct has — it is looking at the wrong "
                + "type or the wrong binding flags, and would pass no matter what");

            // The contrast, so the rule cannot be "satisfied" by making everything readonly.
            var setter = typeof(FPNavMeshTriangle).GetMethod(nameof(FPNavMeshTriangle.SetNeighbor));
            Assert.IsFalse(
                setter.GetCustomAttributes().Any(a => a.GetType().Name == "IsReadOnlyAttribute"),
                "SetNeighbor mutates — marking it readonly would be a compile error, and if this "
                + "ever passes the reflection above is not measuring what it claims");
        }
    }
}
