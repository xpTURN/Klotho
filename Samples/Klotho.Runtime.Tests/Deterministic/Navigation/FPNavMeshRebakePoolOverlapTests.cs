using System;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The overlap guard on <see cref="FPNavMeshRebakeBufferPool"/>.
    ///
    /// <para>Work buffers are one slot per role with no return call, so a consumer that starts
    /// while another is still holding them does not fail — it overwrites, and only partially
    /// (the CDT's <c>Array.Resize</c> replaces a grown array without writing it back to the
    /// slot). The result is a wrong mesh that <c>ComputeFingerprint</c> can pass, because the
    /// fingerprint does not cover adjacency, portals or the spatial grid. A time-sliced rebake
    /// holds those buffers across frames, which is exactly the overlap this guard forbids.</para>
    ///
    /// <para>DEBUG only, so these assert the guard where it exists and the absence of a false
    /// positive everywhere. The shipped precedent for "second consumer, own pool" is the
    /// placement preview (<c>FPBuildingPreviewScratch</c>).</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakePoolOverlapTests
    {
        private static FPNavMesh BuildBase(int half)
        {
            int side = half * 2 + 1;
            var vertices = new FPVector3[side * side];
            var xs = new long[side * side];
            var zs = new long[side * side];
            int n = 0;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                {
                    vertices[n] = new FPVector3(FP64.FromInt(x), FP64.Zero, FP64.FromInt(z));
                    xs[n] = FPGeoPredicates.Snap(vertices[n].x);
                    zs[n] = FPGeoPredicates.Snap(vertices[n].z);
                    n++;
                }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        [Test]
        public void SerialRebakes_OnOnePool_AreFine()
        {
            // The shipped pattern: one pool per room, rebakes one after another. The guard must
            // not fire here or it would break every existing caller.
            FPNavMesh baseMesh = BuildBase(8);
            var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            for (int i = 0; i < 4; i++)
                ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));
            Assert.Pass();
        }

        [Test]
        public void OverlappingUse_OfOnePool_IsRefused()
        {
            // Stands in for what a frame-spanning job does: hold the buffers, then let a second
            // consumer (a cache-miss drain, a join rebake) start on the same pool.
            var pool = new FPNavMeshRebakeBufferPool();
            pool.EnterUse();
            try
            {
#if DEBUG
                Assert.Throws<InvalidOperationException>(() => pool.EnterUse());
#else
                Assert.Ignore("overlap guard is DEBUG-only");
#endif
            }
            finally
            {
                pool.ExitUse();
            }

            // The guard leaves no residue: after the refused entry and the matching exit, the pool
            // is usable again. A leaked depth would turn one caught overlap into a dead pool.
            pool.EnterUse();
            pool.ExitUse();
        }

        [Test]
        public void SecondConsumer_WithItsOwnPool_IsFine()
        {
            // The prescribed fix, and the one the preview already uses.
            var a = new FPNavMeshRebakeBufferPool();
            var b = new FPNavMeshRebakeBufferPool();
            a.EnterUse();
            b.EnterUse();
            b.ExitUse();
            a.ExitUse();
            Assert.Pass();
        }

        [Test]
        public void NonPoolingInstance_IsExempt()
        {
            // Shared by design: every rent there allocates fresh storage, so overlap is safe.
            // Reached through the public API — a null pool resolves to it at the entry point.
            FPNavMesh baseMesh = BuildBase(6);
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);
            FPNavMesh first = FPNavMeshRebaker.Rebake(snapshot, null, null);
            FPNavMesh second = FPNavMeshRebaker.Rebake(snapshot, null, null);
            Assert.AreEqual(first.Triangles.Length, second.Triangles.Length);
        }
    }
}
