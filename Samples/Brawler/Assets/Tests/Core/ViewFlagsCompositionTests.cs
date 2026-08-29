using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// Prefab-authored vs Factory-decided ViewFlags (IMP103 V-3).
    ///
    /// Spawn used to assign the factory's answer straight onto the view. The factory can only express two
    /// of the sixteen combinations, so that assignment destroyed whatever else the prefab had authored —
    /// `DisableUpdate` on an asset was silently gone by the time the view ran.
    ///
    /// A plain OR would not have been the fix: it lets a prefab that ticked EnableSnapshotInterpolation
    /// force the verified path onto a locally-owned entity, which renders the local player several ticks
    /// late. So the merge is masked — the factory wins inside FactoryOwnedFlags, the prefab keeps the
    /// rest — and these tests pin both halves of that, including the direction OR would have broken.
    /// </summary>
    [TestFixture]
    public class ViewFlagsCompositionTests
    {
        private ProbeFactory _factory;

        [SetUp]
        public void SetUp() => _factory = ScriptableObject.CreateInstance<ProbeFactory>();

        [TearDown]
        public void TearDown()
        {
            if (_factory != null) Object.DestroyImmediate(_factory);
        }

        /// <summary>Prefab-owned bits survive a factory answer that says nothing about them.</summary>
        [Test]
        public void PrefabOwnedFlags_SurviveTheFactoryAnswer()
        {
            ViewFlags got = _factory.ComposeViewFlags(
                prefabFlags: ViewFlags.DisableUpdate | ViewFlags.DisablePositionUpdate,
                factoryFlags: ViewFlags.None);

            Assert.That(got, Is.EqualTo(ViewFlags.DisableUpdate | ViewFlags.DisablePositionUpdate),
                "assigning the factory answer used to wipe prefab authoring — that is the V-3 defect");
        }

        /// <summary>The factory's own bit is applied on top without disturbing the prefab's.</summary>
        [Test]
        public void FactoryOwnedFlag_IsAppliedAlongsidePrefabFlags()
        {
            ViewFlags got = _factory.ComposeViewFlags(
                prefabFlags: ViewFlags.DisablePositionUpdate,
                factoryFlags: ViewFlags.EnableSnapshotInterpolation);

            Assert.That(got, Is.EqualTo(ViewFlags.DisablePositionUpdate | ViewFlags.EnableSnapshotInterpolation));
        }

        /// <summary>
        /// The direction a plain OR gets wrong. The prefab ticked EnableSnapshotInterpolation, the factory
        /// said None (a locally-owned entity) — the factory must win, or the local player renders from
        /// verified frames and loses its input responsiveness.
        /// </summary>
        [Test]
        public void FactoryDecisionBeatsPrefab_OnAFactoryOwnedFlag()
        {
            ViewFlags got = _factory.ComposeViewFlags(
                prefabFlags: ViewFlags.EnableSnapshotInterpolation | ViewFlags.DisableUpdate,
                factoryFlags: ViewFlags.None);

            Assert.That(got & ViewFlags.EnableSnapshotInterpolation, Is.EqualTo(ViewFlags.None),
                "a prefab must not be able to force the verified path onto an entity the factory renders predicted");
            Assert.That(got & ViewFlags.DisableUpdate, Is.EqualTo(ViewFlags.DisableUpdate),
                "...while the prefab's own bits are untouched by that decision");
        }

        /// <summary>
        /// A factory that decides more per entity must widen the mask; the flag then behaves as
        /// factory-owned in both directions. This is the seam that keeps a wider override from being
        /// silently dropped — the same class of loss V-3 was.
        /// </summary>
        [Test]
        public void WidenedMask_LetsTheFactoryOwnMoreFlags()
        {
            var wide = ScriptableObject.CreateInstance<WideOwnershipFactory>();
            try
            {
                ViewFlags got = wide.ComposeViewFlags(
                    prefabFlags: ViewFlags.DisableUpdate,
                    factoryFlags: ViewFlags.None);

                Assert.That(got & ViewFlags.DisableUpdate, Is.EqualTo(ViewFlags.None),
                    "once claimed, the factory's answer governs the flag in both directions");
            }
            finally
            {
                Object.DestroyImmediate(wide);
            }
        }

        /// <summary>Default ownership is exactly the one runtime-derived flag, and nothing else.</summary>
        [Test]
        public void DefaultOwnership_IsSnapshotInterpolationOnly()
        {
            Assert.That(_factory.FactoryOwnedFlags, Is.EqualTo(ViewFlags.EnableSnapshotInterpolation));
        }

        // ── Test-only concrete factories ──
        private class ProbeFactory : EntityViewFactory
        {
            protected override GameObject ResolvePrefab(Frame frame, EntityRef entity) => null;
            protected override bool ShouldRender(Frame frame, EntityRef entity) => true;
        }

        private class WideOwnershipFactory : ProbeFactory
        {
            public override ViewFlags FactoryOwnedFlags
                => base.FactoryOwnedFlags | ViewFlags.DisableUpdate;
        }
    }
}
