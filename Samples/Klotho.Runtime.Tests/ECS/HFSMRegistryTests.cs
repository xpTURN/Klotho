using System;
using NUnit.Framework;

using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// MED-6 — HFSMRoot static registry: Register / Has / Get / Clear behavior and the
    /// idempotent-throw duplicate-registration policy (Q1=(b)).
    /// </summary>
    [TestFixture]
    public class HFSMRegistryTests
    {
        private const int RootId = 9501;

        private static HFSMRoot MakeRoot(int rootId) => new HFSMRoot
        {
            RootId         = rootId,
            DefaultStateId = 0,
            States         = new HFSMStateNode[1],
        };

        [SetUp]
        public void SetUp() => HFSMRoot.Clear();

        [TearDown]
        public void TearDown() => HFSMRoot.Clear();

        [Test]
        public void Register_ThenHasAndGet()
        {
            var root = MakeRoot(RootId);
            HFSMRoot.Register(root);

            Assert.IsTrue(HFSMRoot.Has(RootId));
            Assert.AreSame(root, HFSMRoot.Get(RootId));
        }

        [Test]
        public void Clear_RemovesEntries()
        {
            HFSMRoot.Register(MakeRoot(RootId));
            HFSMRoot.Clear();

            Assert.IsFalse(HFSMRoot.Has(RootId));
            Assert.Throws<ArgumentException>(() => HFSMRoot.Get(RootId));
        }

        [Test]
        public void Get_Unknown_Throws()
        {
            Assert.Throws<ArgumentException>(() => HFSMRoot.Get(RootId));
        }

        [Test]
        public void Register_SameInstanceTwice_IsNoOp()
        {
            var root = MakeRoot(RootId);
            HFSMRoot.Register(root);

            Assert.DoesNotThrow(() => HFSMRoot.Register(root));
            Assert.AreSame(root, HFSMRoot.Get(RootId));
        }

        [Test]
        public void Register_DifferentInstanceSameId_Throws()
        {
            HFSMRoot.Register(MakeRoot(RootId));

            Assert.Throws<InvalidOperationException>(() => HFSMRoot.Register(MakeRoot(RootId)));
        }

        [Test]
        public void BuildIsolated_AndExplicitRootLifecycle_DoNotPolluteGlobalRegistry()
        {
            var rootA = new HFSMBuilder(RootId)
                .Default(0)
                .State(0).To(1, new AlwaysTrueDecision(), priority: 1)
                .State(1)
                .BuildIsolated(strict: true);

            var rootB = new HFSMBuilder(RootId)
                .Default(0)
                .State(0)
                .BuildIsolated(strict: true);

            Assert.IsFalse(HFSMRoot.Has(RootId));
            Assert.AreNotSame(rootA, rootB);

            var frame = new Frame(16, null, null);
            var entityA = frame.CreateEntity();
            var entityB = frame.CreateEntity();

            HFSMManager.Init(ref frame, entityA, rootA);
            HFSMManager.Init(ref frame, entityB, rootB);
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, entityA));
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, entityB));

            HFSMManager.Update(ref frame, entityA, rootA);
            HFSMManager.Update(ref frame, entityB, rootB);
            Assert.AreEqual(1, HFSMManager.GetLeafStateId(ref frame, entityA));
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, entityB));

            HFSMManager.Deinit(ref frame, entityA, rootA);
            HFSMManager.Deinit(ref frame, entityB, rootB);
            Assert.AreEqual(-1, HFSMManager.GetLeafStateId(ref frame, entityA));
            Assert.IsFalse(HFSMRoot.Has(RootId));
        }

        private sealed class AlwaysTrueDecision : HFSMDecision
        {
            public override bool Decide(ref AIContext context) => true;
        }
    }
}
