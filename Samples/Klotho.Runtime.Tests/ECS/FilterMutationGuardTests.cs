using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace xpTURN.Klotho.ECS.Tests
{
    // FilterMutationGuard fixture components. typeIds in the 9500 block (9200-9204 maxCount,
    // 9210-9214 reservation pruning, 9220 stale-ref, 9230s, 9300s cleanup, 9400s are taken).
    //
    // Five distinct types because the arity-5 case has to be exercised, and because several tests
    // need to PIN which storage the filter walks: a multi-type filter picks the smallest storage at
    // construction, so a fixture with equal counts would be asserting about a storage chosen by
    // accident. Every multi-type test below therefore gives the intended storage strictly fewer
    // entities than the others.

    [KlothoComponent(9500)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GuardAComponent : IComponent { public int Value; }

    [KlothoComponent(9501)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GuardBComponent : IComponent { public int Value; }

    [KlothoComponent(9502)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GuardCComponent : IComponent { public int Value; }

    [KlothoComponent(9503)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GuardDComponent : IComponent { public int Value; }

    [KlothoComponent(9504)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GuardEComponent : IComponent { public int Value; }

    /// <summary>
    /// What <c>FilterMutationGuard</c> reports and — just as important — what it stays quiet about.
    ///
    /// <para>The violation it exists for produces no exception, no hash mismatch and no desync
    /// (dense order is hash input and is serialised raw, so every peer and every re-execution
    /// reproduces the same double visit). These tests are therefore the only place the behaviour is
    /// observable at all, which is why the guard throws instead of asserting.</para>
    ///
    /// <para>The behavioural tests are wrapped in the same <c>#if</c> as the guard's fields, following
    /// <c>ApplyFullStateClearAllTests</c>: <c>Check()</c>'s call sites are removed by the compiler
    /// outside those symbols, so in a Release <c>dotnet test</c> — which is what CI runs — there is
    /// nothing to throw. The size test below deliberately stays outside the gate so that Release
    /// asserts something rather than silently covering nothing.</para>
    /// </summary>
    [TestFixture]
    public class FilterMutationGuardTests
    {
        private const int MaxEntities = 16;

        private Frame _frame;

        [SetUp]
        public void SetUp() => _frame = new Frame(MaxEntities, null);

        private EntityRef WithA(int value)
        {
            var e = _frame.CreateEntity();
            _frame.Add(e, new GuardAComponent { Value = value });
            return e;
        }

        // The guard's message, asserted alongside the type: ComponentStorageFlat.Add throws
        // InvalidOperationException too (duplicate add, capacity), so the type alone would let a test
        // pass for the wrong reason.
        private const string GuardMessage = "Filter iteration";

        // --- Size: the one assertion that runs in every configuration -----------------------------

        [Test]
        public void GuardStruct_CarriesFieldsOnlyInDevBuilds()
        {
            int size = Unsafe.SizeOf<FilterMutationGuard>();
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            // Fields present: watch + prevCount + entityIndex + started.
            Assert.Greater(size, 1,
                "The guard has no fields in a build that defines the dev symbols — the #if gate is wrong.");
#else
            // No fields at all, so the empty-struct minimum. This is what makes the release cost zero:
            // Filter grows by an empty struct and Create() inlines to `default`.
            Assert.AreEqual(1, size,
                "The guard still has fields in a release build — the #if gate is wrong.");
#endif
        }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // (Runtime #if gate) — Check()'s call sites are compiled away without these symbols, so the
        // assertions below would all fail for a reason that has nothing to do with the guard.

        // --- The violation ------------------------------------------------------------------------

        [Test]
        public void RemovingFromAnotherEntity_Throws()
        {
            var e0 = WithA(0);
            var e1 = WithA(1);
            WithA(2);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                {
                    // e1 is not the entity Next() just handed out on the first pass: the tail moves
                    // into its slot ahead of the cursor and gets visited twice.
                    if (entity.Index == e0.Index)
                        _frame.Remove<GuardAComponent>(e1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        [Test]
        public void DestroyingAnotherEntity_Throws()
        {
            var e0 = WithA(0);
            var e1 = WithA(1);
            WithA(2);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == e0.Index)
                        _frame.DestroyEntity(e1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        // --- The safe cases: these are what systems legitimately do -------------------------------

        [Test]
        public void RemovingFromTheCurrentEntity_IsSilent()
        {
            var visited = new List<int>();

            WithA(0); WithA(1); WithA(2);

            Assert.DoesNotThrow(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                {
                    visited.Add(entity.Index);
                    _frame.Remove<GuardAComponent>(entity);
                }
            });

            // Every entity is still visited exactly once: the swap lands in the slot just handed out,
            // and the stale dense tail past the new Count is read back on a later pass.
            Assert.AreEqual(3, visited.Count);
            CollectionAssert.AllItemsAreUnique(visited);
        }

        [Test]
        public void DestroyingTheCurrentEntity_IsSilent()
        {
            var visited = new List<int>();

            WithA(0); WithA(1); WithA(2);

            Assert.DoesNotThrow(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                {
                    visited.Add(entity.Index);
                    _frame.DestroyEntity(entity);
                }
            });

            Assert.AreEqual(3, visited.Count);
            CollectionAssert.AllItemsAreUnique(visited);
        }

        [Test]
        public void RemovingFromAStorageTheFilterIsNotWalking_IsSilent()
        {
            // Pin the walked storage: A gets two entities, B gets three, so A is the smaller one and
            // the filter walks A. Removing B is then a different storage, which is genuinely safe.
            var a0 = WithA(0);
            var a1 = WithA(1);
            _frame.Add(a0, new GuardBComponent { Value = 0 });
            _frame.Add(a1, new GuardBComponent { Value = 1 });
            var bOnly = _frame.CreateEntity();
            _frame.Add(bOnly, new GuardBComponent { Value = 2 });

            Assert.AreEqual(2, _frame.Filter<GuardAComponent>().Count, "fixture: A must be the smaller storage");

            Assert.DoesNotThrow(() =>
            {
                var filter = _frame.Filter<GuardAComponent, GuardBComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == a0.Index)
                        _frame.Remove<GuardBComponent>(bOnly);
                }
            });
        }

        // --- Filter<T1>: the variant with no Has re-check -----------------------------------------

        [Test]
        public void SingleTypeFilter_RemovingFromAnotherEntity_Throws()
        {
            var e0 = WithA(0);
            var e1 = WithA(1);
            WithA(2);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == e0.Index)
                        _frame.Remove<GuardAComponent>(e1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        [Test]
        public void SingleTypeFilter_RemovingFromTheCurrentEntity_IsSilent()
        {
            WithA(0); WithA(1);

            Assert.DoesNotThrow(() =>
            {
                var filter = _frame.Filter<GuardAComponent>();
                while (filter.Next(out var entity))
                    _frame.Remove<GuardAComponent>(entity);
            });
        }

        // --- Representative arities ---------------------------------------------------------------

        [Test]
        public void Arity2_RemovingFromTheWalkedStorage_Throws()
        {
            // A: 3 entities, B: 4 — A is walked.
            var a0 = WithA(0);
            var a1 = WithA(1);
            var a2 = WithA(2);
            foreach (var e in new[] { a0, a1, a2 })
                _frame.Add(e, new GuardBComponent { Value = 0 });
            var bOnly = _frame.CreateEntity();
            _frame.Add(bOnly, new GuardBComponent { Value = 9 });

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var filter = _frame.Filter<GuardAComponent, GuardBComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == a0.Index)
                        _frame.Remove<GuardAComponent>(a1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        [Test]
        public void Arity5_RemovingFromTheWalkedStorage_Throws()
        {
            // A on 3 entities, B..E on those three plus one extra each, so A stays the smallest.
            var a0 = WithA(0);
            var a1 = WithA(1);
            var a2 = WithA(2);
            foreach (var e in new[] { a0, a1, a2 })
            {
                _frame.Add(e, new GuardBComponent { Value = 0 });
                _frame.Add(e, new GuardCComponent { Value = 0 });
                _frame.Add(e, new GuardDComponent { Value = 0 });
                _frame.Add(e, new GuardEComponent { Value = 0 });
            }
            var padding = _frame.CreateEntity();
            _frame.Add(padding, new GuardBComponent { Value = 9 });
            _frame.Add(padding, new GuardCComponent { Value = 9 });
            _frame.Add(padding, new GuardDComponent { Value = 9 });
            _frame.Add(padding, new GuardEComponent { Value = 9 });

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var filter = _frame.Filter<GuardAComponent, GuardBComponent, GuardCComponent,
                                           GuardDComponent, GuardEComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == a0.Index)
                        _frame.Remove<GuardAComponent>(a1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        [Test]
        public void FilterWithout_RemovingFromTheWalkedStorage_Throws()
        {
            var e0 = WithA(0);
            var e1 = WithA(1);
            WithA(2);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                // One required type, so the walked storage is A by construction.
                var filter = _frame.FilterWithout<GuardAComponent, GuardEComponent>();
                while (filter.Next(out var entity))
                {
                    if (entity.Index == e0.Index)
                        _frame.Remove<GuardAComponent>(e1);
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }

        // --- Nested filters over one storage ------------------------------------------------------

        [Test]
        public void NestedFilterOverTheSameStorage_InnerCurrentEntityRemoval_ThrowsInTheOuterWalk()
        {
            // Legal for the inner loop (it removes its own current entity), a violation for the outer
            // one (that entity is not what the OUTER Next() handed out, and its slot is backfilled by
            // the tail). The throw therefore surfaces in the outer walk, not at the removal.
            WithA(0); WithA(1); WithA(2);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var outer = _frame.Filter<GuardAComponent>();
                while (outer.Next(out var o))
                {
                    var inner = _frame.Filter<GuardAComponent>();
                    while (inner.Next(out var i))
                    {
                        if (i.Index == o.Index)
                            continue;

                        _frame.Remove<GuardAComponent>(i);
                        break;
                    }
                }
            });
            Assert.That(ex.Message, Does.Contain(GuardMessage));
        }
#endif
    }
}
