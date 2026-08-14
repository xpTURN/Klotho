using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Predicate-grid snap at the bake input stage.
    /// Validates: output grid membership (incl. through T-junction splits — the pipeline
    /// must never create new coordinates), idempotence for on-grid input, deterministic
    /// re-weld of snap coincidences (adjacency healing), multi-level Y preservation
    /// (no cross-floor welding), and orientation-flip detection.
    /// </summary>
    [TestFixture]
    public class FPNavMeshBuildPipelineSnapTests
    {
        private const long GRID_MASK = (1L << FPGeoPredicates.SNAP_SHIFT) - 1;

        private static bool OnGrid(FP64 v)
        {
            return (v.RawValue & GRID_MASK) == 0;
        }

        private static void AssertAllVerticesOnGrid(FPNavMesh mesh)
        {
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                Assert.IsTrue(OnGrid(mesh.Vertices[i].x), $"vertex {i} x off-grid: raw={mesh.Vertices[i].x.RawValue}");
                Assert.IsTrue(OnGrid(mesh.Vertices[i].z), $"vertex {i} z off-grid: raw={mesh.Vertices[i].z.RawValue}");
            }
        }

        private static FPVector3 V(double x, double y, double z)
        {
            return new FPVector3(FP64.FromDouble(x), FP64.FromDouble(y), FP64.FromDouble(z));
        }

        [Test]
        public void Build_OffGridInput_OutputOnGrid_YPreserved()
        {
            // Messy off-grid quad (two triangles). Y carries an arbitrary fraction that must
            // survive bit-exactly (snap is XZ-only).
            FP64 y = FP64.FromDouble(0.1234567);
            var vertices = new[]
            {
                new FPVector3(FP64.FromDouble(0.00071), y, FP64.FromDouble(0.00033)),
                new FPVector3(FP64.FromDouble(9.99931), y, FP64.FromDouble(0.00089)),
                new FPVector3(FP64.FromDouble(10.00077), y, FP64.FromDouble(10.00013)),
                new FPVector3(FP64.FromDouble(-0.00059), y, FP64.FromDouble(9.99991)),
            };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            var areas = new[] { 0, 0 };

            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, indices, areas, 1.0);

            Assert.AreEqual(2, mesh.Triangles.Length);
            AssertAllVerticesOnGrid(mesh);
            for (int i = 0; i < mesh.Vertices.Length; i++)
                Assert.AreEqual(y.RawValue, mesh.Vertices[i].y.RawValue, $"vertex {i} y changed");
        }

        [Test]
        public void Build_OnGridInput_Idempotent_BitIdentical()
        {
            // Integer world coordinates are on-grid by construction (fraction bits below
            // SNAP_SHIFT are zero) -> snap must be a bit-exact no-op, twice.
            var vertices = new[] { V(0, 0, 0), V(10, 0, 0), V(10, 0, 10), V(0, 0, 10) };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            var areas = new[] { 0, 0 };

            FPNavMesh first = FPNavMeshBuildPipeline.Build(
                (FPVector3[])vertices.Clone(), (int[])indices.Clone(), (int[])areas.Clone(), 1.0);
            FPNavMesh second = FPNavMeshBuildPipeline.Build(
                first.Vertices.ToArray(), new[] { 0, 1, 2, 0, 2, 3 }, new[] { 0, 0 }, 1.0);

            Assert.AreEqual(2, first.Triangles.Length);
            Assert.AreEqual(vertices.Length, first.Vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.AreEqual(vertices[i].x.RawValue, first.Vertices[i].x.RawValue);
                Assert.AreEqual(vertices[i].z.RawValue, first.Vertices[i].z.RawValue);
                Assert.AreEqual(first.Vertices[i].x.RawValue, second.Vertices[i].x.RawValue);
                Assert.AreEqual(first.Vertices[i].z.RawValue, second.Vertices[i].z.RawValue);
            }
        }

        [Test]
        public void Build_SnapCoincidence_WeldsAndHealsAdjacency()
        {
            // tri2 references duplicate corners A' ~ A and C' ~ C (within one grid cell, same Y).
            // Pre-snap the shared edge exists only geometrically (distinct indices -> no adjacency);
            // post-snap weld remaps A'->A, C'->C and adjacency must form across edge (A, C).
            double sub = 0.0001; // < 1/1024 world unit
            var vertices = new[]
            {
                V(0, 0, 0),          // 0: A
                V(2, 0, 0),          // 1: B
                V(2, 0, 2),          // 2: C
                V(sub, 0, sub),      // 3: A' (same cell as A)
                V(2 + sub, 0, 2 + sub), // 4: C' (same cell as C)
                V(0, 0, 2),          // 5: D
            };
            var indices = new[] { 0, 1, 2, 3, 4, 5 }; // tri1=(A,B,C), tri2=(A',C',D)
            var areas = new[] { 0, 0 };

            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, indices, areas, 1.0);

            Assert.AreEqual(2, mesh.Triangles.Length, "both triangles must survive");
            AssertAllVerticesOnGrid(mesh);

            // Exactly one linked edge per triangle: the healed diagonal.
            int linked0 = CountNeighbors(mesh.Triangles[0]);
            int linked1 = CountNeighbors(mesh.Triangles[1]);
            Assert.AreEqual(1, linked0, "tri0 must gain exactly one neighbor via weld");
            Assert.AreEqual(1, linked1, "tri1 must gain exactly one neighbor via weld");
        }

        [Test]
        public void Build_MultiLevel_SameXZDifferentY_NotWelded()
        {
            // Two stacked floors sharing the same XZ footprint. The weld key includes Y,
            // so nothing merges and both floors survive with their own Y ranges.
            var vertices = new[]
            {
                V(0.0003, 0, 0.0003), V(4.0007, 0, 0.0001), V(3.9991, 0, 4.0009),
                V(0.0003, 5, 0.0003), V(4.0007, 5, 0.0001), V(3.9991, 5, 4.0009),
            };
            var indices = new[] { 0, 1, 2, 3, 4, 5 };
            var areas = new[] { 0, 0 };

            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, indices, areas, 1.0);

            Assert.AreEqual(2, mesh.Triangles.Length);
            AssertAllVerticesOnGrid(mesh);
            Assert.AreEqual(0, mesh.Triangles[0].minY.ToInt());
            Assert.AreEqual(5, mesh.Triangles[1].minY.ToInt());
            // Stacked floors stay disconnected.
            Assert.AreEqual(0, CountNeighbors(mesh.Triangles[0]));
            Assert.AreEqual(0, CountNeighbors(mesh.Triangles[1]));
        }

        [Test]
        public void Build_TJunctionSplit_OutputStaysOnGrid()
        {
            // Off-grid T-junction: vertex 4 sits on the shared long edge of the right quad,
            // forcing a split. Splits reuse existing vertices only, so grid membership must
            // survive the whole pipeline (the grid-membership invariant).
            var vertices = new[]
            {
                V(0.0004, 0, 0.0009),   // 0
                V(4.0002, 0, 0.0004),   // 1
                V(4.0006, 0, 4.0001),   // 2
                V(0.0001, 0, 4.0008),   // 3
                V(4.0004, 0, 2.0003),   // 4: on edge (1)-(2) -> T-junction against left quad
                V(8.0009, 0, 0.0002),   // 5
                V(8.0001, 0, 4.0006),   // 6
            };
            var indices = new[]
            {
                0, 1, 2, 0, 2, 3,   // left quad (edge 1-2 will need splitting at vertex 4)
                1, 5, 4, 5, 6, 4,   // right quads referencing the mid vertex 4
            };
            var areas = new[] { 0, 0, 0, 0 };

            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, indices, areas, 1.0);

            Assert.Greater(mesh.Triangles.Length, 4, "T-junction split must add triangles");
            AssertAllVerticesOnGrid(mesh);
        }

        [Test]
        public void Build_OrientationFlip_ReportedViaLogError()
        {
            // Sliver thinner than one grid cell arranged so floor-snap moves its apex across
            // the base: (0.9, 0.9), (2.1, 1.9), (3.9, 2.9) in cell units flips sign after
            // flooring to (0,0), (2,1), (3,2). Detection must fire; the triangle itself is
            // sub-epsilon and gets degenerate-removed.
            long cell = 1L << FPGeoPredicates.SNAP_SHIFT;
            FP64 C(double cells) => FP64.FromRaw((long)(cells * cell));

            var vertices = new[]
            {
                new FPVector3(C(0.9), FP64.Zero, C(0.9)),
                new FPVector3(C(2.1), FP64.Zero, C(1.9)),
                new FPVector3(C(3.9), FP64.Zero, C(2.9)),
            };
            var indices = new[] { 0, 1, 2 };
            var areas = new[] { 0 };

            var capture = new LogCapture();
            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, indices, areas, 1.0, capture);

            Assert.AreEqual(0, mesh.Triangles.Length, "sub-epsilon sliver must be degenerate-removed");
            Assert.IsTrue(capture.Contains(KLogLevel.Error, "flipped"),
                "orientation flip must be reported via IKLogger.KError");
        }

        private static int CountNeighbors(FPNavMeshTriangle tri)
        {
            int count = 0;
            for (int e = 0; e < 3; e++)
            {
                if (tri.GetNeighbor(e) >= 0)
                    count++;
            }
            return count;
        }
    }
}
