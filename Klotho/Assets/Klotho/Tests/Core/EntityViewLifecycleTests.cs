using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// Validates the lifecycle of <see cref="EntityView"/>.
    ///   - EnsureInitialized invokes OnInitialize only on the first call to prevent double initialization on pool reuse.
    ///   - InternalActivate resets ErrorVisualState and then invokes OnActivate on every call.
    ///   - OnDeactivate can be overridden by the user.
    /// </summary>
    [TestFixture]
    public class EntityViewLifecycleTests
    {
        private GameObject _go;
        private TestEntityView _view;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestEntityView");
            _view = _go.AddComponent<TestEntityView>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void EnsureInitialized_FirstCall_InvokesOnInitialize()
        {
            Assert.AreEqual(0, _view.InitializeCount);
            _view.EnsureInitialized();
            Assert.AreEqual(1, _view.InitializeCount);
        }

        [Test]
        public void EnsureInitialized_SecondCall_IsIdempotent()
        {
            _view.EnsureInitialized();
            _view.EnsureInitialized();
            _view.EnsureInitialized();
            Assert.AreEqual(1, _view.InitializeCount,
                "OnInitialize must be called only once per instance to support pool reuse");
        }

        [Test]
        public void InternalActivate_CallsOnActivateEveryTime()
        {
            var frame = FrameRef.None(FrameKind.Predicted);
            _view.InternalActivate(frame);
            _view.InternalActivate(frame);
            Assert.AreEqual(2, _view.ActivateCount,
                "OnActivate must be called on every pool re-Rent (cumulative calls, unlike EnsureInitialized)");
        }

        [Test]
        public void OnDeactivate_UserOverrideInvoked()
        {
            _view.OnDeactivate();
            Assert.AreEqual(1, _view.DeactivateCount);
        }

        [Test]
        public void PropertySetters_AllowExternalInjection()
        {
            // Common to the EVU / Registry path — externally injected EntityRef / Engine on spawn.
            var entity = new EntityRef(5, 1);
            _view.EntityRef = entity;
            Assert.AreEqual(5, _view.EntityRef.Index);
            Assert.AreEqual(1, _view.EntityRef.Version);
            Assert.IsTrue(_view.EntityRef.IsValid);
            Assert.IsNull(_view.Engine);
        }

        // ── Test-only concrete EntityView subclass ──
        private class TestEntityView : EntityView
        {
            public int InitializeCount;
            public int ActivateCount;
            public int DeactivateCount;

            public override void OnInitialize()
            {
                // Skip base call to avoid null access on _components (the test GameObject has no children).
                InitializeCount++;
            }

            public override void OnActivate(FrameRef frame)
            {
                ActivateCount++;
            }

            public override void OnDeactivate()
            {
                DeactivateCount++;
            }
        }
    }
}
