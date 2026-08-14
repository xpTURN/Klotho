using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Output-array recycling.
    ///
    /// Recycling is opt-in per swap: <see cref="FPNavMeshRebakeContext.CommitSwap"/> is the only
    /// thing that retires a mesh, and it accepts only the context's own most recent output. Those
    /// two facts are what keep the base mesh (which the snapshot re-reads every rebake) and the
    /// currently-live mesh out of the recycling chain — the two design traps this
    /// spent the most words on.
    ///
    /// In DEBUG the retired arrays are poisoned, so a holder still reading the old mesh produces
    /// visibly wrong output. That matters more here than usual: the failures this guards against
    /// are deterministic — every peer computes the same wrong answer — so no desync check would
    /// ever report them.
    /// </summary>
    [TestFixture]
    public class FPNavMeshOutputRecycleTests
    {
        #region Fixture

        private static FPNavMesh BuildBase(int half = 8)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    pts.Add((x, z));

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

        private static FPBuildingRect Rect(double minX, double minZ, double maxX, double maxZ)
        {
            return new FPBuildingRect(
                FP64.FromDouble(minX), FP64.FromDouble(minZ),
                FP64.FromDouble(maxX), FP64.FromDouble(maxZ), FP64.Zero);
        }

        private static FPBuildingRect[] Set(int n)
        {
            var all = new[]
            {
                Rect(-5, -5, -3, -3),
                Rect(1, 1, 3, 3),
                Rect(-5, 2, -3, 4),
                Rect(2, -5, 4, -3),
            };
            var take = new FPBuildingRect[n];
            Array.Copy(all, take, n);
            return take;
        }

        /// <summary>Rebake + install, i.e. what a game seam does around a successful placement.</summary>
        private static FPNavMesh RebakeAndSwap(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings)
        {
            FPNavMesh mesh = FPNavMeshRebaker.Rebake(ctx, buildings);
            ctx.CommitSwap(mesh);
            return mesh;
        }

        #endregion

        [Test]
        public void RecycledOutput_IsBitIdenticalToFresh()
        {
            // A/B: the recycling chain must not change a single value. Run the same building
            // sequence with and without commits (no commit = nothing is ever retired = no reuse).
            FPNavMesh baseMesh = BuildBase();

            var recycling = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            var fresh = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            for (int n = 1; n <= 4; n++)
            {
                FPNavMesh withReuse = RebakeAndSwap(recycling, Set(n));
                FPNavMesh withoutReuse = FPNavMeshRebaker.Rebake(fresh, Set(n));

                Assert.AreEqual(
                    FPNavMeshRebaker.ComputeFingerprint(withoutReuse),
                    FPNavMeshRebaker.ComputeFingerprint(withReuse),
                    $"recycled output diverged at {n} building(s)");
                Assert.AreEqual(withoutReuse.TriangleCount, withReuse.TriangleCount);
                Assert.AreEqual(withoutReuse.VertexCount, withReuse.VertexCount);

                for (int i = 0; i < withoutReuse.TriangleCount; i++)
                {
                    FPNavMeshTriangle a = withoutReuse.Triangles[i];
                    FPNavMeshTriangle b = withReuse.Triangles[i];
                    Assert.AreEqual(a.v0, b.v0, $"tri {i}.v0 at {n} building(s)");
                    Assert.AreEqual(a.v1, b.v1, $"tri {i}.v1 at {n} building(s)");
                    Assert.AreEqual(a.v2, b.v2, $"tri {i}.v2 at {n} building(s)");
                    for (int e = 0; e < 3; e++)
                        Assert.AreEqual(a.GetNeighbor(e), b.GetNeighbor(e), $"tri {i} nb {e} at {n}");
                }
            }
        }

        [Test]
        public void Storage_IsActuallyReused_AfterTwoCommits()
        {
            // The point of the whole exercise. One generation of lag is by design: the mesh
            // retired by commit N+1 is the one produced at N, so it can only be handed out at
            // N+2 — which is exactly what keeps a live mesh from being written through.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            FPNavMesh m1 = RebakeAndSwap(ctx, Set(1));
            m1.GetBackingArrays(out var v1, out var t1, out _, out _);

            FPNavMesh m2 = RebakeAndSwap(ctx, Set(1));   // commit here retires m1
            m2.GetBackingArrays(out var v2, out var t2, out _, out _);
            Assert.AreNotSame(v1, v2, "m2 must not reuse the still-live m1 storage");
            Assert.AreNotSame(t1, t2, "m2 must not reuse the still-live m1 storage");

            FPNavMesh m3 = RebakeAndSwap(ctx, Set(1));   // may now take m1's retired arrays
            m3.GetBackingArrays(out var v3, out var t3, out _, out _);
            Assert.AreSame(v1, v3, "m3 should reuse the storage retired at the previous commit");
            Assert.AreSame(t1, t3, "m3 should reuse the storage retired at the previous commit");
        }

        [Test]
        public void LiveMesh_IsNeverRecycled()
        {
            // Recycling at production time would hand out the live mesh's arrays. Walk a
            // long sequence and assert the installed mesh keeps its content after each further
            // rebake — if its storage were ever handed out, the DEBUG poison would show up here.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            FPNavMesh live = RebakeAndSwap(ctx, Set(1));
            ulong liveFp = FPNavMeshRebaker.ComputeFingerprint(live);

            for (int n = 2; n <= 4; n++)
            {
                FPNavMeshRebaker.Rebake(ctx, Set(n));   // produced but NOT installed
                Assert.AreEqual(liveFp, FPNavMeshRebaker.ComputeFingerprint(live),
                    $"the installed mesh changed while rebake #{n} ran");
            }
        }

        [Test]
        public void BaseMesh_IsNeverRecycled()
        {
            // The snapshot re-reads the base on every rebake, so recycling it would corrupt
            // every later rebake. CommitSwap only accepts this context's own output, which makes
            // the base unreachable by construction — this pins the resulting behaviour.
            FPNavMesh baseMesh = BuildBase();
            ulong baseFp = FPNavMeshRebaker.ComputeFingerprint(baseMesh);
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            for (int n = 1; n <= 4; n++)
                RebakeAndSwap(ctx, Set(n));

            Assert.AreEqual(baseFp, FPNavMeshRebaker.ComputeFingerprint(baseMesh),
                "the base mesh must be untouched after any number of rebakes");

            // And it must still rebake correctly from that untouched base.
            var probe = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(baseMesh, Set(1))),
                FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(probe, Set(1))));
        }

        [Test]
        public void RejectedPlacement_LeavesLiveMeshIntact()
        {
            // The worst path: the rebake that validates a placement throws, so nothing is
            // installed. If recycling happened at production time the live mesh would have been
            // destroyed with no replacement.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            FPNavMesh live = RebakeAndSwap(ctx, Set(2));
            ulong liveFp = FPNavMeshRebaker.ComputeFingerprint(live);

            Assert.Throws<InvalidOperationException>(
                () => FPNavMeshRebaker.Rebake(ctx, new[] { Rect(7, 7, 10, 10) }),
                "a placement crossing the walkable boundary must be rejected");

            Assert.AreEqual(liveFp, FPNavMeshRebaker.ComputeFingerprint(live),
                "a rejected placement must not touch the live mesh");

            // …and the chain still works afterwards.
            FPNavMesh next = RebakeAndSwap(ctx, Set(3));
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(baseMesh, Set(3))),
                FPNavMeshRebaker.ComputeFingerprint(next));
        }

        [Test]
        public void RetiredMesh_ThrowsOnAnyRead()
        {
            // Poisoning alone only catches a stale reader until the retired arrays are handed
            // to their next owner — and a growing mesh overwrites the whole logical range, which is
            // the common case. The retirement flag makes detection independent of that: reading a
            // retired mesh throws whatever the arrays now hold.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            FPNavMesh first = RebakeAndSwap(ctx, Set(1));
            Assert.DoesNotThrow(() => { _ = first.Triangles.Length; }, "still live before the next commit");

            RebakeAndSwap(ctx, Set(2));   // this commit retires `first`

#if DEBUG
            Assert.Throws<InvalidOperationException>(() => { _ = first.Triangles.Length; });
            Assert.Throws<InvalidOperationException>(() => { _ = first.Vertices.Length; });
            Assert.Throws<InvalidOperationException>(() => { _ = first.GridCells.Length; });
            Assert.Throws<InvalidOperationException>(() => { _ = first.GridTriangles.Length; });
            Assert.Throws<InvalidOperationException>(() => { _ = first.TrianglesMutable.Length; });
            Assert.Throws<InvalidOperationException>(() => first.GetCellTriangles(0, 0, out _, out _));
#else
            // Release asserts the OTHER half of the contract. FPNavMesh.AssertLive is
            // [Conditional("DEBUG")] on purpose — its comment says the guard call disappears so the
            // query hot path pays nothing — so the throw above cannot happen here, and a test that
            // demanded it would simply be red forever. Pinning the silence is not a weaker check:
            // it is what fails if someone makes the guard unconditional and quietly puts a branch
            // back into every navmesh read.
            Assert.DoesNotThrow(() => { _ = first.Triangles.Length; },
                "AssertLive is [Conditional(\"DEBUG\")] — a release build must not pay for it");
#endif

            // Counts stay readable — they are plain fields, and a diagnostic that reports "this
            // mesh had N triangles" is exactly what you want while chasing a stale reference.
            Assert.Greater(first.TriangleCount, 0);
        }

        [Test]
        public void CommitSwap_RejectsForeignMesh()
        {
            // The guard that makes "only what this context produced" mechanical rather than a
            // convention — handing it the base mesh (the exact trap this design rejects) must throw.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            Assert.Throws<InvalidOperationException>(() => ctx.CommitSwap(baseMesh));

            FPNavMesh mine = FPNavMeshRebaker.Rebake(ctx, Set(1));
            var other = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            Assert.Throws<InvalidOperationException>(() => other.CommitSwap(mine),
                "a context must not retire another context's output");

            ctx.CommitSwap(mine);
            Assert.Throws<InvalidOperationException>(() => ctx.CommitSwap(mine),
                "committing the same mesh twice would retire the live mesh");
        }

