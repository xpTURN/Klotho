using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The command-path validator, against a fake placement table.
    ///
    /// <para><b>What these are for.</b> The validator exists so a game stops re-deriving the active
    /// set, and the value of that is not the deleted lines — it is that two DESYNC-class audits and
    /// the canonical order now have ONE implementation instead of two. So the tests that matter are
    /// the ones tying the validator to the driver (<see cref="ParityWithTheDriversDerivation"/>) and to
    /// the rebaker (<see cref="AcceptanceMatchesWhatTheRebakerWouldProduce"/>): drift between either
    /// pair is the failure the sharing is meant to make impossible.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshPlacementValidatorTests
    {
        private const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        #region Fixture

        private sealed class FakeSource : IFPNavMeshPlacementSource
        {
            public readonly List<FPNavMeshTimedPlacement> Entries = new List<FPNavMeshTimedPlacement>();
            public int Capacity { get; set; } = 8;
            public int? Eligible;

            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                int n = 0;
                for (int i = 0; i < Entries.Count && n < buffer.Length; i++)
                    buffer[n++] = Entries[i];
                eligible = Eligible ?? Entries.Count;
                return n;
            }

            public void DestroyDue(ref Frame frame, int tick) { }
        }

        private sealed class NullInstaller : IFPNavMeshInstaller
        {
            public void Install(ref Frame frame, FPNavMesh mesh) { }
            public void Reseed(ref Frame frame) { }
        }

        private static FPNavMesh Slab()
        {
            FP64 lo = FP64.FromInt(-10), hi = FP64.FromInt(10);
            var vertices = new[]
            {
                new FPVector3(lo, FP64.Zero, lo), new FPVector3(hi, FP64.Zero, lo),
                new FPVector3(hi, FP64.Zero, hi), new FPVector3(lo, FP64.Zero, hi),
            };
            var xs = new long[4];
            var zs = new long[4];
            for (int i = 0; i < 4; i++)
            {
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private static FPBuildingShapeCatalog SquareCatalog(out int shapeId)
        {
            var b = new FPBuildingShapeCatalogBuilder();
            shapeId = b.Add(new[] { -Unit, Unit, Unit, -Unit }, new[] { -Unit, -Unit, Unit, Unit });
            return b.Build();
        }

        private static FPNavMeshRebakeSnapshot Snapshot(out int shapeId)
            => FPNavMeshRebaker.CreateSnapshot(
                Slab(), null, prewarm: false, shapeCatalog: SquareCatalog(out shapeId));

        private static FPNavMeshTimedPlacement At(
            int sequence, int shapeId, int x, int z, int effective, int removal = int.MaxValue)
            => new FPNavMeshTimedPlacement
            {
                Sequence = sequence,
                Placement = new FPBuildingPlacement(
                    shapeId, FP64.FromInt(x), FP64.FromInt(z), FP64.Zero),
                EffectiveTick = effective,
                RemovalEffectiveTick = removal,
            };

        private static Frame FrameAt(int tick, IKLogger logger = null)
        {
            var frame = new Frame(64, logger);
            frame.Tick = tick;
            return frame;
        }

        #endregion

        // ─────────────────────────────────────────────── c1 · parity with the driver

        [Test]
        public void ParityWithTheDriversDerivation()
        {
            // The whole point of the shared ops in one assertion. If the two ever derive a different
            // set from the same table, the command path accepts placements the driver will not bake —
            // and a peer that JOINS then rebuilds from the driver's derivation and throws where the
            // placing peer succeeded, which surfaces as a load failure rather than as a rejection.
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);

            // A deliberately awkward table: out of sequence order, one pending, one tombstoned.
            source.Entries.Add(At(7, square, 4, 4, effective: 10));
            source.Entries.Add(At(2, square, -4, -4, effective: 10));
            source.Entries.Add(At(9, square, 0, 6, effective: 50));                   // pending at 20
            source.Entries.Add(At(4, square, 6, 0, effective: 10, removal: 15));      // gone by 20

            var driver = new FPNavMeshRebakeDriver(source, new NullInstaller());
            driver.SetSnapshot(snapshot);
            var validator = new FPNavMeshPlacementValidator(source);

            // Drive the driver to tick 20 so its installed key IS the active set there.
            Frame dframe = FrameAt(20);
            driver.Update(ref dframe);

            Frame vframe = FrameAt(0);
            int count = validator.Survey(ref vframe, atTick: 20);

            Assert.AreEqual(2, count,
                "the validator's active set at tick 20 is wrong: 7 and 2 are live, 9 is pending and "
                + "4 is tombstoned");

            // Same set AND same order, established by FINGERPRINT rather than by re-deriving the
            // answer in the test. The first draft of this assertion compared the validator against a
            // list the TEST computed, which is a tautology — it stayed green when the validator's
            // order was artificially reversed. A mesh fingerprint is the real observable: the rebaker
            // appends each hole's vertices in list order, so a different order is a different
            // triangulation.
            //
            // The shared derivation is called directly here (it is internal, and this assembly sees
            // internals) because that is the claim under test: the set the validator uses verbatim
            // must produce the mesh the driver installs.
            var recording = new RecordingInstaller();
            var driver2 = new FPNavMeshRebakeDriver(source, recording);
            driver2.SetSnapshot(snapshot);
            Frame at20 = FrameAt(20);
            driver2.Update(ref at20);
            Assert.IsNotNull(recording.Last, "fixture: the driver installed nothing at tick 20");

            var table = new FPNavMeshTimedPlacement[source.Capacity];
            var derived = new FPBuildingPlacement[source.Capacity + 1];
            var keys = new int[source.Capacity + 1];
            Frame opsFrame = FrameAt(0);
            int tableCount = FPNavMeshPlacementTableOps.Collect(source, ref opsFrame, table);
            int derivedCount = FPNavMeshPlacementTableOps.BuildActiveSet(
                ref opsFrame, table, tableCount, atTick: 20, derived, keys,
                skipSequence: -1, out _);

            Assert.AreEqual(count, derivedCount,
                "the validator and the shared derivation disagree about the active count");

            bool rebaked = FPNavMeshRebaker.TryRebakePlacements(
                new FPNavMeshRebakeContext(snapshot), derived, out FPNavMesh fromDerivation,
                out _, null, default, derivedCount);
            Assert.IsTrue(rebaked && fromDerivation != null, "the derived set did not bake");

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(recording.Last),
                FPNavMeshRebaker.ComputeFingerprint(fromDerivation),
                "the driver installed a DIFFERENT mesh than the shared derivation produces from the "
                + "same table. They share one derivation precisely so this cannot happen — if it "
                + "does, the command path validates against a mesh the driver will never install");
        }

        private sealed class RecordingInstaller : IFPNavMeshInstaller
        {
            public FPNavMesh Last;
            public void Install(ref Frame frame, FPNavMesh mesh) { Last = mesh; }
            public void Reseed(ref Frame frame) { }
        }

        [Test]
        public void TheDerivationIsAFunctionOfTheFrame_NotOfEnumerationOrder()
        {
            // What the canonical sort is FOR, and the property a parity test structurally cannot see:
            // the driver and the validator share one derivation, so a mutation to that derivation
            // moves both and every fingerprint still agrees. What breaks is agreement between PEERS —
            // two games enumerating the same components in different orders. That is not exotic:
            // entity iteration order is storage order, and storage order differs after a rollback, a
            // full-state restore, or simply a different creation history.
            //
            // Removing the sort leaves both peers self-consistent and each carving a different mesh,
            // with the state hash matching on both. This is the assertion that catches it.
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);

            var forward = new FakeSource();
            forward.Entries.Add(At(2, square, -4, -4, effective: 0));
            forward.Entries.Add(At(5, square, 4, 4, effective: 0));
            forward.Entries.Add(At(9, square, 4, -4, effective: 0));

            var reversed = new FakeSource();
            for (int i = forward.Entries.Count - 1; i >= 0; i--)
                reversed.Entries.Add(forward.Entries[i]);

            ulong a = FingerprintOf(forward, snapshot);
            ulong b = FingerprintOf(reversed, snapshot);

            Assert.AreEqual(a, b,
                "two peers holding the SAME placements in different enumeration orders carved "
                + "different meshes. The canonical sort is what makes the rebake input a function of "
                + "the frame; without it the meshes diverge while the state hash still matches, and "
                + "nothing else in the engine would ask why");
        }

        private static ulong FingerprintOf(FakeSource source, FPNavMeshRebakeSnapshot snapshot)
        {
            var recording = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, recording);
            driver.SetSnapshot(snapshot);
            Frame frame = FrameAt(0);
            driver.Update(ref frame);
            Assert.IsNotNull(recording.Last, "fixture: nothing was installed");
            return FPNavMeshRebaker.ComputeFingerprint(recording.Last);
        }

        // ─────────────────────────────────────────────── c2 · acceptance equals the rebake

        [Test]
        public void AcceptanceMatchesWhatTheRebakerWouldProduce()
        {
            // "Validated" and "installed" have to be the same predicate, or a placement is accepted
            // and then refused on the tick it lands — a rejection with no command left to refuse.
            //
            // ⚠ Separate contexts on purpose. TryPreview DISCARDS its output from the context and
            // TryRebakePlacements LEAVES it as the most recent one, so running both through one
            // context would compare two calls made against different context state.
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);
            source.Entries.Add(At(1, square, 4, 4, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0);
            validator.Survey(ref frame, atTick: 0);

            var candidate = new FPBuildingPlacement(
                square, FP64.FromInt(-4), FP64.FromInt(-4), FP64.Zero);

            var previewCtx = new FPNavMeshRebakeContext(snapshot);
            bool previewed = validator.TryPreviewWith(previewCtx, candidate, out _);

            var rebakeCtx = new FPNavMeshRebakeContext(snapshot);
            var set = new[]
            {
                new FPBuildingPlacement(square, FP64.FromInt(4), FP64.FromInt(4), FP64.Zero),
                candidate,
            };
            bool rebaked = FPNavMeshRebaker.TryRebakePlacements(
                rebakeCtx, set, out FPNavMesh mesh, out _, null, default, set.Length);

            Assert.AreEqual(rebaked, previewed,
                "the validator and the rebaker disagreed about the same set. One of them is what the "
                + "player is told and the other is what the mesh becomes");
            Assert.IsTrue(previewed, "fixture: two far-apart squares must be acceptable");
            Assert.IsNotNull(mesh, "fixture: the rebake produced no mesh");
        }

        [Test]
        public void ARefusedSetIsAValueOnBothSides()
        {
            // The same equivalence on the refusing side — overlapping squares.
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);
            source.Entries.Add(At(1, square, 0, 0, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0);
            validator.Survey(ref frame, atTick: 0);

            var overlapping = new FPBuildingPlacement(square, FP64.Zero, FP64.Zero, FP64.Zero);
            bool previewed = validator.TryPreviewWith(
                new FPNavMeshRebakeContext(snapshot), overlapping, out FPBuildingRejectionInfo rejection);

            Assert.IsFalse(previewed, "two squares in the same place were accepted");
            Assert.IsTrue(rejection.IsRejected, "the refusal carried no reason");
            Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, rejection.Reason);
        }

        // ─────────────────────────────────────────────── c3 · the audits, and T-12

        [Test]
        public void ATruncatedTableIsReported_AndCountsTheWHOLETable_NotJustTheActiveSet()
        {
            // T-12. The game's own collect counted `eligible` AFTER the tick-window filter, so its
            // copy of this audit could never fire on tombstones or pending entries — the very things
            // that make the storage bound larger than the policy one. The shared derivation counts the
            // whole table, which is what the seam contract says and what makes the audit reachable by
            // the input that actually overflows it.
            var log = new LogCapture();
            var source = new FakeSource { Capacity = 2 };
            Snapshot(out int square);

            // Two live, plus two the window excludes — the frame holds four, the buffer fits two.
            source.Entries.Add(At(1, square, 4, 4, effective: 0));
            source.Entries.Add(At(2, square, -4, -4, effective: 0));
            source.Entries.Add(At(3, square, 0, 6, effective: 900));                 // pending
            source.Entries.Add(At(4, square, 6, 0, effective: 0, removal: 1));       // tombstoned
            source.Eligible = 4;

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(10, log);
            validator.Survey(ref frame, atTick: 10);

            Assert.IsTrue(log.Contains(KLogLevel.Error, "placement table truncated"),
                "a truncated table was accepted silently. The dropped entries are missing from the "
                + "validation input, so this peer accepts placements the mesh refuses — and the state "
                + "hash still matches on every peer");
        }

        [Test]
        public void ADuplicateSequenceIsReported()
        {
            var log = new LogCapture();
            var source = new FakeSource();
            Snapshot(out int square);
            source.Entries.Add(At(5, square, 4, 4, effective: 0));
            source.Entries.Add(At(5, square, -4, -4, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0, log);
            validator.Survey(ref frame, atTick: 0);

            Assert.IsTrue(log.Contains(KLogLevel.Error, "duplicate Sequence"),
                "a duplicate Sequence went unreported. The sort is unstable, so the validation input "
                + "then depends on enumeration order and stops being a function of the frame");
        }

        // ─────────────────────────────────────────────── c4 · sequence issuance

        [Test]
        public void NextSequenceCountsTheWholeTable_IncludingPendingAndTombstoned()
        {
            var source = new FakeSource();
            Snapshot(out int square);
            source.Entries.Add(At(3, square, 4, 4, effective: 0));
            source.Entries.Add(At(11, square, 0, 6, effective: 900));                // pending
            source.Entries.Add(At(7, square, 6, 0, effective: 0, removal: 1));       // tombstoned

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(10);
            validator.Survey(ref frame, atTick: 10);

            Assert.AreEqual(12, validator.NextSequence,
                "NextSequence narrowed to the active set. It would then hand out a number the pending "
                + "entry still holds, and a duplicate is what makes the canonical order depend on "
                + "enumeration order");
        }

        [Test]
        public void NextSequenceOnAnEmptyTableIsZero()
        {
            var validator = new FPNavMeshPlacementValidator(new FakeSource());
            Frame frame = FrameAt(0);
            Assert.AreEqual(0, validator.Survey(ref frame, atTick: 0));
            Assert.AreEqual(0, validator.NextSequence);
        }

        // ─────────────────────────────────────────────── c5 · the removal skip

        [Test]
        public void ARemovalExcludesItsTarget_WhichTheTickWindowCannotDo()
        {
            // At the moment a remove command is handled the target is still inside its own window, so
            // only an explicit exclusion drops it. Without it the target overlaps ITSELF and the
            // removal is refused — a command that can never succeed.
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);
            source.Entries.Add(At(1, square, 0, 0, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0);

            int withTarget = validator.Survey(ref frame, atTick: 0);
            Assert.AreEqual(1, withTarget, "fixture: the target must be live");

            int withoutTarget = validator.Survey(ref frame, atTick: 0, skipSequence: 1);
            Assert.AreEqual(0, withoutTarget, "the skip did not exclude the target");

            Assert.IsTrue(validator.TryPreview(new FPNavMeshRebakeContext(snapshot), out _),
                "the shrunken set does not bake — a removal can never fail on geometry");
        }

        // ─────────────────────────────────────────────── c6 · T-14 promotion

        [Test]
        public void AnAmbiguousRemovalTargetIsRefused_NotResolvedArbitrarily()
        {
            // T-14. Excluding by Sequence is weaker than the entity identity it replaces: identity
            // compares a version and is immune to a duplicate, while a duplicated Sequence makes "the
            // one to exclude" undefined. Audit 9 only REPORTS that state, so the validator refuses the
            // removal rather than dropping whichever entry the comparison reached first — the
            // alternative deletes a building that is still standing and leaves its hole in the mesh.
            var log = new LogCapture();
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);
            source.Entries.Add(At(5, square, 4, 4, effective: 0));
            source.Entries.Add(At(5, square, -4, -4, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0, log);

            Assert.AreEqual(-1, validator.Survey(ref frame, atTick: 0, skipSequence: 5),
                "an ambiguous removal target was resolved instead of refused");
            Assert.IsTrue(log.Contains(KLogLevel.Error, "duplicate Sequence"),
                "the refusal was silent — the game's numbering bug is the thing to report");

            // And a caller that ignores the -1 is stopped rather than allowed to validate against a
            // set the survey refused to define.
            Assert.Throws<System.ArgumentException>(
                () => validator.TryPreview(new FPNavMeshRebakeContext(snapshot), out _),
                "previewing after a refused survey was allowed");
        }

        [Test]
        public void ADuplicateWithoutASkipStillValidates()
        {
            // The refusal is scoped to the REMOVAL path. A placement's validation does not depend on
            // telling the duplicates apart, and refusing it would turn a numbering bug into an outage.
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out int square);
            source.Entries.Add(At(5, square, 4, 4, effective: 0));
            source.Entries.Add(At(5, square, -4, -4, effective: 0));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0);

            Assert.AreEqual(2, validator.Survey(ref frame, atTick: 0),
                "a duplicate refused the placement path too");
            Assert.IsTrue(validator.TryPreviewWith(
                new FPNavMeshRebakeContext(snapshot),
                new FPBuildingPlacement(square, FP64.FromInt(-8), FP64.FromInt(8), FP64.Zero), out _));
        }

        // ─────────────────────────────────────────────── c7 · the reentrancy contract

        [Test]
        public void PreviewingWithoutASurveyThrows()
        {
            var source = new FakeSource();
            FPNavMeshRebakeSnapshot snapshot = Snapshot(out _);
            var validator = new FPNavMeshPlacementValidator(source);

            Assert.Throws<System.ArgumentException>(
                () => validator.TryPreview(new FPNavMeshRebakeContext(snapshot), out _),
                "a preview ran with no survey behind it — it would validate against an empty buffer "
                + "and accept anything");
        }

        [Test]
        public void ASecondSurveyReplacesTheFirst()
        {
            // The documented consequence of holding the result in this object: one command is one
            // survey. Pinned so the contract is a behaviour rather than a comment.
            var source = new FakeSource();
            Snapshot(out int square);
            source.Entries.Add(At(1, square, 4, 4, effective: 0));
            source.Entries.Add(At(2, square, -4, -4, effective: 100));

            var validator = new FPNavMeshPlacementValidator(source);
            Frame frame = FrameAt(0);

            Assert.AreEqual(1, validator.Survey(ref frame, atTick: 0));
            Assert.AreEqual(2, validator.Survey(ref frame, atTick: 100),
                "the second survey did not replace the first");
        }

        [Test]
        public void ACapacityOfZeroIsRefusedAtConstruction()
        {
            Assert.Throws<System.ArgumentException>(
                () => new FPNavMeshPlacementValidator(new FakeSource { Capacity = 0 }));
        }
    }
}
