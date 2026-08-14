using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// V3/V4/V4b — rebinding a query/pathfinder to a new mesh must answer exactly as a freshly
    /// built one would.
    ///
    /// <para>The oracle is always <b>a fresh instance on the same mesh</b>, never "the same as
    /// before the rebind" — the mesh changed, so before and after are supposed to differ, and a
    /// test that demands they match would pass precisely when the rebind did nothing.</para>
    ///
    /// <para>What makes this worth gating is that the failure is silent. A stale generation stamp
    /// does not throw and does not move the navmesh fingerprint; it just makes a search skip nodes
    /// it should have expanded, and every peer computes the same wrong path. That is a desync no
    /// state hash can see, which is exactly the shape of C-1.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebindTests
    {
        #region Fixture

        /// <summary>Square grid of points at <paramref name="step"/> spacing over [-half, half].</summary>
        private static FPNavMesh BuildMesh(int half, int step)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x += step)
            {
                for (int z = -half; z <= half; z += step)
                    pts.Add((x, z));
            }
            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>Small (fewer triangles), same, and large (more) relative to the 5-step base.</summary>
        private static FPNavMesh SmallerMesh() => BuildMesh(10, 10);
        private static FPNavMesh SameSizeMesh() => BuildMesh(10, 5);
        private static FPNavMesh LargerMesh() => BuildMesh(10, 2);

        private static readonly FPVector3 Start = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.FromInt(-8));
        private static readonly FPVector3 End = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(8));

        private static int[] PathOn(FPNavMeshPathfinder pf, out bool ok)
        {
            ok = pf.FindPath(Start, End, ~0, out int[] corridor, out int length);
            var copy = new int[length];
            System.Array.Copy(corridor, copy, length);
            return copy;
        }

        /// <summary>Fresh query+pathfinder on <paramref name="mesh"/> — the oracle.</summary>
        private static (FPNavMeshQuery q, FPNavMeshPathfinder pf) Fresh(FPNavMesh mesh)
        {
            var q = new FPNavMeshQuery(mesh, null);
            return (q, new FPNavMeshPathfinder(mesh, q, null));
        }

        private static void AssertSamePath(FPNavMesh target, FPNavMeshPathfinder rebound, string what)
        {
            var (_, oracle) = Fresh(target);
            int[] expected = PathOn(oracle, out bool okExpected);
            int[] actual = PathOn(rebound, out bool okActual);

            Assert.AreEqual(okExpected, okActual, $"{what}: path success differs from a fresh pathfinder");
            Assert.AreEqual(expected.Length, actual.Length, $"{what}: corridor length differs from a fresh pathfinder");
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], actual[i], $"{what}: corridor[{i}] differs from a fresh pathfinder");
        }

        #endregion

        // ── V3 — the same path, across all three size relations ──────────────────

        [TestCase("smaller")]
        [TestCase("same")]
        [TestCase("larger")]
        public void V3_ReboundPathfinder_MatchesFresh(string relation)
        {
            FPNavMesh from = SameSizeMesh();
            FPNavMesh to = relation switch
            {
                "smaller" => SmallerMesh(),
                "larger" => LargerMesh(),
                _ => SameSizeMesh(),
            };

            var (q, pf) = Fresh(from);
            q.Rebind(to);
            pf.Rebind(to);

            AssertSamePath(to, pf, $"rebind to a {relation} mesh");
        }

        // ── V4 — a poisoned generation must not survive the rebind ───────────────

        [Test]
        public void V4_PoisonedGeneration_DoesNotAliasAfterRebind()
        {
            // EXACTLY ONE search before the rebind. Repeating it would overwrite the stamps with
            // later generations, so a reset-to-zero would collide with nothing and this test would
            // pass against the very bug it exists to catch — the trap FPNavAgentSystemSwapTests
            // wrote down as "aliasing is total rather than partial and the comparison cannot pass
            // by luck".
            FPNavMesh from = SameSizeMesh();
            FPNavMesh to = SameSizeMesh();

            var (q, pf) = Fresh(from);
            PathOn(pf, out bool okBefore);
            Assert.IsTrue(okBefore, "the poisoning search must actually run, or nothing is stamped");

            q.Rebind(to);
            pf.Rebind(to);

            AssertSamePath(to, pf, "rebind after one poisoning search");
        }

        // ── V4b — the query's own generations ────────────────────────────────────

        [Test]
        public void V4b_ReboundQuery_MatchesFresh()
        {
            FPNavMesh from = SameSizeMesh();
            FPNavMesh to = SameSizeMesh();

            var probeXZ = new FPVector2(FP64.FromInt(3), FP64.FromInt(3));
            var outsideXZ = new FPVector2(FP64.FromInt(11), FP64.Zero);
            var rayOrigin = new FPVector3(FP64.FromInt(-9), FP64.Zero, FP64.Zero);
            var rayDir = new FPVector3(FP64.FromInt(30), FP64.Zero, FP64.Zero);
            var moveFrom = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.Zero);
            var moveTo = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero);

            var query = new FPNavMeshQuery(from, null);

            // One call each, so each counter is left at generation 1 — the value a reset would
            // collide with. FindTriangle drives no counter of its own; it is here because
            // MoveAlongSurface needs a starting triangle.
            query.ClosestPointOnNavMesh(outsideXZ, out _);
            query.Raycast(rayOrigin, rayDir, out _, out _);
            query.MoveAlongSurface(moveFrom, moveTo, query.FindTriangle(moveFrom.ToXZ()), FP64.MaxValue);

            query.Rebind(to);

            var oracle = new FPNavMeshQuery(to, null);

            Assert.AreEqual(oracle.FindTriangle(probeXZ), query.FindTriangle(probeXZ),
                "FindTriangle differs from a fresh query");

            FPVector2 expectedClosest = oracle.ClosestPointOnNavMesh(outsideXZ, out int expectedTri);
            FPVector2 actualClosest = query.ClosestPointOnNavMesh(outsideXZ, out int actualTri);
            Assert.AreEqual(expectedClosest, actualClosest, "ClosestPointOnNavMesh differs from a fresh query");
            Assert.AreEqual(expectedTri, actualTri);

            bool expectedHit = oracle.Raycast(rayOrigin, rayDir, out FPVector3 expectedPoint, out int expectedRayTri);
            bool actualHit = query.Raycast(rayOrigin, rayDir, out FPVector3 actualPoint, out int actualRayTri);
            Assert.AreEqual(expectedHit, actualHit, "Raycast differs from a fresh query");
            Assert.AreEqual(expectedPoint, actualPoint);
            Assert.AreEqual(expectedRayTri, actualRayTri);

            int startTri = oracle.FindTriangle(moveFrom.ToXZ());
            var expectedMove = oracle.MoveAlongSurface(moveFrom, moveTo, startTri, FP64.MaxValue);
            var actualMove = query.MoveAlongSurface(moveFrom, moveTo, startTri, FP64.MaxValue);
            Assert.AreEqual(expectedMove.resultPos, actualMove.resultPos, "MoveAlongSurface differs from a fresh query");
            Assert.AreEqual(expectedMove.resultTri, actualMove.resultTri);
        }

        // ── V5 — the heap survives a grow ────────────────────────────────────────

        [Test]
        public void V5_HeapGrow_RefillsPositionsWithMinusOne()
        {
            // Use the heap on the small mesh first, then grow into a mesh that needs a bigger one.
            FPNavMesh small = SmallerMesh();
            FPNavMesh large = LargerMesh();

            var (q, pf) = Fresh(small);
            PathOn(pf, out _);

            q.Rebind(large);
            pf.Rebind(large);

            int[] path = PathOn(pf, out bool ok);
            Assert.IsTrue(ok,
                "a grown heap must refill _positions with -1. Left at zero, Contains reads every "
                + "triangle as 'already in the heap at slot 0', nothing is ever pushed, and the "
                + "search empties after the start node");
            Assert.Greater(path.Length, 0);

            AssertSamePath(large, pf, "heap grow");
        }

        // ── V5b — the funnel is rebound too ──────────────────────────────────────

        [Test]
        public void V5b_ReboundFunnel_MatchesFresh()
        {
            // V3 cannot see this: the corridor is the pathfinder's output and never passes
            // through the funnel, so dropping FPNavMeshFunnel.Rebind entirely leaves V3 green
            // while corners keep being traced against the mesh that was replaced.
            FPNavMesh from = SameSizeMesh();
            FPNavMesh to = LargerMesh();

            var q = new FPNavMeshQuery(from, null);
            var pf = new FPNavMeshPathfinder(from, q, null);
            var funnel = new FPNavMeshFunnel(from, q, null);

            q.Rebind(to);
            pf.Rebind(to);
            funnel.Rebind(to);

            int[] corridor = PathOn(pf, out bool ok);
            Assert.IsTrue(ok, "the probe path must succeed on the new mesh");

            var oracleQuery = new FPNavMeshQuery(to, null);
            var oracleFunnel = new FPNavMeshFunnel(to, oracleQuery, null);

            int expectedCount = oracleFunnel.FindCorners(corridor, corridor.Length, Start, End, 16);
            var expected = new FPVector3[expectedCount];
            System.Array.Copy(oracleFunnel.Corners, expected, expectedCount);

            int actualCount = funnel.FindCorners(corridor, corridor.Length, Start, End, 16);

            Assert.AreEqual(expectedCount, actualCount, "corner count differs from a fresh funnel");
            for (int i = 0; i < expectedCount; i++)
                Assert.AreEqual(expected[i], funnel.Corners[i], $"corner[{i}] differs from a fresh funnel");
        }

        // ── V5c — generations accumulate across successive rebinds ───────────────

        [Test]
        public void V5c_SuccessiveRebinds_StillMatchFresh()
        {
            // A real match places buildings repeatedly. The four-argument path builds new objects
            // every time and so resets the counters; this path is the one where they accumulate,
            // which makes repetition the interesting case rather than an incidental one.
            FPNavMesh[] meshes = { SameSizeMesh(), SmallerMesh(), LargerMesh(), SameSizeMesh() };

            var (q, pf) = Fresh(meshes[0]);
            PathOn(pf, out _);

            for (int i = 1; i < meshes.Length; i++)
            {
                q.Rebind(meshes[i]);
                pf.Rebind(meshes[i]);
                PathOn(pf, out _);   // exercise, so the next rebind inherits used state
            }

            AssertSamePath(meshes[meshes.Length - 1], pf, $"{meshes.Length - 1} successive rebinds");
        }
    }
}
