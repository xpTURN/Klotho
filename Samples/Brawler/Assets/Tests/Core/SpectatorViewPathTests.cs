using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// Which render path a spectator assigns.
    ///
    /// This is the premise the engine-side decision rests on: the spectator's error-correction calls were
    /// removed because a spectator has no LocalPlayerId — the engine falls back to 0 and the game's ids
    /// start at 1 — so every view lands on the snapshot interpolation path, and that path deliberately
    /// skips the rollback delta. Deltas computed there would have no consumer. Pinning it here rather
    /// than asserting "Capture is not called" keeps the test on the actual reason.
    ///
    /// The premise had a hole and the tests below now pin it closed. OwnerId 0 is a valid id and a P2P
    /// host holds exactly that, so a spectator watching a P2P session used to match its own fallback 0
    /// against the host's entity and treat it as local — CSP render path AND NonVerified binding, because
    /// the same comparison was written twice. `PlayerViewRegistry.IsActuallyLocal` had already
    /// disambiguated the identical collision via IsSpectatorMode; the factory now does it in the one
    /// expression both decisions share.
    ///
    /// Two of these tests exist because fixing only the render path would have been worse than the bug:
    /// a Verified render flag on a NonVerified binding renders from one timeline while the view's lifetime
    /// is decided by the other.
    /// </summary>
    [TestFixture]
    public class SpectatorViewPathTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;

        private static readonly FieldInfo SpectatorModeField = typeof(KlothoEngine)
            .GetField("_isSpectatorMode", BindingFlags.NonPublic | BindingFlags.Instance);

        private EcsSimulation _sim;
        private KlothoEngine _engine;
        private ProbeFactory _factory;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            var logger = loggerFactory.CreateLogger("SpectatorViewPathTests");

            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    MaxRollbackTicks = 8,
                    MaxEntities = MaxEntities,
                },
                new SessionConfig());
            _engine.Initialize(_sim, logger);

            // No network service, so LocalPlayerId takes its `?? 0` fallback — which is exactly the
            // spectator's situation, not a stand-in for it.
            SpectatorModeField.SetValue(_engine, true);
            Assert.That(_engine.LocalPlayerId, Is.Zero, "precondition: a spectator reports LocalPlayerId 0");

            _factory = ScriptableObject.CreateInstance<ProbeFactory>();
            _factory.Attach(_engine, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_factory != null) Object.DestroyImmediate(_factory);
        }

        private EntityRef SpawnOwned(int ownerId)
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            _sim.Frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            return entity;
        }

        /// <summary>
        /// An ordinary player entity (ids start at 1) is rendered from verified frames, so error
        /// correction has nothing to apply — the reason the spectator EC calls were vestigial.
        /// </summary>
        [Test]
        public void Spectator_OrdinaryPlayerEntity_RendersOnTheSnapshotPath()
        {
            var entity = SpawnOwned(ownerId: 1);

            ViewFlags flags = _factory.GetViewFlags(_sim.Frame, entity);

            Assert.That(flags & ViewFlags.EnableSnapshotInterpolation,
                Is.EqualTo(ViewFlags.EnableSnapshotInterpolation),
                "a spectator renders everyone from verified frames, and that path skips the rollback delta");
        }

        /// <summary>
        /// The OwnerId-0 collision, render side. A P2P host is playerId 0 and a spectator's LocalPlayerId
        /// falls back to 0, so this entity used to be rendered predicted — running ahead of every other
        /// entity and snapping on spectator rollback. A spectator owns nothing, so it must be on the
        /// snapshot path like everyone else.
        /// </summary>
        [Test]
        public void Spectator_OwnerIdZeroEntity_RendersOnTheSnapshotPath()
        {
            var entity = SpawnOwned(ownerId: 0);

            ViewFlags flags = _factory.GetViewFlags(_sim.Frame, entity);

            Assert.That(flags & ViewFlags.EnableSnapshotInterpolation,
                Is.EqualTo(ViewFlags.EnableSnapshotInterpolation),
                "a spectator has no local entity — the fallback 0 must not match the P2P host's owner id");
        }

        /// <summary>
        /// The same collision, binding side — the half the original repro test did not cover. The owner
        /// comparison lived in two places (GetViewFlags inline and IsPredictedRender), so guarding only
        /// the render path would have left this one deciding the view's lifetime from the predicted frame
        /// while the render read verified frames.
        /// </summary>
        [Test]
        public void Spectator_OwnerIdZeroEntity_BindsAsVerified()
        {
            var entity = SpawnOwned(ownerId: 0);

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out var behaviour), Is.True);
            Assert.That(behaviour, Is.EqualTo(BindBehaviour.Verified),
                "binding and render path have to agree, and for a spectator both are Verified");
        }

        /// <summary>
        /// The guard must not over-reach: an SD client really does own one entity, and that one stays
        /// predicted. Without this a blanket "never local" would pass the two tests above and silently
        /// put the local player several ticks late.
        /// </summary>
        [Test]
        public void SdClient_OwnEntity_StaysPredicted()
        {
            SpectatorModeField.SetValue(_engine, false);
            var entity = SpawnOwned(ownerId: 0);   // LocalPlayerId is 0 here, and this peer is not a spectator

            ViewFlags flags = _factory.GetViewFlags(_sim.Frame, entity);

            Assert.That(flags & ViewFlags.EnableSnapshotInterpolation, Is.EqualTo(ViewFlags.None),
                "the guard is IsSpectatorMode, not a blanket refusal — an SD client's own entity is predicted");
            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out var behaviour), Is.True);
            Assert.That(behaviour, Is.EqualTo(BindBehaviour.NonVerified),
                "and its binding follows the same expression");
        }

        /// <summary>
        /// The no-owner case shares the same expression through `hasOwner: false`. A server-authoritative
        /// entity has no OwnerComponent, and a spectator must render it from verified frames — the branch
        /// that used to be written out separately.
        /// </summary>
        [Test]
        public void Spectator_UnownedEntity_BindsAndRendersAsVerified()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });

            ViewFlags flags = _factory.GetViewFlags(_sim.Frame, entity);
            Assert.That(flags & ViewFlags.EnableSnapshotInterpolation,
                Is.EqualTo(ViewFlags.EnableSnapshotInterpolation));

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out var behaviour), Is.True);
            Assert.That(behaviour, Is.EqualTo(BindBehaviour.Verified));
        }

        // ── Test-only concrete factory ──
        private class ProbeFactory : EntityViewFactory
        {
            protected override GameObject ResolvePrefab(Frame frame, EntityRef entity) => null;
            protected override bool ShouldRender(Frame frame, EntityRef entity) => true;
        }
    }
}
