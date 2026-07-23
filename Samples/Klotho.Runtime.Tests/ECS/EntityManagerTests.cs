using NUnit.Framework;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class EntityManagerTests
    {
        private EntityManager _em;

        [SetUp]
        public void SetUp()
        {
            _em = new EntityManager(16);
        }

        [Test]
        public void Create_ReturnsValidEntity()
        {
            var entity = _em.Create();

            Assert.IsTrue(entity.IsValid);
            Assert.AreEqual(0, entity.Index);
            Assert.AreEqual(1, entity.Version);
        }

        [Test]
        public void Create_IncrementsCount()
        {
            Assert.AreEqual(0, _em.Count);

            _em.Create();
            Assert.AreEqual(1, _em.Count);

            _em.Create();
            Assert.AreEqual(2, _em.Count);
        }

        [Test]
        public void Create_AssignsSequentialIndices()
        {
            var e0 = _em.Create();
            var e1 = _em.Create();
            var e2 = _em.Create();

            Assert.AreEqual(0, e0.Index);
            Assert.AreEqual(1, e1.Index);
            Assert.AreEqual(2, e2.Index);
        }

        [Test]
        public void IsAlive_ReturnsTrueForLiveEntity()
        {
            var entity = _em.Create();

            Assert.IsTrue(_em.IsAlive(entity));
        }

        [Test]
        public void IsAlive_ReturnsFalseForDestroyedEntity()
        {
            var entity = _em.Create();
            _em.Destroy(entity);

            Assert.IsFalse(_em.IsAlive(entity));
        }

        [Test]
        public void IsAlive_ReturnsFalseForInvalidEntity()
        {
            Assert.IsFalse(_em.IsAlive(EntityRef.None));
            Assert.IsFalse(_em.IsAlive(new EntityRef(-1, 0)));
            Assert.IsFalse(_em.IsAlive(new EntityRef(999, 1)));
        }

        [Test]
        public void Destroy_DecrementsCount()
        {
            var e0 = _em.Create();
            _em.Create();
            Assert.AreEqual(2, _em.Count);

            _em.Destroy(e0);
            Assert.AreEqual(1, _em.Count);
        }

        [Test]
        public void Destroy_DoubleDestroy_IsNoop()
        {
            var entity = _em.Create();
            _em.Destroy(entity);
            _em.Destroy(entity);

            Assert.AreEqual(0, _em.Count);
        }

        [Test]
        public void Create_AfterDestroy_ReusesSlotWithIncrementedVersion()
        {
            var e0 = _em.Create();
            int originalIndex = e0.Index;
            _em.Destroy(e0);

            var e1 = _em.Create();

            Assert.AreEqual(originalIndex, e1.Index);
            Assert.AreEqual(e0.Version + 1, e1.Version);
        }

        [Test]
        public void StaleEntityRef_IsNotAlive()
        {
            var e0 = _em.Create();
            _em.Destroy(e0);
            var e1 = _em.Create();

            Assert.IsFalse(_em.IsAlive(e0));
            Assert.IsTrue(_em.IsAlive(e1));
        }

        [Test]
        public void Create_ExceedingCapacity_Throws()
        {
            var em = new EntityManager(2);
            em.Create();
            em.Create();

            Assert.Throws<System.InvalidOperationException>(() => em.Create());
        }

        [Test]
        public void CopyFrom_RestoresState()
        {
            var source = new EntityManager(16);
            var e0 = source.Create();
            var e1 = source.Create();
            source.Destroy(e0);

            var target = new EntityManager(16);
            target.CopyFrom(source);

            Assert.AreEqual(source.Count, target.Count);
            Assert.IsFalse(target.IsAlive(e0));
            Assert.IsTrue(target.IsAlive(e1));
        }

        [Test]
        public void CopyFrom_NewCreateUsesCorrectIndex()
        {
            var source = new EntityManager(16);
            source.Create();
            source.Create();
            source.Create();

            var target = new EntityManager(16);
            target.CopyFrom(source);

            var newEntity = target.Create();
            Assert.AreEqual(3, newEntity.Index);
        }

        [Test]
        public void Clear_ResetsAllState()
        {
            var e0 = _em.Create();
            _em.Create();
            _em.Clear();

            Assert.AreEqual(0, _em.Count);
            Assert.IsFalse(_em.IsAlive(e0));
        }

        [Test]
        public void FreeList_LIFO_Order()
        {
            var e0 = _em.Create();
            var e1 = _em.Create();
            var e2 = _em.Create();

            _em.Destroy(e0);
            _em.Destroy(e2);

            var reused1 = _em.Create();
            var reused2 = _em.Create();

            Assert.AreEqual(e2.Index, reused1.Index);
            Assert.AreEqual(e0.Index, reused2.Index);
        }

        [Test]
        public void IsAlive_ByIndex_WorksCorrectly()
        {
            var e0 = _em.Create();
            _em.Create();

            Assert.IsTrue(_em.IsAlive(0));
            Assert.IsTrue(_em.IsAlive(1));
            Assert.IsFalse(_em.IsAlive(2));

            _em.Destroy(e0);
            Assert.IsFalse(_em.IsAlive(0));
        }

        [Test]
        public void GetVersion_ReturnsCurrentVersion()
        {
            var e0 = _em.Create();
            Assert.AreEqual(e0.Version, _em.GetVersion(0));

            _em.Destroy(e0);
            var e1 = _em.Create();
            Assert.AreEqual(e1.Version, _em.GetVersion(0));
            Assert.AreEqual(e0.Version + 1, _em.GetVersion(0));
        }

        [Test]
        public void CopyFrom_CapacityMismatch_Throws()
        {
            var other = new EntityManager(8);
            Assert.Throws<System.InvalidOperationException>(() => _em.CopyFrom(other));
        }

        [Test]
        public void Clear_ThenCreate_StartsFromScratch()
        {
            _em.Create();
            _em.Create();
            _em.Clear();

            var e = _em.Create();
            Assert.AreEqual(0, e.Index);
            Assert.AreEqual(1, e.Version);
            Assert.AreEqual(1, _em.Count);
        }

        [Test]
        public void MultipleCreateDestroyCycles_VersionIncrementsCorrectly()
        {
            var e0 = _em.Create();
            Assert.AreEqual(1, e0.Version);

            _em.Destroy(e0);
            var e1 = _em.Create();
            Assert.AreEqual(2, e1.Version);

            _em.Destroy(e1);
            var e2 = _em.Create();
            Assert.AreEqual(3, e2.Version);

            Assert.AreEqual(e0.Index, e2.Index);
            Assert.IsFalse(_em.IsAlive(e0));
            Assert.IsFalse(_em.IsAlive(e1));
            Assert.IsTrue(_em.IsAlive(e2));
        }

        // --- EntityRef struct tests ---

        [Test]
        public void EntityRef_Equality()
        {
            var a = new EntityRef(1, 2);
            var b = new EntityRef(1, 2);
            var c = new EntityRef(1, 3);
            var d = new EntityRef(2, 2);

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a.Equals(b));
            Assert.IsTrue(a.Equals((object)b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());

            Assert.IsFalse(a == c);
            Assert.IsFalse(a == d);
            Assert.IsTrue(a != c);
        }

        [Test]
        public void EntityRef_None_IsNotValid()
        {
            Assert.IsFalse(EntityRef.None.IsValid);
            Assert.AreEqual(-1, EntityRef.None.Index);
            Assert.AreEqual(0, EntityRef.None.Version);
        }

        [Test]
        public void EntityRef_ToString_ContainsIndexAndVersion()
        {
            var e = new EntityRef(3, 7);
            var str = e.ToString();
            Assert.IsTrue(str.Contains("3"));
            Assert.IsTrue(str.Contains("7"));
        }
    }
}
