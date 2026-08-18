using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The driver's mechanism, against a fake placement source.
    ///
    /// <para>These are the nets that used to be regex pins over a sample's source, and two of them
    /// were the ONLY thing guarding their invariant. A pin fails on the edit; a test fails on the
    /// behaviour, which is what lets a mutation prove the net bites.</para>
    ///
    /// <para>A fake source is what makes them possible at all. The same properties driven through a
    /// game's command path can only be reached by arranging placements and rollbacks, and the
    /// property that mattered most — a correction must not kill the in-flight task — is invisible
    /// there, because the tick's own update starts a fresh task right after the correction.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeDriverTests
    {
        private const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        /// <summary>A table the test writes directly. Capacity is the storage bound, as the seam
        /// requires; <see cref="Eligible"/> is what lets a test fake truncation.</summary>
        private sealed class FakeSource : IFPNavMeshPlacementSource
        {
            public readonly List<FPNavMeshTimedPlacement> Entries = new List<FPNavMeshTimedPlacement>();
            public int Capacity { get; set; } = 8;
            public int? Eligible;
            public readonly List<int> DestroyedAt = new List<int>();

            /// <summary>Shared with the installer so one Update's callback ORDER is observable —
            /// that order is the invariant, not just the fact that each one ran.</summary>
            public List<string> Calls;

            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                int n = 0;
                for (int i = 0; i < Entries.Count && n < buffer.Length; i++)
                    buffer[n++] = Entries[i];
                eligible = Eligible ?? Entries.Count;
                return n;
            }

            public void DestroyDue(ref Frame frame, int tick)
            {
                DestroyedAt.Add(tick);
                Calls?.Add("destroy");
            }
        }

        /// <summary>
        /// Records what the driver asked for, and — crucially — touches the PREVIOUS mesh while
        /// installing the next one.
        ///
        /// <para>That is what makes a commit-before-install order observable, and reading the NEW
        /// mesh is not: the commit retires the mesh being REPLACED, so the newly installed one is
        /// never the retired one. A real installer is in exactly this position — the nav system is
        /// still pointing at the outgoing mesh at the moment install is called — so an installer that
        /// touches it is not a contrivance, it is the case the ordering exists to protect.</para>
        ///
        /// <para><c>Triangles</c> rather than <c>TriangleCount</c>: only the span accessors assert
        /// liveness (<c>FPNavMesh.AssertLive</c>), because the count is just a field.</para>
        /// </summary>
        private sealed class RecordingInstaller : IFPNavMeshInstaller
        {
            public readonly List<int> InstalledTriangleCounts = new List<int>();
            public int Reseeds;
            public FPNavMesh Last;
            public int OutgoingReads;
            public List<string> Calls;

            public void Install(ref Frame frame, FPNavMesh mesh)
            {
                if (Last != null && !ReferenceEquals(Last, mesh))
                {
                    // Throws in DEBUG if the driver committed first: the commit retired this one.
                    _ = Last.Triangles.Length;
                    OutgoingReads++;
                }
                InstalledTriangleCounts.Add(mesh.TriangleCount);
                Last = mesh;
                Calls?.Add("install");
            }

            public void Reseed(ref Frame frame)
            {
                Reseeds++;
                Calls?.Add("reseed");
            }
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

        private static FPNavMeshRebakeSnapshot SnapshotWithSquare(out int shapeId)
        {
            var catalog = new FPBuildingShapeCatalogBuilder();
            shapeId = catalog.Add(
                new[] { -Unit, Unit, Unit, -Unit }, new[] { -Unit, -Unit, Unit, Unit });
            return FPNavMeshRebaker.CreateSnapshot(
                Slab(), null, prewarm: false, shapeCatalog: catalog.Build());
        }

        private static FPNavMeshTimedPlacement At(
            int sequence, int shapeId, int x, int z, int effective, int removal = int.MaxValue)
        {
            return new FPNavMeshTimedPlacement
            {
                Sequence = sequence,
                Placement = new FPBuildingPlacement(
                    shapeId, FP64.FromInt(x), FP64.FromInt(z), FP64.Zero),
                EffectiveTick = effective,
                RemovalEffectiveTick = removal,
            };
        }

        private static Frame FrameAt(int tick, Klotho.Logging.IKLogger logger = null)
        {
            var frame = new Frame(64, logger);
            frame.Tick = tick;
            return frame;
        }

        // ─────────────────────────────────────────────── pin ③ replacement

        [Test]
        public void ACorrectionThatRebuilds_LeavesTheInFlightTaskAlive_ByIdentity()
        {
            // The regression NO behavioural test could see before: put the discard back into the
            // rebuild path and every existing suite stays green, because the OUTPUT is identical —
            // the same mesh arrives on the same tick, just paid for in one lump on the boundary frame
            // instead of spread across the frames leading to it.
            //
            // ⚠ The assertion is IDENTITY, not presence. HasPendingRebake reads true either way: the
            // tick's own Update starts a fresh task right after the correction. That is exactly why
            // this test lives here and not in a game's suite — CorrectNow can be called on its own.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));

            // THREE distinct sets, because the correction has to want one the task is not building.
            // With a single placement the task's target and the correction's target are the same set,
            // so TakeTask serves it and the rebuild path is never entered — correct behaviour, and a
            // fixture that proves nothing.
            source.Entries.Add(At(1, square, 4, 4, effective: 10));
            source.Entries.Add(At(2, square, -4, -4, effective: 20));

            Frame f0 = FrameAt(0);
            driver.Update(ref f0);              // installs {}, starts a task for {A} (boundary 10)
            Assert.IsTrue(driver.HasPendingRebake, "no task in flight — the fixture proves nothing");
            int taskId = driver.TaskId;
            Assert.Greater(taskId, 0, "the driver does not report a task identity");

            // {A,B} is neither what is installed nor what the task is building, so this misses the
            // slots and rebuilds — while the task for {A} is still holding its own pool.
            int rebuildsBefore = driver.RebuildInstalls;
            Frame f20 = FrameAt(20);
            driver.CorrectNow(ref f20);

            Assert.Greater(driver.RebuildInstalls, rebuildsBefore,
                "the correction did not rebuild, so it never reached the path under test");
            Assert.IsTrue(driver.HasPendingRebake, "the task is gone");
            Assert.AreEqual(taskId, driver.TaskId,
                "the rebuild replaced the in-flight task. It must not: a predicting peer corrects "
                + "several times per boundary, and killing the task each time is what held its "
                + "slices to one per task. Presence is not enough to see this — identity is");
        }

        // ─────────────────────────────────────────────── pin ② replacement

        [Test]
        public void TheMeshIsInstalledBeforeItIsCommitted_SoTheLiveOneIsNeverRecycled()
        {
            // CommitSwap does not install the mesh, it RETIRES the one being replaced, and a retired
            // mesh's arrays go back to the pool. Committing first hands the installer storage the pool
            // has already taken back.
            //
            // The installer touches the OUTGOING mesh while installing the next one, which is what
            // makes the wrong order observable — a retired mesh throws on a span read in DEBUG.
            //
            // ⚠ slotCount: 1 is load-bearing, and a mutation is what said so. With two slots
            // consecutive installs alternate contexts, so the mesh a commit retires is that slot's
            // OLDER one rather than the mesh being replaced — the outgoing mesh survives and the read
            // succeeds either way. One slot puts the retirement exactly where the ordering matters.
            // (The two-slot case is still covered: OneSlot... and the task-identity test both fail on
            // the same mutation, but by accident rather than by design.)
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer, slotCount: 1);
            driver.SetSnapshot(SnapshotWithSquare(out int square));

            Frame f0 = FrameAt(0);
            driver.Update(ref f0);                        // installs the empty set

            source.Entries.Add(At(1, square, 4, 4, effective: 1));
            for (int tick = 1; tick <= 4; tick++)
            {
                Frame f = FrameAt(tick);
                driver.Update(ref f);
            }

            Assert.Greater(installer.InstalledTriangleCounts.Count, 1,
                "nothing was installed after the first tick, so no commit was exercised");
            Assert.Greater(installer.OutgoingReads, 0,
                "the installer never saw an outgoing mesh, so the ordering was never put to the test "
                + "— the assertion below can only fail if it did");
            foreach (int tris in installer.InstalledTriangleCounts)
                Assert.Greater(tris, 0, "an installed mesh had no triangles");
        }

        // ─────────────────────────────────────────────── the reseed split

        [Test]
        public void ReseedRunsOnEveryExecutionOfABoundaryTick_EvenWhenTheMeshIsAlreadyRight()
        {
            // The four-match divergence in one assertion, and the reason the two halves of the seam
            // are separate methods. Installing is derived and skippable; reseeding writes hashed frame state and is
            // not. Re-executing a boundary after its mesh is already installed must still reseed.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));

            source.Entries.Add(At(1, square, 4, 4, effective: 5));

            Frame first = FrameAt(5);
            driver.Update(ref first);
            int installsAfterFirst = installer.InstalledTriangleCounts.Count;
            Assert.AreEqual(1, installer.Reseeds, "the boundary did not reseed at all");

            // The same tick again — a rollback that lands on it.
            Frame again = FrameAt(5);
            driver.Update(ref again);

            Assert.AreEqual(2, installer.Reseeds,
                "the re-executed boundary skipped the reseed. That is the divergence four live "
                + "matches were spent on: the authority reseeded once and kept the value, the client "
                + "re-executed and kept the triangle index it had from before the swap");
            Assert.AreEqual(installsAfterFirst, installer.InstalledTriangleCounts.Count,
                "the re-execution installed again — the mesh was already right, and skipping that is "
                + "the whole point of the split");
        }

        [Test]
        public void OnlyAnExecutedBoundaryReseeds_NeitherSlicesNorCorrections()
        {
            // The invariant a DIAGNOSTIC rests on, which is why it is pinned here rather than left
            // to the reader. Brawler reads "was this tick a boundary?" off a change in Reseeds from
            // a system registered after the driver — so the counter must move on executed boundary
            // ticks and NOWHERE else. Slices are frame-paced peer-local work and corrections are
            // derived-state repairs; either one touching Reseeds would make that reading wrong, and
            // the misreading is silent (every peer would agree, and the mesh is identical anyway).
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 50));

            Frame start = FrameAt(0);
            driver.Update(ref start);
            Assert.IsTrue(driver.HasPendingRebake, "no task to advance — the fixture proves nothing");
            Assert.AreEqual(0, installer.Reseeds, "tick 0 is not a boundary and it reseeded");

            int slicedBefore = driver.SlicedFrames;
            for (int i = 0; i < 5; i++)
                driver.AdvanceSlice(0.016f);
            Assert.Greater(driver.SlicedFrames, slicedBefore,
                "no slice ran — the assertion below would hold vacuously");
            Assert.AreEqual(0, installer.Reseeds, "advancing a slice reseeded");

            // A correction, and on a BOUNDARY tick — the strongest form: even where a reseed would
            // be legitimate for Update, CorrectNow must not be the one to do it.
            Frame boundary = FrameAt(50);
            int correctionsBefore = driver.Corrections;
            driver.CorrectNow(ref boundary);
            Assert.Greater(driver.Corrections, correctionsBefore,
                "the correction did nothing — the assertion below would hold vacuously");
            Assert.AreEqual(0, installer.Reseeds,
                "CorrectNow reseeded. Only an EXECUTED boundary tick may, because a reseed writes "
                + "hashed frame state and CorrectNow runs on peer-local occasions (world init, a "
                + "full state apply) that the other peers do not share");

            // The OTHER way a correction installs: served from a slot instead of rebuilt. Tick 0's
            // set is still in a slot from the first Update, so correcting back to it hits the cache
            // — a different branch, and one a rebuild-path-only fixture leaves uncovered (found by
            // mutating that branch and watching this test stay green).
            Frame backToEmpty = FrameAt(0);
            int hitsBefore = driver.CacheHits;
            driver.CorrectNow(ref backToEmpty);
            Assert.Greater(driver.CacheHits, hitsBefore,
                "the correction did not come from the cache — the assertion below covers the "
                + "rebuild branch twice instead of covering both");
            Assert.AreEqual(0, installer.Reseeds, "a cache-served correction reseeded");

            // Back to the boundary set so the executed-boundary check below is a real boundary.
            Frame reinstate = FrameAt(50);
            driver.CorrectNow(ref reinstate);
            Assert.AreEqual(0, installer.Reseeds, "reinstating the boundary set reseeded");

            // And the counter does move when it should, so the pin cannot pass by never reseeding.
            Frame executed = FrameAt(50);
            driver.Update(ref executed);
            Assert.AreEqual(1, installer.Reseeds, "the executed boundary did not reseed");
        }

        // ─────────────────────────────────────────────── audits 8 and 9

        [Test]
        public void ATruncatedTable_IsReportedAsAnError_RatherThanSilentlyBaked()
        {
            // The quietest failure in the pipeline: the dropped entries are missing from the mesh
            // while the state hash still matches on every peer, so nothing else asks why.
            var log = new CountingLogger();
            var source = new FakeSource { Capacity = 2, Eligible = 5 };
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 0));
            source.Entries.Add(At(2, square, -4, -4, effective: 0));

            Frame f = FrameAt(0, log);
            driver.Update(ref f);

            Assert.Greater(log.Errors, 0,
                "a table that reported 5 eligible entries and delivered 2 was baked without a word");
        }

        [Test]
        public void ADuplicateSequence_IsReportedAsAnError_BecauseTheSortIsNotStable()
        {
            // Two entries sharing a key make the sort's output depend on the collect order, so the
            // rebake input stops being a function of the frame. Every peer sorts its own order, the
            // navmeshes diverge, and the state hash still matches.
            var log = new CountingLogger();
            var source = new FakeSource();
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(7, square, 4, 4, effective: 0));
            source.Entries.Add(At(7, square, -4, -4, effective: 0));

            Frame f = FrameAt(0, log);
            driver.Update(ref f);

            Assert.Greater(log.Errors, 0, "a duplicate Sequence was sorted and baked without a word");
        }

        // ─────────────────────────────────────────────── the boundary cache

        [Test]
        public void BouncingAcrossABoundary_ServesFromTheSlots_AndBuildsOnlyTheDistinctSets()
        {
            // Two slots are exactly enough for a two-cycle, which is what a predicting peer does at a
            // boundary: cross, roll back, cross again. Distinct sets here are {} and {b}, so two
            // builds cover any number of crossings.
            var source = new FakeSource();
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 10));

            Frame warm = FrameAt(0);
            driver.Update(ref warm);                      // {} installed

            for (int i = 0; i < 6; i++)
            {
                Frame after = FrameAt(10);
                driver.Update(ref after);                 // {b}
                Frame before = FrameAt(9);
                driver.Update(ref before);                // {} again
            }

            int builds = driver.RebuildInstalls + driver.TaskInstalls;
            Assert.LessOrEqual(builds, 4,
                $"twelve crossings cost {builds} builds. Two slots cover a two-cycle; if this grows "
                + "with the number of crossings the cache is not being consulted");
            Assert.Greater(driver.CacheHits, 0, "nothing was served from the slots at all");
        }

        [Test]
        public void OneSlot_ACorrectionThatRebuilds_DiscardsTheTaskInsteadOfEnteringItsPool()
        {
            // The one-slot case the two-slot design silently stopped covering. SlotForRebuild only
            // steers around the task's slot when there is another one, so at slotCount: 1 the
            // rebuild lands on the very slot whose pool the in-flight task is holding — and with one
            // slot the cache cannot absorb it either: the single slot holds the INSTALLED set, and
            // reaching the rebuild means the wanted set is not that one, so the lookup always misses.
            //
            // A rollback is what puts the two together: it lands on a tick whose set differs from
            // what is installed (so a correction runs) while the task started for the next boundary
            // is still alive (Update corrects BEFORE it re-targets the task).
            //
            // What fails without the fix is the pool's own overlap guard — which is DEBUG-only, so
            // this test can only observe the violation in a Debug build. That is the point of the
            // guard rather than a gap in the gate: in Release the same rebuild quietly overwrites the
            // task's work buffers, and the mesh it corrupts is the one a later boundary installs.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer, slotCount: 1);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 5));
            source.Entries.Add(At(2, square, -4, -4, effective: 10));

            Frame warm = FrameAt(0);
            driver.Update(ref warm);
            int emptyTris = installer.Last.TriangleCount;

            for (int tick = 1; tick <= 5; tick++)
            {
                Frame f = FrameAt(tick);
                driver.Update(ref f);
            }
            Assert.AreNotEqual(emptyTris, installer.Last.TriangleCount,
                "the first placement never reached the mesh, so the rest of this test is vacuous");
            Assert.IsTrue(driver.HasPendingRebake,
                "no task is in flight, so the collision this pins cannot be reached");

            Frame back = FrameAt(4);
            driver.Update(ref back);       // rollback: wants the empty set, task is building {A,B}

            Assert.AreEqual(emptyTris, installer.Last.TriangleCount,
                "coming back across the boundary did not restore the empty set");
        }

        [Test]
        public void OneSlot_DisablesTheCache_WithoutBreakingCorrectness()
        {
            // slotCount is a parameter because the second slot's resident cost scales with the stage.
            // At 1 the cache cannot hit; the driver must still install the right mesh every time.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer, slotCount: 1);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 10));

            Frame warm = FrameAt(0);
            driver.Update(ref warm);
            int emptyTris = installer.Last.TriangleCount;

            Frame after = FrameAt(10);
            driver.Update(ref after);
            Assert.AreNotEqual(emptyTris, installer.Last.TriangleCount,
                "the placement never reached the mesh");

            Frame before = FrameAt(9);
            driver.Update(ref before);
            Assert.AreEqual(emptyTris, installer.Last.TriangleCount,
                "coming back across the boundary did not restore the empty set");
            Assert.AreEqual(0, driver.CacheHits, "a single slot must not report cache hits");
        }

        // ─────────────────────────────────────────────── the heartbeat guard

        [Test]
        public void TheSliceHeartbeatIsGrantedToTheFirstClaimantOnly()
        {
            // The guard lives in the driver because a host reaches the wiring through several
            // doors that do not know about each other — a session wires from world init AND from
            // game start; a client's door comes round again on every reconnect — so no single
            // call site can tell whether it is the first.
            //
            // Subscribing twice is not a louder version of subscribing once: it spends the frame
            // budget N times per frame, which turns the feature into the spike it exists to remove.
            // And it is silent — the mesh is identical either way, so only the counters can see it.
            var driver = new FPNavMeshRebakeDriver(new FakeSource(), new RecordingInstaller());

            Assert.IsTrue(driver.TryClaimSliceHeartbeat(), "the first claimant was refused");
            Assert.IsTrue(driver.SliceHeartbeatWired, "the grant was not recorded");

            for (int i = 0; i < 3; i++)
                Assert.IsFalse(driver.TryClaimSliceHeartbeat(),
                    $"claim {i + 2} was granted. Every extra subscription spends the whole frame "
                    + "budget again, and nothing about the mesh shows it");

            Assert.AreEqual(4, driver.HeartbeatClaimAttempts,
                "the attempt counter is what makes the guard observable — comparing slices against "
                + "frame count cannot, because a slice only counts when a task was pending, so there "
                + "is no independent expected value to divide by. Attempts >= 2 with one grant IS "
                + "the evidence");
        }

        // ─────────────────────────────────────────────── the cache key

        [Test]
        public void TheCacheKeyIsTheSetItself_NotTheDigest_SoACollisionCannotPickAMesh()
        {
            // Moved here from the sample's EditMode suite: the only one of those tests that did not
            // need a command path, because it was always about this comparison.
            //
            // DigestAt SUMS per-entry terms — order independence bought with collision resistance —
            // and that is fine while it only answers "did the set change", because a collision there
            // costs one skipped comparison that the next tick repeats. It is NOT fine as a cache key:
            // a collision would silently install a mesh built for a different set, with nothing to
            // notice it by and no self-heal.
            //
            // Reflection because what is pinned is that the comparison EXISTS and looks at geometry.
            // Delete the geometry comparison and the second case below passes two different sets as
            // equal, which is the failure this guards.
            var compare = typeof(FPNavMeshRebakeDriver).GetMethod(
                "SameSet",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(compare,
                "FPNavMeshRebakeDriver.SameSet is gone. If the cache now matches on the digest, a "
                + "collision installs the wrong mesh — see DigestAt, which is a sum");

            FP64 zero = FP64.Zero, six = FP64.FromInt(6), minusSix = FP64.FromInt(-6);
            var a = new[]
            {
                new FPBuildingPlacement(0, 0, minusSix, zero, zero),
                new FPBuildingPlacement(0, 0, six, zero, zero),
            };
            var same = new[]
            {
                new FPBuildingPlacement(0, 0, minusSix, zero, zero),
                new FPBuildingPlacement(0, 0, six, zero, zero),
            };
            var moved = new[]
            {
                new FPBuildingPlacement(0, 0, minusSix, zero, zero),
                new FPBuildingPlacement(0, 0, six, six, zero),        // one entry, one axis
            };

            Assert.IsTrue((bool)compare.Invoke(null, new object[] { a, 2, same, 2 }),
                "two identical sets did not compare equal, so the cache can never hit");
            Assert.IsFalse((bool)compare.Invoke(null, new object[] { a, 2, moved, 2 }),
                "two sets differing only in one entry's position compared EQUAL. Under a digest "
                + "collision that is a silently wrong navmesh — the failure mode with no self-heal");
            Assert.IsFalse((bool)compare.Invoke(null, new object[] { a, 2, same, 1 }),
                "a shorter set compared equal to a longer one with the same prefix");
        }

        // ─────────────────────────────────────────────── the destroy hook

        [Test]
        public void DestroyDue_IsCalledEveryTick_NotOnlyOnBoundaries()
        {
            // <= rather than ==, and the point of the looser predicate is that something has to look
            // for a straggler: a tombstone that slipped past its tick would otherwise sit forever,
            // invisible because the active-set filter already excludes it.
            var source = new FakeSource();
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out _));

            for (int tick = 0; tick < 4; tick++)
            {
                Frame f = FrameAt(tick);
                driver.Update(ref f);
            }

            Assert.AreEqual(new[] { 0, 1, 2, 3 }, source.DestroyedAt.ToArray(),
                "the destroy hook did not run once per tick with that tick");
        }

        // ─────────────────────────────────────────────── slicing, pacing, staleness

        [Test]
        public void SlicingChangesNothingAboutTheMesh_WhichIsWhatLetsTheBudgetBeAGuess()
        {
            // The property the whole feature rests on. AdvanceSlice is driven by wall-clock frames,
            // which are peer-local and differ between machines — so if the number of slices could
            // change the OUTPUT, slicing would be a desync source rather than a smoothing.
            //
            // The rebaker's own split-invariance is pinned elsewhere (FPNavMeshRebakeTaskTests). What
            // is pinned here is the DRIVER's two paths meeting: slices finishing the task before the
            // boundary versus the boundary finishing it synchronously. Those are different code paths
            // (TakeTask returns a done task, or steps it to completion) and only this compares them.
            FPNavMesh sliced = RunToBoundary(sliceEveryTick: true, out int slicedFin);
            FPNavMesh whole = RunToBoundary(sliceEveryTick: false, out int wholeFin);

            Assert.AreEqual(0, slicedFin,
                "the sliced run still finished at the boundary — then both runs took the same path "
                + "and this comparison proves nothing");
            Assert.Greater(wholeFin, 0,
                "the unsliced run did NOT finish at the boundary, so it never exercised the "
                + "synchronous remainder");

            string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(sliced, whole);
            Assert.IsNull(diff,
                $"slicing changed the mesh: {diff}. The budget is a wall-clock-paced, peer-local "
                + "input; if it can change the output it is a divergence source");
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(whole),
                FPNavMeshRebaker.ComputeFingerprint(sliced),
                "the fingerprints differ, which covers the vertex array that "
                + "DescribeFirstDifference does not read");
        }

        /// <summary>Drives one driver up to and across a boundary, optionally advancing slices, and
        /// hands back the mesh that landed plus how often the boundary had to finish the job.</summary>
        private static FPNavMesh RunToBoundary(bool sliceEveryTick, out int boundaryFinishes)
        {
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 10));
            source.Entries.Add(At(2, square, -4, -4, effective: 10));

            for (int tick = 0; tick < 10; tick++)
            {
                Frame f = FrameAt(tick);
                driver.Update(ref f);
                if (sliceEveryTick)
                    driver.AdvanceSlice(0.016f);
            }
            Frame boundary = FrameAt(10);
            driver.Update(ref boundary);

            boundaryFinishes = driver.BoundaryFinishes;
            return installer.Last;
        }

        [Test]
        public void SlicesAdvanceOnFramesOnly_NeverOnTicks()
        {
            // Two different clocks on purpose. A client catching up runs many ticks in one frame, so
            // a tick-paced budget would spend eleven frames' worth of work in the single frame this
            // feature exists to protect.
            var source = new FakeSource();
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 50));

            Frame start = FrameAt(0);
            driver.Update(ref start);
            Assert.IsTrue(driver.HasPendingRebake, "no task to advance — the fixture proves nothing");

            int before = driver.SlicedFrames;
            for (int tick = 1; tick < 20; tick++)
            {
                Frame f = FrameAt(tick);
                driver.Update(ref f);
            }
            Assert.AreEqual(before, driver.SlicedFrames,
                "a tick advanced the rebake. Ticks and frames are different clocks: a catching-up "
                + "client runs many ticks per frame, and spending a frame's budget per tick stalls "
                + "exactly the frame this protects");

            // One slice per frame, and at least one. Not a fixed total — that would pin how many
            // steps this fixture's rebake happens to take, which is not a property of the pacing.
            int last = driver.SlicedFrames;
            int advanced = 0;
            for (int i = 0; i < 5; i++)
            {
                driver.AdvanceSlice(0.016f);
                int now = driver.SlicedFrames;
                Assert.LessOrEqual(now - last, 1, "one frame advanced more than one slice");
                advanced += now - last;
                last = now;
            }
            Assert.Greater(advanced, 0, "frames did not advance the rebake");
        }

        [Test]
        public void ARollbackThatChangesTheAnswer_DropsTheStaleTaskInsteadOfInstallingIt()
        {
            // The task is peer-local derived state built for a PARTICULAR set, and it is identified
            // by digest for exactly this case: after a rollback the frame may describe a different
            // next boundary, and a task built for the old one must not be installed.
            //
            // Without the identity check it would be — the task is offered first, and it is finished
            // and waiting. The mesh would then carry a placement the frame does not describe, which
            // is the class of failure the whole invariant exists to remove.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 10));

            Frame f0 = FrameAt(0);
            driver.Update(ref f0);
            // Bounded, and deliberately not `while (HasPendingRebake)`: that reads true for a task
            // that has FINISHED (it means "a task object exists", not "there is work left"), and
            // AdvanceSlice returns early once done — so the obvious loop never terminates.
            for (int i = 0; i < 64; i++)
                driver.AdvanceSlice(0.016f);          // finish it, so it is ready to be installed
            int staleTask = driver.TaskId;

            // The rollback: the placement that was landing at 10 is replaced by a different one.
            source.Entries.Clear();
            source.Entries.Add(At(2, square, -4, -4, effective: 10));

            Frame f10 = FrameAt(10);
            driver.Update(ref f10);

            FPNavMesh expected = FPNavMeshRebaker.RebakePlacements(
                new FPNavMeshRebakeContext(SnapshotWithSquare(out int sq2)),
                new[] { new FPBuildingPlacement(sq2, FP64.FromInt(-4), FP64.FromInt(-4), FP64.Zero) },
                null, default, 1);
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(expected),
                FPNavMeshRebaker.ComputeFingerprint(installer.Last),
                $"the mesh is not the one the frame describes. The task built before the rollback "
                + $"(id {staleTask}) was installed anyway — it is identified by digest precisely so "
                + "that a frame asking for a different set drops it");
        }

        [Test]
        public void NoTaskIsStartedForASetASlotAlreadyHolds()
        {
            // The measurement that forced this: taskInstalls read 77 for 8 placements before it
            // existed. A predicting peer crosses each boundary many times, and every crossing
            // after the first asks for a set some slot already holds — so the task was restarted and
            // re-consumed on every one of them. Because the task is offered BEFORE the cache, the
            // restarted task kept winning and the cache never got to answer.
            var source = new FakeSource();
            var driver = new FPNavMeshRebakeDriver(source, new RecordingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(At(1, square, 4, 4, effective: 10));

            Frame warm = FrameAt(0);
            driver.Update(ref warm);
            Frame cross = FrameAt(10);
            driver.Update(ref cross);                 // now BOTH sets are in slots

            int tasksBefore = driver.TaskInstalls;
            int idBefore = driver.TaskId;

            // Back before the boundary. The set ahead is the one just installed, so there is nothing
            // to build — and starting a task anyway would burn the whole budget on a mesh that exists.
            for (int i = 0; i < 4; i++)
            {
                Frame back = FrameAt(9);
                driver.Update(ref back);
                Assert.IsFalse(driver.HasPendingRebake,
                    "a task was started for a set a slot already holds. Slicing toward a mesh that "
                    + "exists spends the budget for nothing, and because the task is offered before "
                    + "the cache it also stops the cache from ever answering");
                Frame fwd = FrameAt(10);
                driver.Update(ref fwd);
            }

            Assert.AreEqual(idBefore, driver.TaskId, "a new task was started despite the cache hit");
            Assert.AreEqual(tasksBefore, driver.TaskInstalls,
                "the boundary consumed a task again — with the set cached there should be none");
        }

        [Test]
        public void OneUpdateInstallsBeforeItReseeds()
        {
            // The one ordering inside Update that is load-bearing, and a mutation is what narrowed it
            // to this. The reseed RE-QUERIES each agent's triangle index against the installed mesh,
            // so running it first would seed indices into the mesh this tick is replacing — which is
            // precisely what the reseed exists to prevent.
            //
            // ⚠ The destroy's position is NOT load-bearing, and an earlier version of this test
            // claimed it was ("the task start reads the frame after the destroy"). It does not: the
            // placement table is surveyed ONCE at the top of Update, so everything downstream —
            // including the task start — reads the pre-destroy snapshot of the frame. Swapping the
            // destroy and the task start changes nothing observable, which the mutation showed.
            // Nothing is lost by that either: an entry whose removal tick has arrived is excluded
            // from every active set at this tick or later by the window filter anyway.
            var calls = new List<string>();
            var source = new FakeSource { Calls = calls };
            var installer = new RecordingInstaller { Calls = calls };
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));

            // A boundary tick, so the install and the reseed both fire in one Update.
            source.Entries.Add(At(1, square, 4, 4, effective: 5));
            Frame f = FrameAt(5);
            driver.Update(ref f);

            int install = calls.IndexOf("install");
            int reseed = calls.IndexOf("reseed");
            Assert.GreaterOrEqual(install, 0, "the boundary did not install");
            Assert.GreaterOrEqual(reseed, 0, "the boundary did not reseed");
            Assert.Less(install, reseed,
                "the reseed ran before the install, so it re-queried triangle indices against the "
                + "mesh being replaced — the exact staleness it exists to remove");
            Assert.Contains("destroy", calls, "the destroy hook did not run");
        }

        [Test]
        public void WithoutASnapshot_UpdateIsANoOp()
        {
            // A stage the game does not support placement on registers the driver anyway, and it has
            // to sit quiet rather than throw — the alternative is every host branching on it.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            source.Entries.Add(At(1, 0, 4, 4, effective: 0));

            Frame f = FrameAt(0);
            Assert.DoesNotThrow(() => driver.Update(ref f));
            Assert.AreEqual(0, installer.InstalledTriangleCounts.Count, "it installed without a stage");
            Assert.AreEqual(0, installer.Reseeds, "it reseeded without a stage");
            Assert.AreEqual(0, source.DestroyedAt.Count, "it destroyed without a stage");
        }

        private sealed class CountingLogger : Klotho.Logging.IKLogger
        {
            public int Errors;

            public bool IsEnabled(Klotho.Logging.KLogLevel level) => true;

            public void Log(Klotho.Logging.KLogLevel level, string message, System.Exception ex)
            {
                if (level == Klotho.Logging.KLogLevel.Error)
                    Errors++;
            }
        }
    }
}
