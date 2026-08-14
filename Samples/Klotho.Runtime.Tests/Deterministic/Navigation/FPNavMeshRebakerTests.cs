using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Runtime rebake orchestrator tests. Base fixture: a 20x20
    /// walkable square baked with agent radius 0.5. Pins: exact area conservation (no building /
    /// building = base minus expanded hole), clearance expansion (⊕R_bake, conservative),
    /// placement rejection (touching pair, boundary contact, outside), bit-exact bake metadata
    /// inheritance, uniform areaMask/cost inheritance, bit-identical determinism, fingerprint
    /// sensitivity, and connectivity around a carved building.
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakerTests
    {
        #region Fixture

        private static FPNavMesh BuildBase()
        {
            // 20x20 square, corner + edge-mid + interior vertices (all integer world = on-grid).
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
            {
                for (int z = -10; z <= 10; z += 5)
                    pts.Add((x, z));
            }

            var vertices = new FPVector3[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));

            // Triangulate the point grid via the CDT itself (erase=false keeps the full hull —
            // the square). This gives a clean single-level base without hand-writing indices.
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);

            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null,
                bakeAgentRadius: 0.5);
        }

        private static BigInteger DoubledArea(FPNavMesh mesh)
        {
            // Exact 2x total XZ area over snapped integer coordinates.
            BigInteger sum = 0;
            foreach (var t in mesh.Triangles)
            {
                long ax = FPGeoPredicates.Snap(mesh.Vertices[t.v0].x), az = FPGeoPredicates.Snap(mesh.Vertices[t.v0].z);
                long bx = FPGeoPredicates.Snap(mesh.Vertices[t.v1].x), bz = FPGeoPredicates.Snap(mesh.Vertices[t.v1].z);
                long cx = FPGeoPredicates.Snap(mesh.Vertices[t.v2].x), cz = FPGeoPredicates.Snap(mesh.Vertices[t.v2].z);
                BigInteger cross = (BigInteger)(bx - ax) * (cz - az) - (BigInteger)(bz - az) * (cx - ax);
                sum += BigInteger.Abs(cross);
            }
            return sum;
        }

        private static FPBuildingRect Rect(double minX, double minZ, double maxX, double maxZ)
        {
            return new FPBuildingRect(
                FP64.FromDouble(minX), FP64.FromDouble(minZ),
                FP64.FromDouble(maxX), FP64.FromDouble(maxZ), FP64.Zero);
        }

        private static int CountConnectedComponents(FPNavMesh mesh)
        {
            int n = mesh.Triangles.Length;
            var seen = new bool[n];
            var stack = new Stack<int>();
            int components = 0;
            for (int s = 0; s < n; s++)
            {
                if (seen[s])
                    continue;
                components++;
                stack.Push(s);
                seen[s] = true;
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    for (int e = 0; e < 3; e++)
                    {
                        int nb = mesh.Triangles[cur].GetNeighbor(e);
                        if (nb >= 0 && !seen[nb])
                        {
                            seen[nb] = true;
                            stack.Push(nb);
                        }
                    }
                }
            }
            return components;
        }

        #endregion

        [Test]
        public void Rebake_NoBuildings_PreservesAreaAndMetadata()
        {
            FPNavMesh baseMesh = BuildBase();
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, null);

            Assert.AreEqual(DoubledArea(baseMesh), DoubledArea(rebaked), "walkable area must be conserved exactly");
            Assert.AreEqual(baseMesh.BakeAgentRadius.RawValue, rebaked.BakeAgentRadius.RawValue, "meta must inherit bit-exactly");
            Assert.AreEqual(baseMesh.GridCellSize.RawValue, rebaked.GridCellSize.RawValue);
            Assert.AreEqual(1, CountConnectedComponents(rebaked));
        }

        [Test]
        public void Rebake_OneBuilding_CarvesExpandedHole_Exactly()
        {
            FPNavMesh baseMesh = BuildBase();
            // 2x2 building at center; radius 0.5 → expanded hole exactly 3x3 (grid-exact bounds).
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { Rect(-1, -1, 1, 1) });

            long cell = 1;
            BigInteger holeDoubled = 2 * (BigInteger)(3 * 1024) * (3 * 1024) * cell;
            Assert.AreEqual(DoubledArea(baseMesh) - holeDoubled, DoubledArea(rebaked),
                "carved area must equal the radius-expanded footprint exactly");

            // Walkable stays connected around the building; nothing inside the hole.
            Assert.AreEqual(1, CountConnectedComponents(rebaked));
            foreach (var t in rebaked.Triangles)
            {
                long cx = FPGeoPredicates.Snap(rebaked.Vertices[t.v0].x)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v1].x)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v2].x);
                long cz = FPGeoPredicates.Snap(rebaked.Vertices[t.v0].z)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v1].z)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v2].z);
                long h = 3 * 1536; // 1.5 world units * 1024, times 3 (centroid trick)
                bool insideHole = cx > -h && cx < h && cz > -h && cz < h;
                Assert.IsFalse(insideHole, "no triangle centroid may fall inside the carved hole");
            }
        }

        [Test]
        public void Rebake_TouchingBuildings_Rejected()
        {
            FPNavMesh baseMesh = BuildBase();
            // Two 2x2 buildings 1.0 apart: radius expansion (0.5 each side) makes them touch exactly.
            var buildings = new[] { Rect(-4, -1, -2, 1), Rect(-1, -1, 1, 1) };
            Assert.Throws<InvalidOperationException>(() => FPNavMeshRebaker.Rebake(baseMesh, buildings));
        }

        [Test]
        public void Rebake_SeparatedBuildings_Succeeds()
        {
            FPNavMesh baseMesh = BuildBase();
            // Same pair but with one extra grid cell of separation after expansion.
            var buildings = new[] { Rect(-5, -1, -3, 1), Rect(-1, -1, 1, 1) };
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, buildings);
            Assert.AreEqual(1, CountConnectedComponents(rebaked));
            Assert.Greater(baseMesh.Triangles.Length, 0);
            Assert.Greater(rebaked.Triangles.Length, baseMesh.Triangles.Length,
                "two holes must add boundary triangles");
        }

        [Test]
        public void Rebake_BoundaryContactOrOutside_Rejected()
        {
            FPNavMesh baseMesh = BuildBase();
            // Expanded rect reaches x = -10 exactly → touches the outer boundary → reject.
            Assert.Throws<InvalidOperationException>(
                () => FPNavMeshRebaker.Rebake(baseMesh, new[] { Rect(-9.5, -1, -8, 1) }));
            // Fully outside the walkable square → reject.
            Assert.Throws<InvalidOperationException>(
                () => FPNavMeshRebaker.Rebake(baseMesh, new[] { Rect(20, 20, 22, 22) }));
        }

        [Test]
        public void Rebake_Determinism_BitIdentical_AndFingerprint()
        {
            FPNavMesh baseMesh = BuildBase();
            var buildings = new[] { Rect(-1, -1, 1, 1), Rect(4, 4, 6, 6) };

            FPNavMesh a = FPNavMeshRebaker.Rebake(baseMesh, buildings);
            FPNavMesh b = FPNavMeshRebaker.Rebake(baseMesh, buildings);

            Assert.AreEqual(a.Vertices.Length, b.Vertices.Length);
            for (int i = 0; i < a.Vertices.Length; i++)
            {
                Assert.AreEqual(a.Vertices[i].x.RawValue, b.Vertices[i].x.RawValue);
                Assert.AreEqual(a.Vertices[i].y.RawValue, b.Vertices[i].y.RawValue);
                Assert.AreEqual(a.Vertices[i].z.RawValue, b.Vertices[i].z.RawValue);
            }
            Assert.AreEqual(a.Triangles.Length, b.Triangles.Length);
            for (int i = 0; i < a.Triangles.Length; i++)
            {
                Assert.AreEqual(a.Triangles[i].v0, b.Triangles[i].v0);
                Assert.AreEqual(a.Triangles[i].v1, b.Triangles[i].v1);
                Assert.AreEqual(a.Triangles[i].v2, b.Triangles[i].v2);
            }

            ulong fa = FPNavMeshRebaker.ComputeFingerprint(a);
            Assert.AreEqual(fa, FPNavMeshRebaker.ComputeFingerprint(b), "identical rebakes must fingerprint-match");

            FPNavMesh c = FPNavMeshRebaker.Rebake(baseMesh, new[] { Rect(-1, -1, 1, 1) });
            Assert.AreNotEqual(fa, FPNavMeshRebaker.ComputeFingerprint(c), "different building sets must differ");
        }

        [Test]
        public void Rebake_UniformAreaAttributes_Inherited()
        {
            FPNavMesh baseMesh = BuildBase();
            // Base pipeline assigns areaMask = 1<<0 and cost 1 uniformly; mutate cost to verify
            // inheritance of a non-default uniform value.
            for (int i = 0; i < baseMesh.Triangles.Length; i++)
                baseMesh.TrianglesMutable[i].costMultiplier = FP64.FromDouble(2.5);

            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { Rect(-1, -1, 1, 1) });
            foreach (var t in rebaked.Triangles)
            {
                Assert.AreEqual(1 << 0, t.areaMask);
                Assert.AreEqual(FP64.FromDouble(2.5).RawValue, t.costMultiplier.RawValue);
            }
        }

        [Test]
        public void Rebake_NonSnappedBase_Rejected()
        {
            // Hand-built mesh with an off-grid vertex bypassing the pipeline snap is not
            // constructible via Build (it snaps); simulate by unsnapped base rejection contract:
            FPNavMesh baseMesh = BuildBase();
            var vertices = baseMesh.Vertices.ToArray();
            vertices[0] = new FPVector3(FP64.FromRaw(vertices[0].x.RawValue + 1), vertices[0].y, vertices[0].z);
            var offGrid = new FPNavMesh(
                vertices, baseMesh.Triangles.ToArray(), baseMesh.BoundsXZ,
                baseMesh.GridCells.ToArray(), baseMesh.GridTriangles.ToArray(),
                baseMesh.GridWidth, baseMesh.GridHeight, baseMesh.GridCellSize, baseMesh.GridOrigin,
                baseMesh.BakeAgentRadius, baseMesh.BakeMaxSlopeDeg,
                baseMesh.BakeAgentHeight, baseMesh.BakeAgentClimb);

            Assert.Throws<NotSupportedException>(() => FPNavMeshRebaker.Rebake(offGrid, null));
        }

        [Test]
        public void CreateSnapshot_EmbedsPrewarm_OptOutAvailable()
        {
            FPNavMesh baseMesh = BuildBase();

            var capture = new LogCapture();
            FPNavMeshRebaker.CreateSnapshot(baseMesh, capture);
            Assert.IsTrue(capture.Contains(KLogLevel.Information, "prewarmed"),
                "default CreateSnapshot must run the embedded prewarm (non-IL2CPP build)");

            var silent = new LogCapture();
            FPNavMeshRebaker.CreateSnapshot(baseMesh, silent, prewarm: false);
            Assert.IsFalse(silent.Contains(KLogLevel.Information, "prewarmed"),
                "prewarm: false must skip the warming step");
        }

        [Test]
        public void CreateSnapshot_UnsupportedBase_Throws()
        {
            // Cache construction failure = rebake feature unavailable — must throw at load
            // time (contrast: Prewarm stays best-effort). Multi-level = XZ-duplicate base.
            FPNavMesh baseMesh = BuildBase();
            var vertices = new FPVector3[baseMesh.Vertices.Length + 1];
            baseMesh.Vertices.CopyTo(vertices);
            var dup = baseMesh.Vertices[0];
            vertices[vertices.Length - 1] = new FPVector3(dup.x, dup.y + FP64.FromInt(3), dup.z);
            var multiLevel = new FPNavMesh(
                vertices, baseMesh.Triangles.ToArray(), baseMesh.BoundsXZ,
                baseMesh.GridCells.ToArray(), baseMesh.GridTriangles.ToArray(),
                baseMesh.GridWidth, baseMesh.GridHeight, baseMesh.GridCellSize, baseMesh.GridOrigin,
                baseMesh.BakeAgentRadius, baseMesh.BakeMaxSlopeDeg,
                baseMesh.BakeAgentHeight, baseMesh.BakeAgentClimb);

            Assert.Throws<NotSupportedException>(() => FPNavMeshRebaker.CreateSnapshot(multiLevel));
        }

        [Test]
        public void Prewarm_NullSnapshot_NoOp()
        {
            Assert.DoesNotThrow(() => FPNavMeshRebaker.Prewarm(null));
        }

        // ── buildingCount: a reused buffer instead of an exact-size copy ─────
        // Mirrors FPNavMeshHexagonPlacementTests' placementCount pair. The rect path is the one
        // the guide presents as the simplest entry ("a rectangle needs no catalog"), so it was
        // the one still forcing an allocation per placement.

        [Test]
        public void BuildingCount_LetsAnOversizedBufferStandInForAnExactOne()
        {
            FPNavMesh baseMesh = BuildBase();

            var exact = new[] { Rect(-6, -6, -4, -4), Rect(2, 2, 4, 4) };

            // Same two at the front, then tail entries that must never be read. They are legal
            // placements on their own — the point is that they are ignored, not that they would
            // be rejected.
            var padded = new FPBuildingRect[5];
            padded[0] = exact[0];
            padded[1] = exact[1];
            padded[2] = Rect(-6, 2, -4, 4);
            padded[3] = Rect(2, -6, 4, -4);
            padded[4] = Rect(-1, -1, 0, 0);

            FPNavMesh fromExact = FPNavMeshRebaker.Rebake(baseMesh, exact);
            FPNavMesh fromPadded = FPNavMeshRebaker.Rebake(baseMesh, padded, null, default, 2);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(fromExact),
                FPNavMeshRebaker.ComputeFingerprint(fromPadded),
                "the tail past buildingCount must not reach the rebake at all");

            // Control: without the count the same buffer is a different input. Without this, a
            // green above could just mean the padding happened not to matter.
            FPNavMesh fromWholeBuffer = FPNavMeshRebaker.Rebake(baseMesh, padded);
            Assert.AreNotEqual(
                FPNavMeshRebaker.ComputeFingerprint(fromExact),
                FPNavMeshRebaker.ComputeFingerprint(fromWholeBuffer),
                "the padding was chosen to change the mesh — if it does not, this test proves nothing");
        }

        [Test]
        public void BuildingCount_PastTheArray_IsRefused()
        {
            FPNavMesh baseMesh = BuildBase();
            var two = new[] { Rect(-6, -6, -4, -4), Rect(2, 2, 4, 4) };
            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.Rebake(baseMesh, two, null, default, 3));
            StringAssert.Contains("buildingCount", ex.Message);
        }

        [Test]
        public void BuildingCount_NegativeOtherThanTheSentinel_IsRefused()
        {
            // -1 is "the whole array". Folding every negative into it would turn a caller whose
            // count arithmetic went wrong into a full-buffer rebake — tail included — and a
            // reusable buffer's tail is the previous rebake's rects, which are mutually legal by
            // construction. Nothing downstream would object, and every peer would agree on the
            // wrong mesh.
            FPNavMesh baseMesh = BuildBase();
            var two = new[] { Rect(-6, -6, -4, -4), Rect(2, 2, 4, 4) };

            Assert.DoesNotThrow(() => FPNavMeshRebaker.Rebake(baseMesh, two, null, default, -1));

            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.Rebake(baseMesh, two, null, default, -2));
            StringAssert.Contains("buildingCount", ex.Message);
        }
    }
}