#if DEBUG
        [Test]
        public void RecycledTail_MustNotLeakIntoTheContractVerifier()
        {
            // The recycling this file is about has a second edge, and it points at the DEBUG
            // contract verifier. RentOutputVertices hands back an OVERSIZE array
            // (minSize + minSize/4 + 16) and the rebake fills only [0, vertexCount) — the doc on
            // that rent says so, and calls everything past the count "the previous mesh's data".
            //
            // The verifier used to take the array with no count, so it walked that tail:
            //   * fresh array  -> tail is (0,0,0), bogus T-junction candidates on real geometry;
            //   * recycled one -> tail is the (-99999,...) retire poison, which stretches the
            //     vertex grid's bounding box by ~1e8 snap units until every real vertex lands in
            //     one cell and the acceleration degrades into a full scan per edge.
            //
            // The second one is the live case, and it is not subtle: measured at ~1,600x on a 3.2k
            // triangle mesh (9,704 checks -> 16,287,492). Cost after the collapse is
            // 3 * triCount * V, so it grows with the SQUARE of stage size. This is DEBUG-only,
            // which means it lands squarely on Unity editor iteration — and IMP96's whole feature
            // is runtime rebaking, so an editor session pays it on every rebake.
            //
            // Which rebake sees which tail is fixed by the pool holding ONE retired slot: the
            // first two rebakes allocate fresh, and every rebake from the third on gets a
            // recycled (poisoned) array. So the run below has to reach at least three.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            // Identical buildings every round, so the counts are directly comparable — any spread
            // is the tail, not a difference in the geometry being scanned.
            var checks = new long[5];
            for (int i = 0; i < checks.Length; i++)
            {
                FPNavMeshBuildPipeline.TJunctionCandidateChecks = 0;
                ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, Set(2)));
                checks[i] = FPNavMeshBuildPipeline.TJunctionCandidateChecks;
            }

            long min = long.MaxValue, max = 0;
            foreach (long c in checks)
            {
                if (c < min) min = c;
                if (c > max) max = c;
            }
            Assert.Greater(min, 0, "the T-junction scan must actually be running — a zero count "
                + "means this gate is measuring nothing");
            Assert.LessOrEqual(max, min * 2,
                $"candidate checks must stay flat across rebakes; got [{string.Join(", ", checks)}]. "
                + "A jump at the third rebake is the recycled tail leaking into the vertex grid");
        }
#endif
    }
}
