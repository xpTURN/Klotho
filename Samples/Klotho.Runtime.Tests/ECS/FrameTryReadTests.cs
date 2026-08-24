using System.Runtime.InteropServices;
using NUnit.Framework;

namespace xpTURN.Klotho.ECS.Tests
{
    // typeId 9240 block — 9200-9202 (maxCount), 9210-9214 (reservation pruning), 9220 (stale ref)
    // and 9230-9231 (registry fingerprint) are taken.
    [KlothoComponent(9240)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct TryReadProbeComponent : IComponent
    {
        public int Value;
        public int Other;
    }

    /// <summary>
    /// What <see cref="Frame.TryRead{T}"/> promises beyond being shorter than <c>Has</c> +
    /// <c>GetReadOnly</c>.
    ///
    /// <para>The contract is the predicate, not the value: a test that only compares the component
    /// when one is present would pass for an implementation whose <c>true</c>/<c>false</c> disagreed
    /// with <c>Has</c> somewhere in the domain, and that disagreement is silent — no exception, no
    /// desync, just a branch not taken. Hence the sweep in the last test rather than a single
    /// present/absent pair.</para>
    ///
    /// <para>Two neighbouring behaviours are pinned elsewhere on purpose, next to the same facts
    /// about <c>Has</c> they derive from: the stale-handle trap and the garbage-index bounds check in
    /// <c>StaleEntityRefGuardTests</c>, and the pruned-type fail-fast in
    /// <c>ReservationPruningTests</c>.</para>
    /// </summary>
    [TestFixture]
    public class FrameTryReadTests
    {
        private const int MaxEntities = 16;

        private Frame _frame;

        [SetUp]
        public void SetUp() => _frame = new Frame(MaxEntities, null);

        [Test]
        public void Carried_ReturnsTrueAndTheComponent()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new TryReadProbeComponent { Value = 7, Other = 11 });

            Assert.IsTrue(_frame.TryRead<TryReadProbeComponent>(entity, out var probe));
            Assert.AreEqual(7, probe.Value);
            Assert.AreEqual(11, probe.Other, "every field copies out, not just the first");
        }

        [Test]
        public void NotCarried_ReturnsFalseAndDefault()
        {
            var entity = _frame.CreateEntity();

            Assert.IsFalse(_frame.TryRead<TryReadProbeComponent>(entity, out var probe));
            // Guaranteed, not incidental: T is unmanaged, so unlike the reference-typed TryGet APIs
            // on Frame there is no null to dereference and the out value is documented as default.
            Assert.AreEqual(default(TryReadProbeComponent), probe);
        }

        [Test]
        public void Value_MatchesGetReadOnly()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new TryReadProbeComponent { Value = 42, Other = -1 });

            ref readonly var viaRef = ref _frame.GetReadOnly<TryReadProbeComponent>(entity);
            Assert.IsTrue(_frame.TryRead<TryReadProbeComponent>(entity, out var viaCopy));
            Assert.AreEqual(viaRef.Value, viaCopy.Value);
            Assert.AreEqual(viaRef.Other, viaCopy.Other);
        }

        [Test]
        public void Predicate_AgreesWithHas_AcrossTheDomain()
        {
            var carrying = _frame.CreateEntity();
            _frame.Add(carrying, new TryReadProbeComponent { Value = 1 });

            var bare = _frame.CreateEntity();               // alive, carries nothing

            var removed = _frame.CreateEntity();            // carried, then removed
            _frame.Add(removed, new TryReadProbeComponent { Value = 2 });
            _frame.Remove<TryReadProbeComponent>(removed);

            var stale = _frame.CreateEntity();              // destroyed; slot may be reused later
            _frame.Add(stale, new TryReadProbeComponent { Value = 3 });
            _frame.DestroyEntity(stale);

            var candidates = new[]
            {
                carrying, bare, removed, stale,
                new EntityRef(MaxEntities, 1),              // index out of range
                new EntityRef(-1, 1),                       // negative index
                default,                                    // never issued
            };

            foreach (var entity in candidates)
            {
                bool has = _frame.Has<TryReadProbeComponent>(entity);
                bool tried = _frame.TryRead<TryReadProbeComponent>(entity, out _);
                Assert.AreEqual(has, tried,
                    $"TryRead must answer exactly what Has answers (index {entity.Index}, " +
                    $"version {entity.Version})");
            }
        }
    }
}
