using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.Tests
{
    // Reservation-pruning test components — 9210 block to avoid other fixtures' slots.

    // Engine-core marker: must stay reserved even when listed in the denylist (force-excluded).
    [KlothoComponent(9210)]
    [KlothoCoreComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PruneCoreComponent : IComponent
    {
        public int Value;
    }

    // Game component A — not in the denylist ⇒ reserved.
    [KlothoComponent(9211)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PruneGameAComponent : IComponent
    {
        public int Value;
    }

    // Game component B — listed in the denylist ⇒ pruned (no layout).
    [KlothoComponent(9212)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PruneGameBComponent : IComponent
    {
        public int Value;
    }

    // Cold-cache regression component — referenced ONLY by the pre-resolve test so its TypeIdCache stays
    // cold until that test resolves it pre-compute (mirrors PruneComponent<T> at server bootstrap).
    [KlothoComponent(9213)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PruneColdComponent : IComponent
    {
        public int Value;
    }

    // Singleton pruned type — verifies the GetSingleton path is guarded (its CountOffset=0 would otherwise
    // alias heap[0] and silently misread the count instead of throwing).
    [KlothoComponent(9214)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PruneSingletonComponent : IComponent
    {
        public int Value;
    }

    [TestFixture]
    public class ReservationPruningTests
    {
        private const int Max = 64;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp() => ComponentStorageRegistry.ResetForTesting();

        // Null denylist = no pruning: every registered type gets a layout (current behavior).
        [Test]
        public void NullPruneSet_ReservesAll_NoRegression()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            int a = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            Assert.Greater(ComponentStorageRegistry.GetLayout(a).TotalSize, 0, "A reserved");
            Assert.Greater(ComponentStorageRegistry.GetLayout(b).TotalSize, 0, "B reserved");
        }

        // Denylist prunes the listed type: no layout, absent from the sorted list, smaller heap.
        [Test]
        public void Denylist_PrunesListedType_ShrinksHeap()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            int heapFull = ComponentStorageRegistry.GetHeapSize(Max);
            int a = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { b }); // B denylisted

            Assert.Greater(ComponentStorageRegistry.GetLayout(a).TotalSize, 0, "A (not denylisted) reserved");
            Assert.AreEqual(0, ComponentStorageRegistry.GetLayout(b).TotalSize, "B (denylisted) pruned = no layout");
            Assert.Less(ComponentStorageRegistry.GetHeapSize(Max), heapFull, "pruned heap < full heap");

            bool bPresent = false;
            foreach (var id in ComponentStorageRegistry.RegisteredTypeIdsSorted)
                if (id == b) bPresent = true;
            Assert.IsFalse(bPresent, "pruned typeId absent from RegisteredTypeIdsSorted");
        }

        // Core force-exclude — a [KlothoCoreComponent] type stays reserved even when listed in the denylist.
        // Uses {core, b} (not {core} alone, which would collapse to no-pruning) so core survives WHILE b is
        // actively pruned — that is what "force-exclude under active pruning" means.
        [Test]
        public void CoreComponent_NeverPruned_DespiteDenylisting()
        {
            int core = ComponentStorageRegistry.GetTypeId<PruneCoreComponent>();
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { core, b }); // core + b listed

            Assert.Greater(ComponentStorageRegistry.GetLayout(core).TotalSize, 0,
                "core component force-excluded from the denylist (still reserved)");
            Assert.AreEqual(0, ComponentStorageRegistry.GetLayout(b).TotalSize,
                "non-core b is pruned (proves pruning is active, not a no-op collapse)");
        }

        // Empty non-null denylist = no pruning (reserve all). Regression pin for the removed allowlist landmine
        // where an empty non-null list meant "prune everything" and collapsed the core.
        [Test]
        public void EmptyDenylist_ReservesAll_NoLandmine()
        {
            int a = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int>()); // empty non-null

            Assert.Greater(ComponentStorageRegistry.GetLayout(a).TotalSize, 0, "A reserved (empty = no pruning)");
            Assert.Greater(ComponentStorageRegistry.GetLayout(b).TotalSize, 0, "B reserved (empty = no pruning)");
        }

        // A denylist of only core typeIds subtracts to empty ⇒ null ⇒ no pruning (reserve all).
        [Test]
        public void CoreOnlyDenylist_ReservesAll()
        {
            int a = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int core = ComponentStorageRegistry.GetTypeId<PruneCoreComponent>();
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { core }); // core only

            Assert.Greater(ComponentStorageRegistry.GetLayout(a).TotalSize, 0, "A reserved (core-only = no pruning)");
            Assert.Greater(ComponentStorageRegistry.GetLayout(core).TotalSize, 0, "core reserved");
        }

        // Accessing a pruned type through GetStorage<T> throws at the single chokepoint.
        [Test]
        public void PrunedType_GetStorage_Throws()
        {
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { b }); // B denylisted

            var frame = new Frame(Max, _logger);
            Assert.DoesNotThrow(() => frame.GetStorage<PruneGameAComponent>(), "non-denylisted access OK");
            Assert.Throws<System.InvalidOperationException>(
                () => frame.GetStorage<PruneGameBComponent>(), "pruned access throws (not DivideByZero)");
        }

        // Guard covers the GetSingleton path — a pruned singleton's default layout has CountOffset=0 that
        // aliases heap[0]; without the GetStorage chokepoint guard, GetSingleton would silently misread that
        // aliased count instead of failing. The guard (fired at GetStorage) throws cleanly first.
        [Test]
        public void PrunedSingleton_GetSingleton_Throws()
        {
            int s = ComponentStorageRegistry.GetTypeId<PruneSingletonComponent>();
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { s }); // singleton denylisted

            var frame = new Frame(Max, _logger);
            var ex = Assert.Throws<System.InvalidOperationException>(
                () => frame.GetSingleton<PruneSingletonComponent>(),
                "pruned singleton access throws at the guard (not a silent CountOffset=0 aliased misread)");
            StringAssert.Contains("reserved set", ex.Message, "throw came from the prune guard, not the count-0 path");
        }

        // Changing the denylist after freeze recomputes in DEBUG/editor/test builds (no throw).
        [Test]
        public void PruneSetChange_RecomputesInDebug()
        {
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { b });
            Assert.AreEqual(0, ComponentStorageRegistry.GetLayout(b).TotalSize, "B pruned initially");

            // Different denylist while frozen — auto-recompute in test builds (release throws; not exercised here).
            Assert.DoesNotThrow(() =>
                ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int>()));
            Assert.Greater(ComponentStorageRegistry.GetLayout(b).TotalSize, 0, "B reserved after recompute");
        }

        // Regression — a typeId resolved BEFORE layout compute (as PruneComponent<T> does at bootstrap)
        // caches a default(0) layout; freeze must invalidate that cache (via _generation bump) so a
        // reserved (non-denylisted) type is not falsely pruned by the GetStorage guard. (Surfaced as a live
        // server crash: BotComponent "registered but pruned" despite being reserved.)
        [Test]
        public void PreResolvedTypeId_NotFalselyPruned_AfterFreeze()
        {
            int cold = ComponentStorageRegistry.GetTypeId<PruneColdComponent>(); // cold: _layouts==null → stale default cached
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int>()); // Cold NOT denylisted = reserved
            var frame = new Frame(Max, _logger);
            Assert.DoesNotThrow(() => frame.GetStorage<PruneColdComponent>(),
                "freeze must invalidate the pre-compute stale TypeIdCache (else reserved type falsely pruned)");
        }

        // Freeze-eq must compare the EFFECTIVE prune-set, not the raw list: two rooms passing different-but-
        // equivalent raw lists (e.g. {core} vs empty, both collapsing to null) must NOT spuriously conflict.
        // Not unit-testable here — in test/editor/DEBUG builds a freeze conflict RECOMPUTES rather than throws,
        // so a raw-comparison bug would pass DoesNotThrow silently; the spurious-throw symptom only appears in
        // release builds (whose throw branch is compiled out in the test/editor/DEBUG build used here).
        [Test]
        [Ignore("Release-only: freeze-conflict recomputes (not throws) in test builds, so a raw-vs-effective " +
                "comparison bug is not observable here. Verified by reasoning + release build.")]
        public void EquivalentPruneSets_NoSpuriousFreezeConflict_ReleaseOnly()
        {
        }
    }
}
