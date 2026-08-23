using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.ECS.Diagnostics;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.ECS.Tests
{
    // [KlothoCleanup] test components. typeIds in the 9300 block (9200s are the maxCount fixture).
    //
    // NOTE these registrations are process-global: declaring them here means every fixture in this
    // assembly runs with two cleanup types in the layout. That is deliberate — it also proves the pass
    // is harmless for storages nobody touches — but it is why the "report omits idle slots" assertion
    // below drives SystemPerfMonitor directly instead of going through a runner.

    [KlothoComponent(9300)]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CleanupMarkComponent : IComponent
    {
        public int Value;
    }

    [KlothoComponent(9301)]
    [KlothoCleanup(CleanupMode.DestroyEntity)]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 1)]
    public partial struct CleanupDoomedComponent : IComponent
    {
    }

    // Second DestroyEntity marker: two of these on one entity must not double-destroy.
    [KlothoComponent(9302)]
    [KlothoCleanup(CleanupMode.DestroyEntity)]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 1)]
    public partial struct CleanupAlsoDoomedComponent : IComponent
    {
    }

    // Cleaned-up singleton: legal with RemoveComponent, and the reason the TryGetSingleton contract
    // exists (GetSingleton throws once the carrier is gone).
    [KlothoComponent(9303)]
    [KlothoSingletonComponent]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CleanupSingletonComponent : IComponent
    {
        public int Value;
    }

    // No [KlothoCleanup] — must stay out of the cleanup caches and survive the pass.
    [KlothoComponent(9304)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CleanupUnmarkedComponent : IComponent
    {
        public int Value;
    }

    /// <summary>Adds one CleanupMarkComponent per tick so the pass has something to clear.</summary>
    internal sealed class MarkAddingSystem : ISystem
    {
        public int Adds;

        public void Update(ref Frame frame)
        {
            var e = frame.CreateEntity();
            frame.Add(e, new CleanupMarkComponent { Value = frame.Tick });
            Adds++;
        }
    }

    /// <summary>Observes the mark storage count as seen at the START of a tick (before any add).</summary>
    internal sealed class MarkCountProbeSystem : ISystem
    {
        public int LastSeenAtTickStart = -1;

        public void Update(ref Frame frame)
            => LastSeenAtTickStart = frame.Filter<CleanupMarkComponent>().Count;
    }

    internal sealed class NoOpTickSystem : ISystem
    {
        public void Update(ref Frame frame) { }
    }

    [TestFixture]
    public class ComponentCleanupTests
    {
        private const int MaxEntities = 64;

        [SetUp]
        public void SetUp() => ComponentStorageRegistry.ResetForTesting();

        private static Frame NewFrame() => new Frame(MaxEntities, null);

        // ── Registration pipeline: the attribute reaches the registry, unmarked types do not ──

        [Test]
        public void Registration_CleanupModeReachesRegistry_UnmarkedStaysNone()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(MaxEntities);

            Assert.AreEqual(CleanupMode.RemoveComponent, ComponentStorageRegistry.GetCleanupMode(
                ComponentStorageRegistry.GetTypeId<CleanupMarkComponent>()));
            Assert.AreEqual(CleanupMode.DestroyEntity, ComponentStorageRegistry.GetCleanupMode(
                ComponentStorageRegistry.GetTypeId<CleanupDoomedComponent>()));
            Assert.AreEqual(CleanupMode.None, ComponentStorageRegistry.GetCleanupMode(
                ComponentStorageRegistry.GetTypeId<CleanupUnmarkedComponent>()));
        }

        // The observable form of "a project with no cleanup types pays nothing": the target lists hold
        // exactly the declared types, so an unmarked type is never touched by the pass.
        [Test]
        public void TargetCaches_ContainOnlyDeclaredTypes_Ascending()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(MaxEntities);

            int mark      = ComponentStorageRegistry.GetTypeId<CleanupMarkComponent>();
            int singleton = ComponentStorageRegistry.GetTypeId<CleanupSingletonComponent>();
            int doomed    = ComponentStorageRegistry.GetTypeId<CleanupDoomedComponent>();
            int also      = ComponentStorageRegistry.GetTypeId<CleanupAlsoDoomedComponent>();
            int unmarked  = ComponentStorageRegistry.GetTypeId<CleanupUnmarkedComponent>();

            var clear = ComponentStorageRegistry.CleanupClearTypeIds.ToArray();
            var destroy = ComponentStorageRegistry.CleanupDestroyTypeIds.ToArray();

            Assert.Contains(mark, clear);
            Assert.Contains(singleton, clear);
            Assert.Contains(doomed, destroy);
            Assert.Contains(also, destroy);
            CollectionAssert.DoesNotContain(clear, unmarked);
            CollectionAssert.DoesNotContain(destroy, unmarked);

            CollectionAssert.IsOrdered(clear, "pass order must be ascending typeId (no registration-order dependency)");
            CollectionAssert.IsOrdered(destroy);
            Assert.AreEqual(clear.Length + destroy.Length, ComponentStorageRegistry.CleanupTypeCount);
        }

        // ── RemoveComponent ──

        [Test]
        public void RemoveComponent_ClearedAtTickEnd_AndEmptyAtNextTickStart()
        {
            var probe = new MarkCountProbeSystem();
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(probe, SystemPhase.PreUpdate)
                .AddSystem(new MarkAddingSystem(), SystemPhase.Update)
                .Initialize();

            harness.Tick();
            // Cleared at the end of the tick it was added in.
            Assert.AreEqual(0, harness.Simulation.Frame.Filter<CleanupMarkComponent>().Count);

            harness.Tick();
            // ...and the next tick started from empty, not from last tick's leftovers.
            Assert.AreEqual(0, probe.LastSeenAtTickStart);
            Assert.AreEqual(0, harness.Simulation.Frame.Filter<CleanupMarkComponent>().Count);
        }

        [Test]
        public void RemoveComponent_UnmarkedComponentSurvivesThePass()
        {
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new NoOpTickSystem())
                .Initialize();

            var e = harness.Simulation.Frame.CreateEntity();
            harness.Simulation.Frame.Add(e, new CleanupUnmarkedComponent { Value = 7 });
            harness.Simulation.Frame.Add(e, new CleanupMarkComponent { Value = 7 });

            harness.Tick();

            Assert.AreEqual(1, harness.Simulation.Frame.Filter<CleanupUnmarkedComponent>().Count);
            Assert.AreEqual(0, harness.Simulation.Frame.Filter<CleanupMarkComponent>().Count);
        }

        // Rollback/resim reproduce the cleaned state: the pass runs before the snapshot/hash, so a
        // replayed tick must land on the same hash as the forward run.
        [Test]
        public void Cleanup_IsReproducedByRollbackAndReplay()
        {
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new MarkAddingSystem())
                .Initialize();

            harness.AssertReplayMatchesForward(forwardTicks: 6, rollbackTo: 2);
        }

        // ── DestroyEntity ──

        [Test]
        public void DestroyEntity_DestroysCarriers_AndLeavesOthersAlone()
        {
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new NoOpTickSystem())
                .Initialize();

            var frame = harness.Simulation.Frame;
            var doomed = frame.CreateEntity();
            frame.Add(doomed, new CleanupDoomedComponent());
            var survivor = frame.CreateEntity();
            frame.Add(survivor, new CleanupUnmarkedComponent { Value = 1 });

            harness.Tick();

            Assert.IsFalse(harness.Simulation.Frame.Entities.IsAlive(doomed), "carrier must be destroyed");
            Assert.IsTrue(harness.Simulation.Frame.Entities.IsAlive(survivor));
        }

        // Two DestroyEntity markers on one entity: the second destroy must be skipped, not re-run.
        // Without the IsAlive check, Frame.DestroyEntity would re-fire OnEntityDestroyed and re-run
        // RemoveAllComponents (its own guard sits at the third step only).
        [Test]
        public void DestroyEntity_TwoMarkersOnOneEntity_DestroyedOnce()
        {
            int destroyedCallbacks = 0;
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new NoOpTickSystem())
                .Initialize();

            var frame = harness.Simulation.Frame;
            var e = frame.CreateEntity();
            frame.Add(e, new CleanupDoomedComponent());
            frame.Add(e, new CleanupAlsoDoomedComponent());
            frame.OnEntityDestroyed += _ => destroyedCallbacks++;

            harness.Tick();

            Assert.IsFalse(harness.Simulation.Frame.Entities.IsAlive(e));
            Assert.AreEqual(1, destroyedCallbacks, "OnEntityDestroyed must fire exactly once per entity");
        }

        // ── Singleton contract (F-12 / D-4) ──

        [Test]
        public void CleanedSingleton_TryGetReturnsFalse_GetThrows()
        {
            var harness = new EcsRollbackHarness(MaxEntities)
                .AddSystem(new NoOpTickSystem())
                .Initialize();

            var frame = harness.Simulation.Frame;
            var carrier = frame.CreateEntity();
            frame.Add(carrier, new CleanupSingletonComponent { Value = 3 });
            Assert.IsTrue(frame.TryGetSingleton<CleanupSingletonComponent>(out _), "present before the pass");

            harness.Tick();

            Assert.IsFalse(harness.Simulation.Frame.TryGetSingleton<CleanupSingletonComponent>(out _));
            Assert.Throws<InvalidOperationException>(() =>
            {
                ref var _ = ref harness.Simulation.Frame.GetSingleton<CleanupSingletonComponent>();
            }, "GetSingleton on a cleaned singleton throws — games must use TryGetSingleton");
        }

        // ── Fingerprint fold (R-1) ──
        //
        // Writable in-process only because Register overwrites an already-registered type's metadata
        // and ResetForTesting clears the frozen flag. _cleanupMode itself is retained across the reset,
        // which is why the second Register call is what changes the mode.
        [Test]
        public void LayoutFingerprint_DiffersWhenOnlyCleanupModeDiffers()
        {
            ComponentStorageRegistry.Register<CleanupFoldProbeComponent>(9310, cleanup: CleanupMode.None);
            ComponentStorageRegistry.EnsureLayoutComputed(MaxEntities);
            long fpNone = ComponentStorageRegistry.LayoutFingerprint;

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.Register<CleanupFoldProbeComponent>(9310, cleanup: CleanupMode.RemoveComponent);
            ComponentStorageRegistry.EnsureLayoutComputed(MaxEntities);
            long fpRemove = ComponentStorageRegistry.LayoutFingerprint;

            Assert.AreNotEqual(fpNone, fpRemove,
                "cleanup mode is a determinism input: two builds disagreeing on it must not share a fingerprint");

            // ...and the mode is the ONLY difference, so restoring it restores the value.
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.Register<CleanupFoldProbeComponent>(9310, cleanup: CleanupMode.None);
            ComponentStorageRegistry.EnsureLayoutComputed(MaxEntities);
            Assert.AreEqual(fpNone, ComponentStorageRegistry.LayoutFingerprint);
        }

        // ── Perf instrumentation (D-8) ──

        [Test]
        public void PerfMonitor_BindsBothCleanupSlots_AndSystemStatsStayAligned()
        {
            var runner = new SystemRunner();
            var marker = new MarkAddingSystem();
            runner.AddSystem(marker, SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            bool sawClear = false, sawDestroy = false, sawSystem = false;
            foreach (var stat in runner.PerfMonitor.Stats)
            {
                if (stat.Name == "(builtin) CleanupClear") sawClear = true;
                if (stat.Name == "(builtin) CleanupDestroy") sawDestroy = true;
                if (stat.Name == nameof(MarkAddingSystem))
                {
                    sawSystem = true;
                    // The system's own slot must still be its own — this is the regression that a
                    // mis-adjusted FirstSystemPerfIndex would break (stats shifted by one).
                    Assert.AreEqual(1, stat.UpdateCount);
                }
            }

            Assert.IsTrue(sawClear && sawDestroy, "both cleanup slots must be bound");
            Assert.IsTrue(sawSystem, "system stats must remain present and correctly named");
        }

        // Idle builtin slots are omitted from the report rather than printed as permanent zero rows.
        // Driven directly: the slot indices are fixed consts, so binding cannot be made conditional.
        [Test]
        public void ToText_OmitsSlotsThatNeverExecuted()
        {
            var monitor = new SystemPerfMonitor();
            monitor.Bind(new ReadOnlySpan<string>(new[] { "RanOnce", "NeverRanAtAllWithALongName" }));
            monitor.Record(0, 10, 0);

            string text = monitor.ToText();

            StringAssert.Contains("RanOnce", text);
            Assert.IsFalse(text.Contains("NeverRanAtAllWithALongName"),
                "a slot with UpdateCount == 0 must not appear");
            // The omitted (longer) name must not have widened the name column either.
            StringAssert.Contains("RanOnce ", text);
        }

        // ── zero-alloc, both paths ──
        //
        // The 4-tick warmup is load-bearing: it consumes _dirty (EnsureSorted) and _perfBindDirty
        // (RebindPerf, which allocates the name buffer) plus the destroy buffer's one-time growth.
        [Test]
        public void CleanupPasses_AreAllocationFree_MonitorOff()
            => AssertSteadyStateZeroAlloc(enableMonitor: false);

        [Test]
        public void CleanupPasses_AreAllocationFree_MonitorOn()
            => AssertSteadyStateZeroAlloc(enableMonitor: true);

        private static void AssertSteadyStateZeroAlloc(bool enableMonitor)
        {
            var runner = new SystemRunner();
            runner.AddSystem(new DoomedSpawnSystem(), SystemPhase.Update);
            if (enableMonitor) runner.EnablePerfMonitor();

            var frame = NewFrame();
            for (int i = 0; i < 4; i++) runner.RunUpdateSystems(ref frame);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) runner.RunUpdateSystems(ref frame);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated, "cleanup passes must reuse their buffer");
        }
    }

    // Spawns one doomed entity per tick, so both cleanup passes do real work every tick.
    internal sealed class DoomedSpawnSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var e = frame.CreateEntity();
            frame.Add(e, new CleanupDoomedComponent());
            frame.Add(e, new CleanupMarkComponent { Value = frame.Tick });
        }
    }

    // Registered by hand inside the fold test only (no [KlothoComponent] attribute, so the generator's
    // ModuleInitializer never touches it and it cannot perturb other fixtures' layouts).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CleanupFoldProbeComponent : IComponent
    {
        public int Value;

        public const int TYPE_ID = 9310;
        public int GetSerializedSize() => 4;
        public ulong GetHash(ulong h) => xpTURN.Klotho.Deterministic.FPHash.Hash(h, Value);
        public void Serialize(ref xpTURN.Klotho.Serialization.SpanWriter w) => w.WriteInt32(Value);
        public void Deserialize(ref xpTURN.Klotho.Serialization.SpanReader r) => Value = r.ReadInt32();
    }
}
