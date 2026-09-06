using System;

using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The navigation caps as instance values: what <see cref="FPNavTuning"/> refuses, what it must
    /// keep accepting, and that a non-default cap actually binds where it claims to.
    ///
    /// <para>The first test is the one with history: the validation rules must pass the shipped
    /// defaults. An earlier draft of this work would have rejected them — the funnel wants
    /// <c>corridorCap + 1</c> portals and the defaults ship 128 against 128 — so the relationship
    /// is documented rather than enforced, and this fixture is where that stays true.</para>
    /// </summary>
    [TestFixture]
    public class FPNavTuningTests
    {
        #region The buffer is the authority — a caller cannot overrun it

        /// <summary>
        /// <c>FindCorners</c> must never write past its own buffer, whatever <c>maxCorners</c> the
        /// caller asks for.
        ///
        /// <para><b>Why a lower bound in <c>Validate</c> is not enough.</b> The callers do not agree
        /// on <c>maxCorners</c>: the tick path passes 4, both editor overlays pass 8, and
        /// <c>FPNavMeshRebindTests</c> passes 16. A validation floor of 4 would let a
        /// <c>maxWaypoints: 4</c> tuning through and it would still overrun in an overlay. The
        /// number belongs to the caller; the buffer belongs to the funnel, so the funnel clamps.</para>
        ///
        /// <para>The serpentine is the fixture that makes this reachable — an open field is convex,
        /// so its funnel emits a single corner and would overrun nothing.</para>
        /// </summary>
        [Test]
        public void FindCorners_ClampsToItsOwnBuffer_NotTheCallersRequest()
        {
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(16, 6, out var endCell);
            var tuning = new FPNavTuning(maxWaypoints: 2);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);

            FPVector3 start = NavAgentTestHelper.CellCenter(0, 0);
            FPVector3 end = NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz);
            Assert.IsTrue(pathfinder.FindPath(start, end, FPNavAgentSystem.DEFAULT_AREA_MASK,
                out int[] corridor, out int corridorLength), "fixture: the serpentine is connected");
            Assert.Greater(corridorLength, 2, "fixture: the corridor must bend, or there is no third corner");

            int count = 0;
            Assert.DoesNotThrow(
                () => count = funnel.FindCorners(corridor, corridorLength, start, end, 8),
                "a caller asking for more corners than the buffer holds must be clamped, not "
                + "allowed to write past the end of it");
            Assert.LessOrEqual(count, tuning.MaxWaypoints,
                "and the count reported must be one the buffer can actually back");
        }

        /// <summary>
        /// <c>Funnel</c>'s <c>corridorLength == 1</c> shortcut writes <c>_waypoints[1]</c> without
        /// consulting anything, so a one-waypoint buffer is overrun by the second write. That path
        /// is the editor overlay's, not the tick's, which is why no shipped caller has hit it.
        /// </summary>
        [Test]
        public void Funnel_SingleTriangleShortcut_FitsTheBuffer()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(maxWaypoints: 1);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);

            var start = new FPVector3(FP64.One, FP64.Zero, FP64.One);
            var end = new FPVector3(FP64.FromDouble(1.5), FP64.Zero, FP64.FromDouble(1.5));
            int tri = query.FindTriangle(start.ToXZ());
            Assert.GreaterOrEqual(tri, 0, "fixture: the start must be on the mesh");

            Assert.DoesNotThrow(
                () => funnel.Funnel(new[] { tri }, 1, start, end, out _, out _),
                "the single-triangle shortcut must fit a one-waypoint buffer too");
        }

        #endregion

        #region Identity — value equality, the digest, and the cross-check

        /// <summary>Every knob a tuning carries, one per entry, for the sweeps below.</summary>
        private static readonly (string Name, FPNavTuning Value)[] OneKnobOff =
        {
            ("MaxAgents", new FPNavTuning(maxAgents: 63)),
            ("CollisionResolveIterations", new FPNavTuning(collisionResolveIterations: 3)),
            ("BfsFrontierCap", new FPNavTuning(bfsFrontierCap: 255)),
            ("MaxIterations", new FPNavTuning(maxIterations: 999)),
            ("MaxPortals", new FPNavTuning(maxPortals: 127)),
            ("MaxWaypoints", new FPNavTuning(maxWaypoints: 63)),
            ("MaxNeighbors", new FPNavTuning(maxNeighbors: 7)),
            ("MaxOrcaLines", new FPNavTuning(maxOrcaLines: 63)),
            ("MoveMaxQueue", new FPNavTuning(moveMaxQueue: 47)),
            ("CorridorCap", new FPNavTuning(corridorCap: 127)),
        };

        [Test]
        public void Equality_IsByValue()
        {
            Assert.IsTrue(new FPNavTuning(corridorCap: 32) == new FPNavTuning(corridorCap: 32),
                "two tunings with the same knobs are the same tuning");
            Assert.AreEqual(new FPNavTuning(corridorCap: 32).GetHashCode(),
                new FPNavTuning(corridorCap: 32).GetHashCode(),
                "equal values must hash equal");
            Assert.IsTrue(FPNavTuning.Default != new FPNavTuning(corridorCap: 32));
        }

        /// <summary>
        /// Every knob counts. A field missing from <c>Equals</c> makes the cross-check wave through
        /// a stack that disagrees about exactly that knob — the sweep is the only thing that finds it.
        /// </summary>
        [Test]
        public void Equality_CountsAllTenKnobs()
        {
            Assert.AreEqual(10, OneKnobOff.Length, "the sweep must cover every knob");
            foreach (var (name, off) in OneKnobOff)
                Assert.IsTrue(FPNavTuning.Default != off, $"{name} is not counted by Equals");
        }

        /// <summary>The same sweep for the digest — <c>Equals</c> passing proves nothing about it.</summary>
        [Test]
        public void Digest_CountsAllTenKnobs()
        {
            foreach (var (name, off) in OneKnobOff)
                Assert.AreNotEqual(FPNavTuning.Default.Digest, off.Digest,
                    $"{name} is not folded into the digest — a peer differing only here would still shake hands");
        }

        /// <summary>
        /// <c>Default</c> folds to zero so that adding the tuning term moved no existing
        /// fingerprint — see the remarks on <see cref="FPNavTuning.Digest"/>.
        /// </summary>
        [Test]
        public void Digest_OfDefault_IsTheIdentity()
        {
            Assert.AreEqual(0L, FPNavTuning.Default.Digest,
                "a non-zero default would move every recorded fingerprint for a change that alters "
                + "no behaviour, and every replay taken before it would be refused");
        }

        /// <summary>
        /// The digest crosses processes, so it must not ride a randomised hash seed. Pinned to a
        /// literal: if the fold ever changes, this says so out loud instead of silently refusing
        /// peers.
        /// </summary>
        [Test]
        public void Digest_IsStableAcrossRuns()
        {
            Assert.AreEqual(unchecked((long)0xEC454BDBE5820B8CUL), new FPNavTuning(corridorCap: 32).Digest,
                "the digest must be reproducible from the values alone");
        }

        /// <summary>
        /// The default path's navigation fingerprint is bit-identical to what it was before the
        /// tuning term existed. Pinned to the value captured from the build immediately before this
        /// change: it is the only way a test can assert "before and after", and it is what makes
        /// replays recorded earlier still readable.
        /// </summary>
        [Test]
        public void DefaultStack_NavFingerprint_DidNotMove()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null);

            Assert.AreEqual(unchecked((long)0x303F02AD9AB50251UL), system.GetNavFingerprint(),
                "adding the tuning term must not move the default fingerprint — if it does, every "
                + "replay recorded before this change is refused for a change that altered nothing");
        }

        [Test]
        public void CrossCheck_RefusesAStackThatDisagrees()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(corridorCap: 32);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);   // the forgotten one

            var ex = Assert.Throws<ArgumentException>(
                () => new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning));
            StringAssert.Contains("pathfinder", ex.Message, "the message must name the odd one out");
            StringAssert.Contains("CorridorCap", ex.Message, "and the knob that differs");
        }

        [Test]
        public void CrossCheck_CoversSetAvoidance_ButNotNull()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(corridorCap: 32);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning);

            Assert.Throws<ArgumentException>(() => system.SetAvoidance(new FPNavAvoidance()),
                "a default avoidance under a non-default stack is the mismatch this catches");
            Assert.DoesNotThrow(() => system.SetAvoidance(null),
                "null is 'no avoidance', a different question answered where it is used");
            Assert.DoesNotThrow(() => system.SetAvoidance(new FPNavAvoidance(tuning)));
        }

        /// <summary>
        /// The other door into the trio. Without a check here the constructor's is bypassable:
        /// build a consistent stack, then swap in one that is not.
        /// </summary>
        [Test]
        public void CrossCheck_CoversTheFourArgSwap_AndLeavesTheOneArgAlone()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(corridorCap: 32);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning);

            var other = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var strayQuery = new FPNavMeshQuery(other, null);              // Default
            var strayFunnel = new FPNavMeshFunnel(other, strayQuery, null);
            var strayPath = new FPNavMeshPathfinder(other, strayQuery, null);

            Assert.Throws<ArgumentException>(
                () => system.SwapNavMesh(other, strayQuery, strayPath, strayFunnel),
                "the four-argument swap replaces the trio outright, so it needs the same guard");

            Assert.DoesNotThrow(() => system.SwapNavMesh(other),
                "the one-argument form rebinds the existing trio, so the tuning is preserved");
        }

        /// <summary>
        /// The agent system's visited buffer is sized from the same knob the query walks with.
        /// They used to be a hardcoded 48 against a default 48 — equal by coincidence, and the
        /// handover would have silently truncated the moment anyone raised <c>moveMaxQueue</c>,
        /// with no counter to say so.
        /// </summary>
        [Test]
        public void VisitedBuffer_TracksMoveMaxQueue_NotAHardcodedForty8()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(moveMaxQueue: 128);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning);

            Assert.AreEqual(128, system.DebugVisitedBufferLength,
                "the handover buffer must be as wide as the walk the query is allowed to make — "
                + "a narrower one truncates the visited path silently");
        }

        #endregion

        #region Validation

        [Test]
        public void Default_IsAccepted()
        {
            Assert.DoesNotThrow(() => FPNavTuning.Default.Validate(),
                "the shipped values must satisfy every rule we enforce — a rule that rejects them " +
                "makes the default constructor throw");
        }

        [Test]
        public void Zeroed_IsRejected()
        {
            // `new FPNavTuning()` binds to the implicit parameterless struct constructor, so this
            // is the shape a caller most easily produces by accident.
            var zeroed = default(FPNavTuning);
            var ex = Assert.Throws<ArgumentException>(() => zeroed.Validate());
            StringAssert.Contains("FPNavTuning.Default", ex.Message,
                "the message has to point at the way in");
        }

        [Test]
        public void NeighborsAtOrAboveTheLineBudget_IsRejected()
        {
            var starved = new FPNavTuning(maxNeighbors: 64, maxOrcaLines: 64);
            Assert.Throws<ArgumentException>(() => starved.Validate(),
                "MaxOrcaLines - MaxNeighbors is the obstacle budget; at zero the boundary stops " +
                "constraining agents");
        }

        [Test]
        public void CorridorCapAboveTheStorageCeiling_IsRejected()
        {
            var overCeiling = new FPNavTuning(corridorCap: FPNavTuning.CorridorCeiling + 1);
            Assert.Throws<ArgumentException>(() => overCeiling.Validate(),
                "the corridor lives in a fixed buffer sized at compile time");
        }

        [Test]
        public void NonPositiveCap_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => new FPNavTuning(maxPortals: 0).Validate());
            Assert.Throws<ArgumentException>(() => new FPNavTuning(maxIterations: -1).Validate());
        }

        [Test]
        public void EveryConstructor_ValidatesWhatItWasHanded()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(4);
            var bad = new FPNavTuning(maxNeighbors: 64, maxOrcaLines: 64);

            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);

            Assert.Throws<ArgumentException>(() => new FPNavMeshQuery(mesh, null, bad));
            Assert.Throws<ArgumentException>(() => new FPNavMeshPathfinder(mesh, query, null, bad));
            Assert.Throws<ArgumentException>(() => new FPNavMeshFunnel(mesh, query, null, bad));
            Assert.Throws<ArgumentException>(() => new FPNavAvoidance(bad));
            Assert.Throws<ArgumentException>(
                () => new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, bad));
        }

        #endregion

        #region A cap that binds

        [Test]
        public void SmallerAgentCap_DropsMoreFromThePositionCorrection()
        {
            const int agents = 12;
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var tuning = new FPNavTuning(maxAgents: 8);

            var query = new FPNavMeshQuery(mesh, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning);
            system.SetAvoidance(new FPNavAvoidance(tuning));

            var positions = new FPVector3[agents];
            var velocities = new FPVector2[agents];
            for (int i = 0; i < agents; i++)
            {
                positions[i] = new FPVector3(
                    FP64.FromDouble(4.0 + (i % 4) * 0.1), FP64.Zero,
                    FP64.FromDouble(4.0 + (i / 4) * 0.1));
                velocities[i] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                positions, velocities, out var entities, maxEntities: agents + 8);
            system.Update(ref frame, entities, entities.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(agents - 8, system.DebugCollisionResolveTruncatedCount,
                "the cap this system was built with is the one that binds, not MAX_AGENTS");
        }

        [Test]
        public void SmallerCorridorCap_ClampsTheSearchAndReportsIt()
        {
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(16, 6, out var endCell);
            var tuning = new FPNavTuning(corridorCap: 16);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0),
                NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out int corridorLength);

            Assert.IsTrue(found, "the serpentine is connected end to end");
            Assert.AreEqual(16, corridorLength, "the instance cap clamps the result");
            Assert.AreEqual(1, pathfinder.DebugCorridorTruncatedCount);
        }

        [Test]
        public void SameTuning_TwiceOverTheSameSearch_IsIdentical()
        {
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(16, 6, out var endCell);
            var tuning = new FPNavTuning(corridorCap: 24, maxIterations: 2048);

            int[] Run()
            {
                var query = new FPNavMeshQuery(mesh, null, tuning);
                var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
                pathfinder.FindPath(
                    NavAgentTestHelper.CellCenter(0, 0),
                    NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz),
                    FPNavAgentSystem.DEFAULT_AREA_MASK, out int[] corridor, out int length);
                var copy = new int[length];
                Array.Copy(corridor, copy, length);
                return copy;
            }

            CollectionAssert.AreEqual(Run(), Run(),
                "a non-default tuning has to be as deterministic as the default one");
        }

        #endregion

        #region Corridor copy truncation (a wiring-bug detector, not a diagnostic)

        [Test]
        public unsafe void SetCorridor_ReportsWhatItDropped()
        {
            var src = new[] { 3, 1, 4, 1, 5 };
            int* dst = stackalloc int[8];
            int dstLen = 0;

            Assert.AreEqual(0, NavCorridorHelper.SetCorridor(dst, ref dstLen, 8, src, src.Length),
                "everything fits — nothing dropped");
            Assert.AreEqual(5, dstLen);

            Assert.AreEqual(3, NavCorridorHelper.SetCorridor(dst, ref dstLen, 2, src, src.Length),
                "a shorter destination drops the tail, and that used to be silent");
            Assert.AreEqual(2, dstLen);
        }

        [Test]
        public void WiredSystem_NeverTruncatesTheCorridorCopy()
        {
            // The search and the storage share one cap, so the copy cannot truncate. A nonzero
            // count here means they were built from different caps.
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(16, 6, out var endCell);
            var tuning = new FPNavTuning(corridorCap: 16);
            var query = new FPNavMeshQuery(mesh, null, tuning);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null, tuning);
            var funnel = new FPNavMeshFunnel(mesh, query, null, tuning);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null, tuning);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                NavAgentTestHelper.CellCenter(0, 0), 0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(
                ref nav, NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz));
            for (int tick = 1; tick <= 30; tick++)
                system.Update(ref frame, entities, 1, tick, NavAgentTestHelper.DT);

            Assert.AreEqual(0, system.DebugCorridorCopyTruncatedCount,
                "0 is the only correct value for this counter under one effective cap");
        }

        #endregion
    }
}
