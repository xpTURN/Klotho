using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The contract around reporting a refused placement by value instead of by exception.
    ///
    /// <para>The text of each refusal is pinned separately in
    /// <c>FPBuildingRejectionGoldenTests</c>. What is gated here is everything else: that the two
    /// forms agree, that the rejecting path stops formatting, that the FIRST failing check is
    /// still the one reported, and that a rejection leaves the context's recycling chain
    /// undisturbed.</para>
    /// </summary>
    [TestFixture]
    public class FPBuildingRejectionContractTests
    {
        #region Fixture

        /// <summary>20x20 slab, no agent radius — a rect is its own expansion.</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 2)
                for (int z = -10; z <= 10; z += 2)
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.0);
        }

        private static FPNavMeshRebakeContext Context() =>
            new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false));

        private static FPBuildingRect Rect(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static readonly FPBuildingRect[] Legal = { Rect(-4, -4, -2, -2) };
        private static readonly FPBuildingRect[] Overlapping = { Rect(-4, -4, 0, 0), Rect(-2, -2, 2, 2) };
        private static readonly FPBuildingRect[] Outside = { Rect(20, 20, 22, 22) };

        private static long MeasureAlloc(Action a, int warmup = 8, int iterations = 5)
        {
            for (int i = 0; i < warmup; i++) a();
            var samples = new List<long>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                a();
                samples.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        #endregion

        // ── V2: the two forms agree ──────────────────────────────────────────

        [Test]
        public void TryAndThrowing_AgreeOnAcceptance()
        {
            FPNavMeshRebakeContext a = Context(), b = Context();

            Assert.IsTrue(FPNavMeshRebaker.TryRebake(a, Legal, out FPNavMesh viaTry, out var rejection));
            Assert.AreEqual(FPBuildingRejection.None, rejection.Reason, "a success reports no reason");

            FPNavMesh viaThrowing = FPNavMeshRebaker.Rebake(b, Legal);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(viaThrowing),
                FPNavMeshRebaker.ComputeFingerprint(viaTry),
                "the same input through the two forms must produce the same mesh");
        }

        [Test]
        public void TryAndThrowing_AgreeOnRejection()
        {
            var cases = new (FPBuildingRect[] input, FPBuildingRejection reason)[]
            {
                (Overlapping, FPBuildingRejection.BuildingsOverlap),
                (new[] { Rect(-10, -2, -8, 2) }, FPBuildingRejection.TouchesWalkableBoundary),
                (Outside, FPBuildingRejection.OutsideWalkableRegion),
            };

            foreach (var (input, reason) in cases)
            {
                Assert.IsFalse(
                    FPNavMeshRebaker.TryRebake(Context(), input, out FPNavMesh mesh, out var rejection),
                    $"{reason}: Try must report failure");
                Assert.IsNull(mesh, $"{reason}: no mesh on rejection");
                Assert.AreEqual(reason, rejection.Reason);
                Assert.IsTrue(rejection.IsRejected);

                // The throwing form still throws, and still the same type. A game that catches
                // InvalidOperationException around Rebake keeps working unchanged.
                Assert.Throws<InvalidOperationException>(
                    () => FPNavMeshRebaker.Rebake(Context(), input), $"{reason}: throwing form");
            }
        }

        [Test]
        public void MalformedRequest_StillThrows_AndIsNotARejection()
        {
            // The line this whole design is drawn along. A request that should never have been
            // built is the game's bug; reporting it as "you cannot build there" would hand a
            // developer error to the player, identically on every peer, with nothing to notice it.
            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryRebake(Context(), Legal, out _, out _, null, default, 5),
                "a count past the array is a malformed request, not a refused placement");

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryRebake(null, Legal, out _, out _),
                "a null context is a malformed request");
        }

        [Test]
        public void RejectionInfo_NamesTheBuildings()
        {
            FPNavMeshRebaker.TryRebake(Context(), Overlapping, out _, out var overlap);
            Assert.AreEqual(0, overlap.IndexA);
            Assert.AreEqual(1, overlap.IndexB, "an overlap names both buildings");

            FPNavMeshRebaker.TryRebake(Context(), Outside, out _, out var outside);
            Assert.AreEqual(0, outside.IndexA, "the message never carried this index; the value does");
            Assert.AreEqual(-1, outside.IndexB);
        }

        // ── V2c: the rejecting path stops formatting ─────────────────────────

        [Test]
        public void RejectingPath_AllocatesEssentiallyNothing()
        {
            // The performance half of the change. Rejection is a normal, player-driven, every-peer
            // event, and it used to build an exception plus an interpolated message every time.
            //
            // Measured on an early rejection deliberately: the four checks that run before the
            // triangulation return without touching the hole map or the CDT. EmptyWalkableRegion
            // is not one of them — it can only be reported after both — so it is not comparable
            // and is not what a game meets anyway (no placement reaches it).
            FPNavMeshRebakeContext ctx = Context();
            long rejecting = MeasureAlloc(
                () => FPNavMeshRebaker.TryRebake(ctx, Overlapping, out _, out _));

            Assert.Less(rejecting, 1024,
                $"a refused placement allocated {rejecting} B — the Try path must not format a message");

            // Control: the throwing form on the same input still pays for exception + text, which
            // is the cost the Try form exists to avoid. If this is not markedly larger the
            // measurement above is not measuring what it claims.
            long throwing = MeasureAlloc(() =>
            {
                try { FPNavMeshRebaker.Rebake(ctx, Overlapping); }
                catch (InvalidOperationException) { }
            });
            Assert.Greater(throwing, rejecting,
                "the throwing form should cost more; otherwise this test proves nothing");
        }

        // ── V2d: the first failing check is the one reported ─────────────────

        [Test]
        public void FirstFailingCheckWins_NotTheLast()
        {
            // With exceptions this was free — the first throw out of the method was what the
            // caller saw. Reporting by value has to reproduce it, and getting it wrong is silent:
            // the accept/reject verdict, the mesh and the fingerprint are all identical, only the
            // reason shown to the player changes.
            //
            // This input violates two: it overlaps building 0 AND it crosses the boundary. The
            // pairwise pass runs before the per-building pass, so overlap must win.
            var both = new[] { Rect(-4, -4, 0, 0), Rect(-2, -2, 30, 30) };

            Assert.IsFalse(FPNavMeshRebaker.TryRebake(Context(), both, out _, out var rejection));
            Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, rejection.Reason,
                "the pairwise check runs first, so its verdict is the one reported");

            // Proof the second violation is real: the same building alone reports the boundary.
            Assert.IsFalse(FPNavMeshRebaker.TryRebake(
                Context(), new[] { Rect(-2, -2, 30, 30) }, out _, out var alone));
            Assert.AreEqual(FPBuildingRejection.TouchesWalkableBoundary, alone.Reason,
                "without the overlap partner the later check is what fires — so the test above "
                + "really is choosing between two live failures");
        }

        // ── V9: a rejection leaves the context's chain undisturbed ───────────

        [Test]
        public void Rejection_DoesNotDisturbTheRecyclingChain()
        {
            // A refused placement must be a non-event for the context. If the failure path ever
            // told it about a mesh that does not exist, the chain would stop patching the previous
            // mesh — slower and far heavier, with the same output and therefore no symptom.
            FPNavMeshRebakeContext clean = Context(), interrupted = Context();

            clean.CommitSwap(FPNavMeshRebaker.Rebake(clean, Legal));
            FPNavMesh cleanSecond = FPNavMeshRebaker.Rebake(clean, Legal);
            clean.CommitSwap(cleanSecond);

            interrupted.CommitSwap(FPNavMeshRebaker.Rebake(interrupted, Legal));
            Assert.IsFalse(FPNavMeshRebaker.TryRebake(interrupted, Overlapping, out _, out _),
                "the interruption has to actually be a rejection");
            FPNavMesh interruptedSecond = FPNavMeshRebaker.Rebake(interrupted, Legal);
            Assert.DoesNotThrow(() => interrupted.CommitSwap(interruptedSecond),
                "the rejection must not have left the chain expecting a different mesh");

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(cleanSecond),
                FPNavMeshRebaker.ComputeFingerprint(interruptedSecond),
                "a rejection in between must change nothing about the next accepted rebake");
        }

        // ── A-4: the context says when it has stopped patching ───────────────

        /// <summary>Captures warnings so the once-per-context contract can be counted.</summary>
        private sealed class CountingLogger : xpTURN.Klotho.Logging.IKLogger
        {
            internal int Warnings;
            public bool IsEnabled(xpTURN.Klotho.Logging.KLogLevel level) => true;
            public void Log(xpTURN.Klotho.Logging.KLogLevel level, string message, Exception ex)
            {
                if (level == xpTURN.Klotho.Logging.KLogLevel.Warning)
                    Warnings++;
            }
        }

        [Test]
        public void SkippingCommitSwap_IsReportedOncePerContext()
        {
            // Forgetting CommitSwap costs roughly double the time and three orders of magnitude
            // more allocation, and produces a byte-identical mesh — so no output comparison can
            // see it, and PatchOutcome cannot either: its counters are only touched once a
            // previous mesh exists, so they read all-zero exactly as on a first rebake. This
            // warning is the only signal there is.
            var log = new CountingLogger();
            FPNavMeshRebakeContext ctx = Context();

            FPNavMeshRebaker.Rebake(ctx, Legal, log);
            Assert.AreEqual(0, log.Warnings, "the first rebake has nothing uncommitted behind it");

            FPNavMeshRebaker.Rebake(ctx, Legal, log);
            Assert.AreEqual(1, log.Warnings, "the second one is rebaking over an uninstalled mesh");

            FPNavMeshRebaker.Rebake(ctx, Legal, log);
            Assert.AreEqual(1, log.Warnings, "and it says so once, not once per placement");
        }

        [Test]
        public void CommittingEveryTime_IsSilent()
        {
            // The control. Without it the test above would pass on a warning that fires always.
            var log = new CountingLogger();
            FPNavMeshRebakeContext ctx = Context();

            for (int i = 0; i < 4; i++)
                ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, Legal, log));

            Assert.AreEqual(0, log.Warnings, "a caller that commits has nothing to be told");
        }

        [Test]
        public void OneMissedCommit_RecoversOnTheNext()
        {
            // Why once-per-context is the right granularity rather than once-per-occurrence: a
            // single slip is self-healing. CommitSwap only accepts the most recent output, so
            // committing the NEXT one re-links the chain, and the whole cost was one un-patched
            // rebake. The warning is really aimed at the caller who never commits at all.
            FPNavMeshRebakeContext ctx = Context();

            FPNavMeshRebaker.Rebake(ctx, Legal);            // produced, never installed
            FPNavMesh second = FPNavMeshRebaker.Rebake(ctx, Legal);

            Assert.DoesNotThrow(() => ctx.CommitSwap(second),
                "the chain accepts the next output even though the previous one was dropped");

            int before = ctx.PatchOutcome.Incremental;
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, Legal));
            Assert.Greater(ctx.PatchOutcome.Incremental, before,
                "and patching resumes — the miss cost exactly one rebake, not the context");
        }
    }
}
