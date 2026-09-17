using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace xpTURN.Klotho.Unity6000_6.Tests
{
    /// <summary>
    /// DefaultEntityViewPool keys its queues by the prefab GameObject (it used to key by instance ID).
    /// EditMode is enough: the happy path is Instantiate / SetActive / SetParent only — Destroy is
    /// reached solely by the fallback branches (a prefab without EntityView, a view the pool never made),
    /// which these tests avoid. On Unity 6000.6 this is the one automated run of the dictionary through
    /// Object.Equals/GetHashCode on the 64-bit EntityId.
    /// </summary>
    [TestFixture]
    public class DefaultEntityViewPoolTests
    {
        private class ProbeEntityView : EntityView { }

        private readonly List<GameObject> _owned = new();
        private DefaultEntityViewPool _pool;
        private GameObject _p, _q;

        [SetUp]
        public void SetUp()
        {
            _pool = Own(new GameObject("pool")).AddComponent<DefaultEntityViewPool>();
            _p = Own(new GameObject("prefab-p"));
            _p.AddComponent<ProbeEntityView>();
            _q = Own(new GameObject("prefab-q"));
            _q.AddComponent<ProbeEntityView>();
        }

        [TearDown]
        public void TearDown()
        {
            // Rented views are re-parented to the scene root; pooled ones live under the pool object.
            for (int i = _owned.Count - 1; i >= 0; i--)
                if (_owned[i] != null) Object.DestroyImmediate(_owned[i]);
            _owned.Clear();
        }

        private GameObject Own(GameObject go) { _owned.Add(go); return go; }

        private EntityView RentSync(GameObject prefab)
        {
            var task = _pool.Rent(prefab);
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Succeeded), "Rent is synchronous for both hit and miss");
            var view = task.GetAwaiter().GetResult();
            Assert.That(view, Is.Not.Null);
            Own(view.gameObject);
            return view;
        }

        [Test]
        public void Prewarm_ThenRent_HandsOutThePrewarmedInstances_WithoutInstantiating()
        {
            _pool.Prewarm(_p, 2);
            var prewarmed = new HashSet<EntityView>(_pool.GetComponentsInChildren<ProbeEntityView>(includeInactive: true));
            Assert.That(prewarmed.Count, Is.EqualTo(2));

            var a = RentSync(_p);
            var b = RentSync(_p);
            Assert.That(a, Is.Not.SameAs(b));
            Assert.That(prewarmed.Contains(a), Is.True);
            Assert.That(prewarmed.Contains(b), Is.True);
            Assert.That(a.gameObject.activeSelf, Is.True);
            Assert.That(a.transform.parent, Is.Null);
        }

        [Test]
        public void Return_ThenRent_ReusesTheSameInstances_ForTheSamePrefab()
        {
            _pool.Prewarm(_p, 2);
            var a = RentSync(_p);
            var b = RentSync(_p);
            var first = new HashSet<EntityView> { a, b };

            _pool.Return(a);
            _pool.Return(b);
            Assert.That(a.gameObject.activeSelf, Is.False);
            Assert.That(a.transform.parent, Is.SameAs(_pool.transform));

            var c = RentSync(_p);
            var d = RentSync(_p);
            Assert.That(c, Is.Not.SameAs(d));
            Assert.That(first.Contains(c), Is.True);
            Assert.That(first.Contains(d), Is.True);
        }

        [Test]
        public void Rent_KeysByPrefab_SoAnotherPrefabNeverReceivesPooledInstances()
        {
            var a = RentSync(_p);          // miss → instantiated
            _pool.Return(a);

            var fromQ = RentSync(_q);      // different key → its own instantiate
            Assert.That(fromQ, Is.Not.SameAs(a));

            var fromP = RentSync(_p);      // hit → the pooled instance of p
            Assert.That(fromP, Is.SameAs(a));
        }
    }
}
