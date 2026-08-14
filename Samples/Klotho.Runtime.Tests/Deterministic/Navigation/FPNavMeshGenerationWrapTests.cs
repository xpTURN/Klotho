using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// V4c — the generation counters wrap safely.
    ///
    /// <para>Three counters stamp "seen during this search" into a parallel array and are only
    /// ever incremented. Until now three of the four had no wrap handling, and nothing noticed
    /// because every navmesh swap built new objects and so reset them to zero. Rebinding keeps the
    /// objects, which makes the counters monotonic for the whole life of a room — so the guards go
    /// in with the rebind, and these tests are what say they work.</para>
    ///
    /// <para>The aliasing itself cannot be reached from a test — it needs the counter to travel
    /// all the way around, some four billion queries. So what these pin is the guard's contract,
    /// and each test carries two assertions because there are two ways to get it wrong:</para>
    ///
    /// <list type="bullet">
    /// <item>the counter lands on 1 — catches no guard at all, and catches restarting at 0, which
    /// would hand out the "never seen" sentinel as a live generation;</item>
    /// <item>the answer is unchanged — catches restarting at 1 <b>without clearing the stamps</b>,
    /// where the first run's marks read as "already seen in this search" and every triangle they
    /// cover is skipped.</item>
    /// </list>
    ///
    /// <para>Each test therefore drives one query at generation 1, arms the counter one short of
    /// the brink, and drives the same query again.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshGenerationWrapTests
    {
        private static readonly FieldInfo QueryGenField = typeof(FPNavMeshQuery)
            .GetField("_queryGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RaycastGenField = typeof(FPNavMeshQuery)
            .GetField("_raycastGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PathfinderGenField = typeof(FPNavMeshPathfinder)
            .GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance);

        private static FPNavMesh BuildMesh()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
            {
                for (int z = -10; z <= 10; z += 5)
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

        /// <summary>Sets the counter one short of the brink, so the next ++ trips the guard.</summary>
        private static void ArmWrap(FieldInfo field, object target)
        {
            field.SetValue(target, int.MaxValue - 1);
        }

        [Test]
        public void QueryGeneration_WrapsWithoutAliasing()
        {
            FPNavMesh mesh = BuildMesh();
            var query = new FPNavMeshQuery(mesh, null);

            // Outside the mesh, so the cell scan that uses _queryGeneration actually runs.
            var outside = new FPVector2(FP64.FromInt(11), FP64.Zero);
            FPVector2 before = query.ClosestPointOnNavMesh(outside, out int triBefore);
            Assert.GreaterOrEqual(triBefore, 0, "the probe must reach the scan — otherwise nothing is stamped");

            ArmWrap(QueryGenField, query);
            FPVector2 after = query.ClosestPointOnNavMesh(outside, out int triAfter);

            Assert.AreEqual(before, after,
                "restarting at 1 without clearing the stamps makes the first call's marks read as "
                + "already-seen, so every triangle they cover is skipped");
            Assert.AreEqual(triBefore, triAfter);
            Assert.AreEqual(1, (int)QueryGenField.GetValue(query),
                "must restart at 1, not 0 — 0 is the 'never seen' sentinel");
        }

        [Test]
        public void RaycastGeneration_WrapsWithoutAliasing()
        {
            FPNavMesh mesh = BuildMesh();
            var query = new FPNavMeshQuery(mesh, null);

            var origin = new FPVector3(FP64.FromInt(-9), FP64.Zero, FP64.Zero);
            var dir = new FPVector3(FP64.FromInt(30), FP64.Zero, FP64.Zero);

            bool hitBefore = query.Raycast(origin, dir, out FPVector3 pointBefore, out int triBefore);

            ArmWrap(RaycastGenField, query);
            bool hitAfter = query.Raycast(origin, dir, out FPVector3 pointAfter, out int triAfter);

            Assert.AreEqual(hitBefore, hitAfter, "the wrap must not change whether the ray hits");
            Assert.AreEqual(pointBefore, pointAfter);
            Assert.AreEqual(triBefore, triAfter);
            Assert.AreEqual(1, (int)RaycastGenField.GetValue(query),
                "must restart at 1, not 0 — 0 is the 'never seen' sentinel");
        }

        [Test]
        public void PathfinderGeneration_WrapsWithoutAliasing()
        {
            FPNavMesh mesh = BuildMesh();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            var start = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.FromInt(-8));
            var end = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(8));

            bool okBefore = pathfinder.FindPath(start, end, ~0, out int[] corridorBefore, out int lenBefore);
            Assert.IsTrue(okBefore, "the probe path must succeed — a failed path proves nothing");
            var snapshot = new int[lenBefore];
            System.Array.Copy(corridorBefore, snapshot, lenBefore);

            ArmWrap(PathfinderGenField, pathfinder);
            bool okAfter = pathfinder.FindPath(start, end, ~0, out int[] corridorAfter, out int lenAfter);

            Assert.IsTrue(okAfter, "the wrap must not lose the path");
            Assert.AreEqual(lenBefore, lenAfter);
            for (int i = 0; i < lenBefore; i++)
                Assert.AreEqual(snapshot[i], corridorAfter[i], $"corridor[{i}] diverged across the wrap");
            Assert.AreEqual(1, (int)PathfinderGenField.GetValue(pathfinder),
                "must restart at 1, not 0 — TouchNode treats 0 as 'never touched'");
        }
    }
}
