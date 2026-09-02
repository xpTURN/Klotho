using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Core.Tests;

namespace xpTURN.Klotho.ECS.Tests
{
    // Component-signal test components — 9400 block to avoid other fixtures' slots.

    [KlothoComponent(9400)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SignalWatchedComponent : IComponent
    {
        public int Value;
    }

    [KlothoComponent(9401)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SignalIgnoredComponent : IComponent
    {
        public int Value;
    }

    // One-tick marker: the [KlothoCleanup] bulk pass empties it at every tick end.
    [KlothoComponent(9402)]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SignalCleanupComponent : IComponent
    {
        public int Value;
    }

    /// <summary>
    /// <c>ISignalOnComponentAdded/Removed</c> had interfaces, routing and analyzer coverage but no caller:
    /// a game could implement them and nothing would happen. These tests pin the wiring — and the gate
    /// that keeps it free for projects with no listeners.
    /// </summary>
    [TestFixture]
    public class ComponentSignalWiringTests
    {
        private const int MaxEntities = 32;

        private sealed class AddedListener : ISystem, ISignalOnComponentAdded<SignalWatchedComponent>
        {
            public readonly List<int> Values = new List<int>();
            public readonly List<int> Entities = new List<int>();
            public int BumpTo;

            public void Update(ref Frame frame) { }

            public void OnAdded(ref Frame frame, EntityRef entity, ref SignalWatchedComponent component)
            {
                Values.Add(component.Value);
                Entities.Add(entity.Index);
                if (BumpTo != 0) component.Value = BumpTo;   // proves the ref points at the storage slot
            }
        }

        private sealed class RemovedListener : ISystem, ISignalOnComponentRemoved<SignalWatchedComponent>
        {
            public readonly List<int> Values = new List<int>();

            public void Update(ref Frame frame) { }

            public void OnRemoved(ref Frame frame, EntityRef entity, SignalWatchedComponent component)
                => Values.Add(component.Value);
        }

        // Order marker: appends its own name so the firing order can be asserted.
        private sealed class OrderedListener : ISystem, ISignalOnComponentAdded<SignalWatchedComponent>
        {
            public string Name;
            public List<string> Log;

            public void Update(ref Frame frame) { }

            public void OnAdded(ref Frame frame, EntityRef entity, ref SignalWatchedComponent component)
                => Log.Add(Name);
        }

        private static EcsSimulation CreateSimulation(params (object system, SystemPhase phase)[] systems)
        {
            var sim = new EcsSimulation(MaxEntities);
            foreach (var (system, phase) in systems)
                sim.AddSystem(system, phase);
            sim.Initialize();
            return sim;
        }

        [Test]
        public void Add_FiresListenerForThatComponentType()
        {
            var listener = new AddedListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 7 });

            Assert.AreEqual(new[] { 7 }, listener.Values.ToArray());
            Assert.AreEqual(new[] { entity.Index }, listener.Entities.ToArray());
        }

        [Test]
        public void Add_DoesNotFireForAnotherComponentType()
        {
            var listener = new AddedListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalIgnoredComponent { Value = 7 });

            Assert.IsEmpty(listener.Values);
        }

