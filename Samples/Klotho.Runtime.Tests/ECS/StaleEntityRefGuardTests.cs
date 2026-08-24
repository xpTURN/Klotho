using System.Runtime.InteropServices;
using NUnit.Framework;

namespace xpTURN.Klotho.ECS.Tests
{
    // typeId 9220 block — 9200-9202 (maxCount) and 9210-9214 (reservation pruning) are taken.
    [KlothoComponent(9220)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct StaleRefProbeComponent : IComponent
    {
        public int Value;
    }

    /// <summary>
    /// What an <see cref="EntityRef"/> decoded from an untrusted id does and does not prove.
    ///
    /// <para><see cref="EntityRef.ToId"/> packs index AND version precisely so that a reference to
    /// a destroyed entity can be told apart from whatever later took its slot. Reading the version
    /// back is a deliberate act though — <see cref="Frame.Has{T}"/> forwards <c>entity.Index</c>
    /// alone — and a command handler that gates on <c>Has</c> is therefore not checking the thing
    /// the id was designed to carry.</para>
    ///
    /// <para>This is not hypothetical. The Brawler sample's building-removal handler gated on
    /// <c>Has</c> with a comment claiming a stale id "resolves to not found and is rejected, never
    /// removes the wrong building". On a reused slot every step after the guard then worked on the
    /// new occupant: <see cref="EntityRef"/> equality DOES compare version, so the collect-for-
    /// rebake did not skip it, and <see cref="Frame.DestroyEntity"/> strips components by index
    /// unconditionally, so the live building lost its components while its hole stayed carved in
    /// the navmesh. Every peer did it identically, so no desync check could report it.</para>
    ///
    /// <para>These tests pin the two behaviours the fix leans on, so that the reason the handler
    /// calls <c>Entities.IsAlive</c> before <c>Has</c> survives someone reading the guard later and
    /// finding it redundant. If <c>Has</c> is ever made version-aware, the first test here fails —
    /// that is the signal to revisit the guard, not to delete the assert.</para>
    /// </summary>
    [TestFixture]
    public class StaleEntityRefGuardTests
    {
        private const int MaxEntities = 16;

        private Frame _frame;
        private EntityRef _stale;
        private EntityRef _live;

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(MaxEntities, null);

            _stale = _frame.CreateEntity();
            _frame.Add(_stale, new StaleRefProbeComponent { Value = 1 });
            _frame.DestroyEntity(_stale);

            _live = _frame.CreateEntity();
            _frame.Add(_live, new StaleRefProbeComponent { Value = 2 });

            // The whole fixture is about one slot being handed to two entities in turn; if the
            // allocator ever stops reusing it the tests below would pass for the wrong reason.
            Assert.AreEqual(_stale.Index, _live.Index, "fixture needs the freed slot reused");
            Assert.AreNotEqual(_stale.Version, _live.Version, "fixture needs the version to move");
        }

        [Test]
        public void Has_IgnoresVersion_SoAStaleRefAnswersAboutTheNewOccupant()
        {
            // The trap: true, even though the entity this ref names is gone.
            Assert.IsTrue(_frame.Has<StaleRefProbeComponent>(_stale),
                "Frame.Has forwards entity.Index alone — it cannot reject a stale reference. " +
                "If this now fails, Has has become version-aware; see the fixture remarks.");
        }

        [Test]
        public void TryRead_IgnoresVersionToo_SoItHandsBackTheNewOccupantsComponent()
        {
            // Frame.TryRead folds Has + GetReadOnly, so it inherits the trap above verbatim — and
            // makes it easier to walk into: the folded call has no gap between two lines for an
            // IsAlive guard to sit in, so the ordering has to be remembered rather than seen. The
            // XML docs on TryRead say so; this pins the behaviour those docs describe.
            Assert.IsTrue(_frame.TryRead<StaleRefProbeComponent>(_stale, out var viaStale),
                "TryRead forwards entity.Index alone, exactly as Has does. " +
                "If this now fails, the lookup has become version-aware; see the fixture remarks.");

            ref readonly var live = ref _frame.GetReadOnly<StaleRefProbeComponent>(_live);
            Assert.AreEqual(live.Value, viaStale.Value,
                "and the value it hands back belongs to the new occupant, not the named entity");
        }

        [Test]
        public void IsAlive_ReadsVersion_SoItRejectsTheStaleRef()
        {
            Assert.IsFalse(_frame.Entities.IsAlive(_stale), "stale ref must not read as alive");
            Assert.IsTrue(_frame.Entities.IsAlive(_live), "the current occupant must");
        }

        [Test]
        public void EntityRefEquality_ComparesVersion_SoASkipListWillNotMatchAStaleRef()
        {
            // Why gating on Has alone was worse than merely permissive: the caller passes the same
            // ref on to "skip this one" logic, which does compare version and therefore does NOT
            // skip the entity the guard just admitted. The two disagree about what the ref means.
            Assert.AreNotEqual(_stale, _live);
            Assert.IsFalse(_stale == _live);
        }

        [Test]
        public void DestroyEntity_StripsComponentsByIndex_SoAStaleRefDeletesTheNewOccupant()
        {
            _frame.DestroyEntity(_stale);

            // The damage. Entities.Destroy inside DestroyEntity is version-checked and no-ops, so
            // the slot stays alive — but RemoveAllComponents already ran on the raw index.
            Assert.IsFalse(_frame.Has<StaleRefProbeComponent>(_live),
                "the live entity lost its component to a stale reference");
            Assert.IsTrue(_frame.Entities.IsAlive(_live),
                "and it is still alive — a componentless survivor, not a clean removal");
        }

        [Test]
        public void IsAliveThenHas_IsTheGuardThatHolds()
        {
            // The shape the handler uses. Ordering matters only for clarity; both are cheap.
            Assert.IsFalse(_frame.Entities.IsAlive(_stale) && _frame.Has<StaleRefProbeComponent>(_stale));
            Assert.IsTrue(_frame.Entities.IsAlive(_live) && _frame.Has<StaleRefProbeComponent>(_live));
        }

        [Test]
        public void GarbageId_IsAlreadyRejected_ByTheStorageBoundsCheck()
        {
            // Bounding the claim: an arbitrary long is NOT the hole. EntityRef.FromId decodes it
            // without validation, but ComponentStorageFlat.Has bounds-checks the index and
            // verifies the dense/sparse round trip, so only an in-range LIVE slot gets through.
            foreach (long id in new[] { long.MaxValue, long.MinValue, -1L, 0x7FFFFFFF00000001L })
            {
                EntityRef garbage = EntityRef.FromId(id);
                Assert.IsFalse(_frame.Has<StaleRefProbeComponent>(garbage), $"id {id}");
                Assert.IsFalse(_frame.Entities.IsAlive(garbage), $"id {id}");
                // TryRead inherits the same bounds check, and this is the one place it is strictly
                // safer than the pair it replaces: bare GetReadOnly indexes ComponentsSpan with
                // SparseSpan[index] and has no bounds check of its own.
                Assert.IsFalse(_frame.TryRead<StaleRefProbeComponent>(garbage, out var probe),
                    $"id {id}");
                Assert.AreEqual(default(StaleRefProbeComponent), probe, $"id {id}");
            }
        }
    }
}
