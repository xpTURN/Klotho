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
        public void BuildIsolated_RootsRunSideBySide_WithoutTheRegistry()
        {
            var rootA = new HFSMBuilder(RootId)
                .Default(0)
                .State(0).To(1, new ConstDecision(true), priority: 1)
                .State(1)
                .BuildIsolated(strict: true);
            var rootB = new HFSMBuilder(RootId)
                .Default(0)
                .State(0)
                .BuildIsolated(strict: true);

            Assert.IsFalse(HFSMRoot.Has(RootId));
            Assert.AreNotSame(rootA, rootB);

            var frame = new Frame(16, null);
            var a = frame.CreateEntity();
            var b = frame.CreateEntity();
            HFSMManager.Init(ref frame, a, rootA);
            HFSMManager.Init(ref frame, b, rootB);
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, a));
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, b));

            HFSMManager.Update(ref frame, a, rootA);
            HFSMManager.Update(ref frame, b, rootB);
            Assert.AreEqual(1, HFSMManager.GetLeafStateId(ref frame, a));
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, b));

            HFSMManager.Deinit(ref frame, a, rootA);
            HFSMManager.Deinit(ref frame, b, rootB);
            Assert.AreEqual(-1, HFSMManager.GetLeafStateId(ref frame, a));
            Assert.AreEqual(-1, HFSMManager.GetLeafStateId(ref frame, b));
            Assert.IsFalse(HFSMRoot.Has(RootId));
        }

        [Test]
        public void ExplicitRoot_OfAnotherId_IsRejected()
        {
            var rootA = new HFSMBuilder(RootId).Default(0).State(0).BuildIsolated();
            var rootB = new HFSMBuilder(RootId + 1).Default(0).State(0).BuildIsolated();

            var frame = new Frame(16, null);
            var e = frame.CreateEntity();
            HFSMManager.Init(ref frame, e, rootA);

            Assert.Throws<InvalidOperationException>(() => HFSMManager.Update(ref frame, e, rootB));
            Assert.Throws<InvalidOperationException>(() => HFSMManager.Deinit(ref frame, e, rootB));
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref frame, e), "the rejected calls left the FSM as it was");

            Assert.DoesNotThrow(() => HFSMManager.Update(ref frame, e, rootA));
            HFSMManager.Deinit(ref frame, e, rootA);
            Assert.AreEqual(-1, HFSMManager.GetLeafStateId(ref frame, e));
        }
    }
}