        [Test]
        public void Add_RefPointsAtTheStorageSlot()
        {
            var listener = new AddedListener { BumpTo = 99 };
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 7 });

            Assert.AreEqual(99, sim.Frame.GetReadOnly<SignalWatchedComponent>(entity).Value,
                "the listener's write must land in the storage, not in a copy");
        }

        [Test]
        public void Remove_FiresBeforeRemovalWithTheValue()
        {
            var listener = new RemovedListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 42 });
            sim.Frame.Remove<SignalWatchedComponent>(entity);

            Assert.AreEqual(new[] { 42 }, listener.Values.ToArray(),
                "the removed value must be readable — the signal fires before the slot is gone");
            Assert.IsFalse(sim.Frame.Has<SignalWatchedComponent>(entity));
        }

        [Test]
        public void Remove_DoesNotFireWhenTheComponentIsAbsent()
        {
            var listener = new RemovedListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Remove<SignalWatchedComponent>(entity);   // never added

            Assert.IsEmpty(listener.Values, "Remove is a no-op for an absent component, so nothing fires");
        }

        [Test]
        public void FiringOrderFollowsPhaseThenRegistrationOrder()
        {
            var log = new List<string>();
            var late = new OrderedListener { Name = "late", Log = log };     // PostUpdate
            var first = new OrderedListener { Name = "first", Log = log };   // Update, registered first
            var second = new OrderedListener { Name = "second", Log = log };

            var sim = CreateSimulation(
                (late, SystemPhase.PostUpdate),
                (first, SystemPhase.Update),
                (second, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 1 });

            Assert.AreEqual(new[] { "first", "second", "late" }, log.ToArray());
        }

        [Test]
        public void NoListeners_LeavesTheGateClosed()
        {
            var sim = CreateSimulation();
            Assert.IsFalse(sim.Frame.SignalMasks.Any,
                "a project with no listeners must not pay for a mask lookup on the Add path");
        }

        // Warmup then average, the same shape the repo's other alloc gates use: tiered JIT and one-shot
        // caches (TypeIdCache<T> first touch) make the first calls allocate differently from the
        // hundredth, and a per-Add gate's cost is the steady state.
        [Test]
        public void ClosedGate_AddAndRemoveAllocateNothing()
        {
            var sim = CreateSimulation();   // no listeners at all
            var entity = sim.Frame.CreateEntity();

            for (int i = 0; i < 64; i++)
                AddThenRemove(sim.Frame, entity);

            const int iterations = 256;
            long perCycle = AllocProbe.SmallestPerCall(() => AddThenRemove(sim.Frame, entity), iterations);

            Assert.AreEqual(0, perCycle,
                "the gate must be free for a project that registers no listeners");
        }

        private static void AddThenRemove(Frame frame, EntityRef entity)
        {
            frame.Add(entity, new SignalWatchedComponent { Value = 1 });
            frame.Remove<SignalWatchedComponent>(entity);
        }

        [Test]
        public void ListenerRegisteredAfterInitialize_FiresImmediately()
        {
            // Design property, not a regression: production registers every system before Initialize
            // (ISimulationCallbacks is contracted to run before Engine.Initialize). The mask is built in
            // AddSystem precisely so this cannot depend on a tick boundary.
            var sim = CreateSimulation();
            var listener = new AddedListener();
            sim.AddSystem(listener, SystemPhase.Update);

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 5 });

            Assert.AreEqual(new[] { 5 }, listener.Values.ToArray());
        }

        // --- destroy path (P2) ---

        private sealed class DestroyOrderListener : ISystem,
            ISignalOnComponentRemoved<SignalWatchedComponent>,
            ISignalOnComponentRemoved<SignalIgnoredComponent>,
            IEntityDestroyedSystem
        {
            public readonly List<string> Log = new List<string>();

            public void Update(ref Frame frame) { }

            public void OnRemoved(ref Frame frame, EntityRef entity, SignalWatchedComponent component)
                => Log.Add($"watched:{component.Value}:alive={frame.Entities.IsAlive(entity.Index)}");

            public void OnRemoved(ref Frame frame, EntityRef entity, SignalIgnoredComponent component)
                => Log.Add($"ignored:{component.Value}");

            public void OnEntityDestroyed(ref Frame frame, EntityRef entity) => Log.Add("entity");
        }

        [Test]
        public void Destroy_FiresRemovedForEveryComponentTheEntityHad()
        {
            var listener = new DestroyOrderListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 3 });
            sim.Frame.Add(entity, new SignalIgnoredComponent { Value = 4 });
            sim.Frame.DestroyEntity(entity);

            // typeId order (9400 before 9401), components before the entity signal, and the entity is
            // still alive while its components are being reported.
            Assert.AreEqual(new[] { "watched:3:alive=True", "ignored:4", "entity" }, listener.Log.ToArray());
        }

        [Test]
        public void Destroy_DoesNotFireForComponentsTheEntityLacked()
        {
            var listener = new DestroyOrderListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 3 });
            sim.Frame.DestroyEntity(entity);

            Assert.AreEqual(new[] { "watched:3:alive=True", "entity" }, listener.Log.ToArray(),
                "the entity never had SignalIgnoredComponent, so nothing fires for it");
        }

        [Test]
        public void Destroy_WithNoListeners_AllocatesNothing()
        {
            var sim = CreateSimulation();   // no listeners: the per-typeId gate stays closed

            for (int i = 0; i < 64; i++)
                SpawnThenDestroy(sim.Frame);

            const int iterations = 128;
            long perCycle = AllocProbe.SmallestPerCall(() => SpawnThenDestroy(sim.Frame), iterations);

            Assert.AreEqual(0, perCycle);
        }

        private static void SpawnThenDestroy(Frame frame)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new SignalWatchedComponent { Value = 1 });
            frame.DestroyEntity(entity);
        }

        [Test]
        public void Destroy_ListenerExceptionPropagates()
        {
            // R-11(a): the exception propagates and the entity is left half-destroyed. Accepted, not
            // handled — nothing catches a tick exception, so no execution remains to observe it. This
            // test pins the policy rather than a desirable state.
            var sim = CreateSimulation((new ThrowingRemovedListener(), SystemPhase.Update));

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new SignalWatchedComponent { Value = 1 });

            Assert.Throws<InvalidOperationException>(() => sim.Frame.DestroyEntity(entity));
            Assert.IsTrue(sim.Frame.Entities.IsAlive(entity.Index),
                "the destroy did not complete — documenting the accepted consequence");
        }

        private sealed class ThrowingRemovedListener : ISystem,
            ISignalOnComponentRemoved<SignalWatchedComponent>
        {
            public void Update(ref Frame frame) { }
            public void OnRemoved(ref Frame frame, EntityRef entity, SignalWatchedComponent component)
                => throw new InvalidOperationException("listener bug");
        }

        // --- bulk clear (P3) ---

        private sealed class CleanupMarkAddingSystem : ISystem
        {
            public int Next = 1;
            public void Update(ref Frame frame)
            {
                var entity = frame.CreateEntity();
                frame.Add(entity, new SignalCleanupComponent { Value = Next++ });
            }
        }

        private sealed class CleanupRemovedListener : ISystem,
            ISignalOnComponentRemoved<SignalCleanupComponent>
        {
            public readonly List<int> Values = new List<int>();
            public void Update(ref Frame frame) { }
            public void OnRemoved(ref Frame frame, EntityRef entity, SignalCleanupComponent component)
                => Values.Add(component.Value);
        }

        [Test]
        public void BulkClear_FiresRemovedBeforeTheStorageIsEmptied()
        {
            var listener = new CleanupRemovedListener();
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new CleanupMarkAddingSystem(), SystemPhase.Update)
                .AddSystem(listener, SystemPhase.Update)
                .Initialize();

            harness.Tick();

            Assert.AreEqual(new[] { 1 }, listener.Values.ToArray(),
                "the value must be readable — the signal has to fire before the clear");
            Assert.AreEqual(0, harness.Simulation.Frame.Filter<SignalCleanupComponent>().Count,
                "and the clear still happened");

            harness.Tick();
            Assert.AreEqual(new[] { 1, 2 }, listener.Values.ToArray(), "once per tick, per entity");
        }

        [Test]
        public void BulkClear_FiresOncePerEntity()
        {
            var listener = new CleanupRemovedListener();
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new CleanupMarkAddingSystem(), SystemPhase.Update)
                .AddSystem(new CleanupMarkAddingSystem { Next = 10 }, SystemPhase.Update)
                .AddSystem(listener, SystemPhase.Update)
                .Initialize();

            harness.Tick();

            Assert.AreEqual(new[] { 1, 10 }, listener.Values.ToArray(),
                "two markers in one tick means two signals, in dense order");
        }

        [Test]
        public void Resim_FiresAgain_AndTheStateHashIsUnchanged()
        {
            // Signals are not events: a rollback replays the tick and the listener runs a second time.
            // A listener that only writes to the frame is reproduced exactly — which is what makes the
            // replayed hash match — while one that accumulates outside the frame (this listener's list,
            // deliberately) double-counts. That asymmetry is the rule ECS.md §6 states.
            var listener = new CleanupRemovedListener();
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new CleanupMarkAddingSystem(), SystemPhase.Update)
                .AddSystem(listener, SystemPhase.Update)
                .Initialize();

            long[] forward = harness.RunAndRecord(3);
            int firedForward = listener.Values.Count;

            long[] replayed = harness.RollbackAndReplay(1, 3);

            Assert.AreEqual(forward[forward.Length - 1], replayed[replayed.Length - 1],
                "frame state must replay identically");
            Assert.Greater(listener.Values.Count, firedForward,
                "the listener fired again during the resim — signals are per execution, not per tick");
        }

        [Test]
        public void RingSlotsNeverGetTheSinkOrTheMasks()
        {
            var listener = new AddedListener();
            var sim = CreateSimulation((listener, SystemPhase.Update));

            var target = new Frame(MaxEntities, null);
            target.CopyFrom(sim.Frame);

            Assert.IsNull(target.SignalSink, "CopyFrom must not carry the sink into a state container");
            Assert.IsNull(target.SignalMasks, "CopyFrom must not carry the masks into a state container");
        }

        [Test]
        public void UnregisteredComponentListener_DoesNotBreakRegistration()
        {
            // A listener for a component type with no [KlothoComponent] has no typeId to key a mask bit
            // on. That must be skipped, not throw: SystemRunnerTests' doubles are exactly this shape.
            var sim = new EcsSimulation(MaxEntities);
            Assert.DoesNotThrow(() => sim.AddSystem(new UnregisteredListener(), SystemPhase.Update));
            Assert.DoesNotThrow(() => sim.Initialize());
        }

        // No [KlothoComponent]: never registered, so it has no typeId to key a mask bit on. It only ever
        // appears as a generic argument, so it needs no storage and no fields.
        private struct UnregisteredComponentForSignals : IComponent
        {
            public int GetSerializedSize() => 0;
            public void Serialize(ref Serialization.SpanWriter writer) { }
            public void Deserialize(ref Serialization.SpanReader reader) { }
            public ulong GetHash(ulong hash) => hash;
        }

        private sealed class UnregisteredListener : ISystem,
            ISignalOnComponentAdded<UnregisteredComponentForSignals>
        {
            public void Update(ref Frame frame) { }
            public void OnAdded(ref Frame frame, EntityRef entity,
                                ref UnregisteredComponentForSignals component) { }
        }
    }
}
