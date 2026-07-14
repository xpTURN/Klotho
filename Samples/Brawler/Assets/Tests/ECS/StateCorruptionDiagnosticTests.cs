// Every test here drives the positive-control injection, which only exists under the fault-injection
// define: the mutator is applied in EcsSimulation.Tick under #if KLOTHO_FAULT_INJECTION, and
// FaultInjection.Reset() is compiled only under it. With the define off (the shipped default) this whole
// fixture is meaningless and must not compile — otherwise it breaks the default Brawler build.
#if KLOTHO_FAULT_INJECTION
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Diagnostics;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// Positive-control validation for the desync-diagnostic funnel: when a KNOWN
    /// component field is corrupted at a KNOWN tick, the layered <see cref="StateHashBreakdown"/>
    /// must localize the divergence to exactly that component type and leave every other layer clean.
    /// Complements the negative control (the hash-only salt <c>ForceDesyncHashTick</c>, which corrupts
    /// Total alone). Also pins the re-execution-idempotency invariant: a "fire once" mutation
    /// would be washed out by verified/rollback resim and the desync would never surface.
    /// </summary>
    [TestFixture]
    public class StateCorruptionDiagnosticTests
    {
        private const int MaxEntities = 32;
        private const int CorruptionTick = 3;
        private static readonly List<ICommand> NoCommands = new List<ICommand>();
        private static readonly FPVector3 Sentinel = new FPVector3(FP64.FromInt(999), FP64.FromInt(-7), FP64.Zero);

        [TearDown]
        public void TearDown() => FaultInjection.Reset();

        // Builds a sim carrying one entity with a Transform (the corruption target) and a Health
        // component (a control layer that must stay untouched). No systems / commands, so the only
        // state change across ticks is whatever the fault injection introduces.
        private EcsSimulation CreateSimulation(out EntityRef entity)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.Initialize();
            entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            sim.Frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            return sim;
        }

        // Sets Transform.Position to a fixed sentinel — a re-execution-idempotent mutation (SET, not
        // accumulate) as RegisterStateCorruption requires.
        private static void CorruptTransform(EntityRef entity, Frame frame)
        {
            ref var t = ref frame.Get<TransformComponent>(entity);
            t.Position = Sentinel;
        }

        private static int BreakdownIndexOf<T>() where T : unmanaged, IComponent
        {
            int typeId = ComponentStorageRegistry.GetTypeId<T>();
            var sorted = ComponentStorageRegistry.RegisteredTypeIdsSorted;   // internal, visible to tests
            for (int i = 0; i < sorted.Length; i++)
                if (sorted[i] == typeId) return i;
            return -1;
        }

        [Test]
        public void StateCorruption_BreakdownLocalizesMutatedComponent_OthersClean()
        {
            var clean = CreateSimulation(out var cleanEntity);
            var dirty = CreateSimulation(out var dirtyEntity);

            // Arm the dirty sim directly (the EcsSimulation.Tick application path). Engine-level arming
            // by LocalPlayerId is exercised separately via RegisterStateCorruption below.
            dirty.StateCorruptionTick = CorruptionTick;
            dirty.StateCorruptionMutator = f => CorruptTransform(dirtyEntity, f);

            for (int t = 0; t <= CorruptionTick; t++)
            {
                // Lockstep until the corruption tick executes.
                if (t < CorruptionTick)
                    Assert.AreEqual(clean.GetStateHash(), dirty.GetStateHash(),
                        $"sims must be identical before the corruption tick (t={t})");
                clean.Tick(NoCommands);
                dirty.Tick(NoCommands);
            }

            var bdClean = new StateHashBreakdown();
            var bdDirty = new StateHashBreakdown();
            clean.GetStateHash(bdClean);
            dirty.GetStateHash(bdDirty);

            Assert.AreNotEqual(bdClean.Total, bdDirty.Total, "Total must diverge after the corruption");

            int transformIdx = BreakdownIndexOf<TransformComponent>();
            int healthIdx = BreakdownIndexOf<HealthComponent>();
            Assert.GreaterOrEqual(transformIdx, 0, "TransformComponent must be registered");
            Assert.GreaterOrEqual(healthIdx, 0, "HealthComponent must be registered");

            Assert.AreNotEqual(bdClean.ComponentHashes[transformIdx], bdDirty.ComponentHashes[transformIdx],
                "the mutated component (TransformComponent) must be localized");
            Assert.AreEqual(bdClean.ComponentHashes[healthIdx], bdDirty.ComponentHashes[healthIdx],
                "an untouched control component (HealthComponent) must stay clean");

            // Every OTHER component layer must be byte-identical — no false accusation.
            for (int i = 0; i < bdClean.ComponentHashes.Length; i++)
            {
                if (i == transformIdx) continue;
                Assert.AreEqual(bdClean.ComponentHashes[i], bdDirty.ComponentHashes[i],
                    $"non-target component layer index {i} must be unchanged");
            }

            // No snapshot participants here, so the system layer must match exactly (stays clean).
            Assert.AreEqual(bdClean.SystemHashes.Length, bdDirty.SystemHashes.Length);
            for (int i = 0; i < bdClean.SystemHashes.Length; i++)
                Assert.AreEqual(bdClean.SystemHashes[i], bdDirty.SystemHashes[i],
                    $"system layer {i} must be unchanged");
        }

        [Test]
        public void StateCorruption_ReExecutionReappliesCorruption_NotWashedOut()
        {
            long cleanHash;
            {
                var clean = CreateSimulation(out _);
                for (int t = 0; t <= CorruptionTick; t++) clean.Tick(NoCommands);
                cleanHash = clean.GetStateHash();
            }

            var dirty = CreateSimulation(out var entity);
            dirty.StateCorruptionTick = CorruptionTick;
            dirty.StateCorruptionMutator = f => CorruptTransform(entity, f);

            // Advance to the corruption tick, saving the pre-tick snapshot each step (engine order).
            for (int t = 0; t < CorruptionTick; t++) { dirty.SaveSnapshot(); dirty.Tick(NoCommands); }
            dirty.SaveSnapshot();                 // pre-CorruptionTick snapshot (clean)
            dirty.Tick(NoCommands);               // executes CorruptionTick → mutator fires
            long firstHash = dirty.GetStateHash();

            Assert.AreNotEqual(cleanHash, firstHash, "corruption must diverge from the clean run");

            // Mimic verified/rollback resim: rewind to the (clean) pre-tick snapshot and re-execute.
            dirty.Rollback(CorruptionTick);
            dirty.Tick(NoCommands);
            long resimHash = dirty.GetStateHash();

            Assert.AreEqual(firstHash, resimHash,
                "the mutator must re-fire on every re-execution of the tick (R9-F1); a one-shot mutation " +
                "would be washed out by resim and the desync would silently vanish");
            Assert.AreNotEqual(cleanHash, resimHash, "re-executed corruption must still diverge");
        }

        [Test]
        public void RegisterStateCorruption_PopulatesStatics_ResetClears()
        {
            FaultInjection.RegisterStateCorruption(CorruptionTick, new[] { 1, 3 }, f => { });

            Assert.AreEqual(CorruptionTick, FaultInjection.StateCorruptionTick);
            Assert.IsNotNull(FaultInjection.StateCorruptionMutator);
            Assert.IsTrue(FaultInjection.StateCorruptionPlayerIds.Contains(1));
            Assert.IsTrue(FaultInjection.StateCorruptionPlayerIds.Contains(3));
            Assert.IsFalse(FaultInjection.StateCorruptionPlayerIds.Contains(2));

            FaultInjection.Reset();

            Assert.AreEqual(-1, FaultInjection.StateCorruptionTick);
            Assert.IsNull(FaultInjection.StateCorruptionMutator);
            Assert.AreEqual(0, FaultInjection.StateCorruptionPlayerIds.Count);
        }
    }
}
#endif
