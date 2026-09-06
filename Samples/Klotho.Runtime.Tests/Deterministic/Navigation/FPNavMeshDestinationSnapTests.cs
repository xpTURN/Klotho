using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The editor visualizer's destination click now goes through the passable projection, the
    /// same way <c>BotFSMHelper.SnapDestination</c> does for a bot.
    ///
    /// <para><b>What these tests can and cannot see.</b> The snap itself still lives in
    /// <c>FPNavMeshAgentSimulator</c> (and its Godot twin), which no test project references — so
    /// nothing here observes it. What they pin is the PATTERN the tool must copy, plus the one
    /// engine member the change added (<see cref="FPNavAgentSystem.ResolvePlanMask"/>). The snap
    /// is still gated by eye; the DIAGNOSIS no longer is — it moved to
    /// <see cref="FPNavPathFailure"/> and <c>FPNavPathFailureTests</c> pins it directly.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshDestinationSnapTests
    {
        private const int Cells = 8;                 // 8x8 lattice cells of 2 units = 16x16 world
        private const double Bx = 8.0, Bz = 8.0;     // footprint centre

        private static FPVector2 At(double x, double z)
            => new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));

        private static FPNavMesh Retained(FPNavMesh field) => NavAgentTestHelper.RebakeWithBuildings(
            field, NavAgentTestHelper.Building(Bx, Bz, retain: true));

        private static NavAgentComponent Agent()
        {
            var nav = default(NavAgentComponent);
            NavAgentComponent.Init(ref nav, new FPVector3(FP64.One, FP64.Zero, FP64.One));
            return nav;
        }

        #region The mask fold, and what folding it wrong costs

        /// <summary>
        /// The reason the fold is a public engine member instead of a line at the caller.
        ///
        /// <para>A zero override means "no override", i.e. <c>DEFAULT_AREA_MASK</c>. A caller that
        /// duplicates the fold and drops the <c>!= 0 ?</c> passes 0 as the mask, and 0 shares a bit
        /// with nothing — so EVERY triangle becomes impassable and every click is refused. The
        /// symptom (the agent does not move) is identical to the defect the snap exists to remove,
        /// which is what makes the mistake expensive to diagnose rather than merely wrong.</para>
        /// </summary>
        [Test]
        public void TheFoldTurnsAZeroOverrideIntoTheDefault_AndTheRawZeroRefusesEverything()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);
            var nav = Agent();
            FPVector2 ground = At(3.0, 3.0);

            Assert.AreEqual(0, nav.PlanAreaMaskOverride, "fixture: a fresh agent carries no override");
            Assert.AreEqual(FPNavAgentSystem.DEFAULT_AREA_MASK, FPNavAgentSystem.ResolvePlanMask(nav),
                "a zero override must resolve to the default, not to zero");
            Assert.AreEqual(FPNavAgentSystem.DEFAULT_AREA_MASK, FPNavAgentSystem.ResolveWalkMask(nav),
                "same for the walk side");

            Assert.GreaterOrEqual(query.FindPassableTriangle(ground, FPNavAgentSystem.ResolvePlanMask(nav)), 0,
                "folded through the engine, ordinary ground is usable");
            Assert.AreEqual(-1, query.FindPassableTriangle(ground, nav.PlanAreaMaskOverride),
                "THE TOOTH: passing the raw override refuses ordinary ground — this is the "
                + "duplicated-fold mistake, and its symptom is the one this change removes");
        }

        /// <summary>
        /// And an override that IS set survives the fold, on each side independently — the pair
        /// worth setting in the tool is the one where plan and walk disagree.
        /// </summary>
        [Test]
        public void AnOverrideThatIsSetSurvivesTheFold_PerSide()
        {
            var nav = Agent();
            NavAgentComponent.SetAreaMask(ref nav, FPNavMeshAreas.ALL_AREAS, FPNavMeshAreas.DEFAULT_AGENT_MASK);

            Assert.AreEqual(FPNavMeshAreas.ALL_AREAS, FPNavAgentSystem.ResolvePlanMask(nav),
                "plan keeps its own value");
            Assert.AreEqual(FPNavMeshAreas.DEFAULT_AGENT_MASK, FPNavAgentSystem.ResolveWalkMask(nav),
                "walk keeps its own value");
            Assert.AreNotEqual(FPNavAgentSystem.ResolvePlanMask(nav), FPNavAgentSystem.ResolveWalkMask(nav),
                "the two sides are resolved separately — the asymmetry is the point");
        }

        #endregion

        #region The height belongs to the SNAPPED point, not to the click

        /// <summary>
        /// The projection is XZ and a destination is an <c>FPVector3</c>, so the caller has to
        /// choose a Y. Keeping the click's own y pairs a moved XZ with an unrelated height; on any
        /// mesh that is not flat that puts the destination off the surface, and on a multi-floor
        /// mesh it puts it on the wrong floor.
        ///
        /// <para>Measured on a slope rather than on stacked floors because the property under test
        /// is "Y is a function of the SNAPPED XZ": a slope makes the two candidate answers differ
        /// by a value the test can state exactly, while stacked floors only make them differ by
        /// whichever floor the fixture happened to build.</para>
        /// </summary>
        [Test]
        public void TheHeightIsResampledAtTheSnappedXZ_NotAtTheClick()
        {
            // y = x/2 over a 4x4 world: one quad, split into two triangles by the pipeline.
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(4), FP64.FromInt(2), FP64.Zero),
                new FPVector3(FP64.FromInt(4), FP64.FromInt(2), FP64.FromInt(4)),
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),
            };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            var mesh = FPNavMeshBuildPipeline.Build(
                vertices, indices, new[] { 0, 0 }, 8.0, null, bakeAgentRadius: 0.5);
            var query = new FPNavMeshQuery(mesh, null);
            int mask = FPNavAgentSystem.ResolvePlanMask(Agent());

            // A click off the LOW edge, halfway along the slope: the projection pulls it back in
            // z, and the point it lands on is mid-slope — so the resampled height is a genuine
            // interpolation and not a vertex value the sampler would have clamped to anyway.
            FPVector2 click = At(2.0, -2.0);
            FP64 clickY = FP64.FromInt(5);          // whatever height the click carried
            Assert.AreEqual(-1, query.FindPassableTriangle(click, mask), "fixture: the click is off-mesh");

            FPVector2 snapped = query.ProjectToPassable(click, FP64.FromInt(8), mask, out int tri);
            Assert.GreaterOrEqual(tri, 0, "fixture: the projection should reach the surface here");
            Assert.AreNotEqual(click.y.RawValue, snapped.y.RawValue, "fixture: the point moved in z");

            FP64 atSnapped = query.SampleHeight(snapped, tri);

            // The height that belongs to the snapped point is the surface height there: y = x/2.
            Assert.AreEqual((snapped.x / FP64.FromInt(2)).RawValue, atSnapped.RawValue,
                "the resampled height must be the surface height at the SNAPPED xz");
            Assert.IsTrue(atSnapped > FP64.Zero && atSnapped < FP64.FromInt(2),
                "fixture: mid-slope, so the interpolation is load-bearing (not a clamped vertex y)");

            // And this is the defect: keeping the click's own y instead would put the destination
            // 4 units above the surface it was snapped to.
            Assert.AreNotEqual(clickY.RawValue, atSnapped.RawValue,
                "the click's own height is NOT the height of the snapped point");
            Assert.AreEqual(FP64.FromInt(4).RawValue, (clickY - atSnapped).RawValue,
                "and the error is the full slope offset, not a rounding difference");

            // The pair holds as a triangle membership, not just as a number: the destination built
            // from (snapped, resampled y) is found on the triangle its height came from.
            var dest = new FPVector3(snapped.x, atSnapped, snapped.y);
            Assert.AreEqual(tri, query.FindTriangle(dest.ToXZ(), dest.y),
                "the destination must sit on the triangle its height came from");
        }

        #endregion

        #region How far the snap can actually reach

        /// <summary>
        /// The two numbers the tool's knob and its refusal message are written against.
        ///
        /// <para>The projection's fallback searches the click's cell plus the 8 around it, so the
        /// reach scales with <c>GridCellSize</c> and with nothing else. That gives a
        /// <b>guarantee</b> — anything within one cell of the click is certain to be considered,
        /// because the 3x3 block extends at least one full cell past the click on every axis and
        /// triangles are registered in every cell their bbox touches — and a <b>ceiling</b>, past
        /// which raising <c>maxDist</c> buys nothing at all.</para>
        ///
        /// <para>The default field cannot show this: its grid cell is 8 world units, wider than any
        /// footprint the helper can place, so the one-ring reach never binds. That is exactly what
        /// <c>ProjectionReach_IsBoundedToTheNeighbouringCells</c> said it could not measure. This
        /// fixture shrinks the broadphase cell instead of growing the building.</para>
        /// </summary>
        [Test]
        public void TheReachIsOneCellRing_SoTheGuaranteeHoldsAndTheCeilingBites()
        {
            const double GridCell = 0.5;
            var mesh = Retained(NavAgentTestHelper.CreateOpenFieldNavMesh(Cells, GridCell));
            var query = new FPNavMeshQuery(mesh, null);
            int mask = FPNavAgentSystem.ResolvePlanMask(Agent());

            double cell = mesh.GridCellSize.ToDouble();
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            Assert.AreEqual(GridCell, cell, 1e-9, "fixture: the broadphase cell is the one asked for");
            TestContext.Out.WriteLine(
                $"grid cell {cell:F3}, footprint half {half:F3} = {half / cell:F2} cells");

            // ── the guarantee ────────────────────────────────────────────────────────────────
            // A click just inside the footprint edge: the nearest passable ground is 0.15 world
            // units away, well inside one cell. maxDist = GridCellSize (the tool's default) must
            // find it, and it must land on ground the mask allows.
            FPVector2 nearEdge = At(Bx - half + 0.15, Bz);
            Assert.AreEqual(-1, query.FindPassableTriangle(nearEdge, mask),
                "fixture: the click itself is on the footprint");

            FPVector2 escape = query.ProjectToPassable(nearEdge, FP64.FromDouble(cell), mask, out int nearTri);
            Assert.GreaterOrEqual(nearTri, 0,
                "GUARANTEE: passable ground within one cell of the click is always found");
            Assert.AreEqual(nearTri, query.FindPassableTriangle(escape, mask),
                "and the point it returns really is on the passable triangle it names");

            // ── the ceiling ──────────────────────────────────────────────────────────────────
            // The footprint centre. Passable ground is 3.5 cells away — past the far corner of the
            // 3x3 block — so no maxDist can reach it. This is the claim the refusal message makes:
            // dragging the slider to the top does not help, and the message has to say why.
            FPVector2 centre = At(Bx, Bz);
            Assert.AreEqual(-1, query.FindPassableTriangle(centre, mask),
                "fixture: the centre is on the footprint");

            query.ProjectToPassable(centre, FP64.FromDouble(cell), mask, out int atDefault);
            Assert.AreEqual(-1, atDefault, "fixture: the default reach does not cover the centre");

            query.ProjectToPassable(centre, FP64.FromInt(1000), mask, out int atAbsurd);
            Assert.AreEqual(-1, atAbsurd,
                "CEILING: maxDist is not the binding constraint here — the one-cell-ring search is, "
                + "so an arbitrarily large reach still refuses");

            // Where the boundary actually falls, recorded rather than asserted: it depends on where
            // in its cell the click landed, and a triangle wider than a cell inflates it further
            // (registration is by bbox), so it is not a constant worth pinning.
            for (double d = 0.2; d <= half + 0.6; d += 0.2)
            {
                FPVector2 p = At(Bx - half + d, Bz);
                query.ProjectToPassable(p, FP64.FromInt(1000), mask, out int t);
                TestContext.Out.WriteLine(
                    $"  {d / cell:F1} cells into the footprint → {(t >= 0 ? "reached" : "refused")}");
            }
        }

        #endregion
    }
}
