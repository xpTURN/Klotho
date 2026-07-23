using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // ───────────────────────────────────────────────
    // Extra helpers (ConstDecision / RecordAction live in HFSMManagerP2Tests.cs)
    // ───────────────────────────────────────────────

    /// <summary>HFSMDecision returning a fixed value and counting Decide() calls.</summary>
    internal sealed class CountingDecision : HFSMDecision
    {
        private readonly bool _value;
        public int CallCount;
        public CountingDecision(bool value) => _value = value;
        public override bool Decide(ref AIContext context) { CallCount++; return _value; }
    }

    /// <summary>AIAction that appends a tag to a shared list — for asserting execution order.</summary>
    internal sealed class OrderRecordAction : AIAction
    {
        private readonly List<string> _log;
        private readonly string _tag;
        public OrderRecordAction(List<string> log, string tag) { _log = log; _tag = tag; }
        public override void Execute(ref AIContext context) => _log.Add(_tag);
    }

    [TestFixture]
    public class HFSMTransitionInheritanceTests
    {
        private const int MaxEntities = 16;
        private static readonly ConstDecision True = new ConstDecision(true);

        private IKLogger _logger;
        private Frame _frame;
        private EntityRef _entity;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = loggerFactory.CreateLogger("HFSMInheritanceTests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();
        }

        private int Leaf() => HFSMManager.GetLeafStateId(ref _frame, _entity);

        private int[] Chain()
        {
            System.Span<int> buf = stackalloc int[HFSMComponent.MaxDepth];
            int depth = HFSMManager.GetActiveStateIds(ref _frame, _entity, buf);
            var arr = new int[depth];
            for (int i = 0; i < depth; i++) arr[i] = buf[i];
            return arr;
        }

        // ── (A) inheritance behavior ──────────────────────────────────────────

        [Test]
        public void ParentTransition_FiresFromChildLeaf()
        {
            // Combat(0, dc→Attack) ─To→ Evade(2); Attack(1) leaf has no transition.
            new HFSMBuilder(9301)
                .Default(0)
                .State(0, defaultChildId: 1).To(2, True, priority: 50)
                .State(1, parentId: 0)
                .State(2)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9301);
            Assert.AreEqual(1, Leaf(), "starts at Attack (Combat's default child)");

            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(2, Leaf(), "parent Combat's transition fired from leaf Attack");
        }

        [Test]
        public void ChildTransition_OverridesParent()
        {
            // Attack(1) ─To→ Evade(2); Combat(0) ─To→ Flee(3). Child must win.
            new HFSMBuilder(9302)
                .Default(0)
                .State(0, defaultChildId: 1).To(3, True, priority: 50)
                .State(1, parentId: 0).To(2, True, priority: 50)
                .State(2)
                .State(3)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9302);
            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(2, Leaf(), "child (Attack) transition preempts parent (Combat)");
        }

        [Test]
        public void ChildWins_DespiteLowerPriority()
        {
            // Child prio 10, parent prio 100 — hierarchy beats priority value.
            new HFSMBuilder(9303)
                .Default(0)
                .State(0, defaultChildId: 1).To(3, True, priority: 100)
                .State(1, parentId: 0).To(2, True, priority: 10)
                .State(2)
                .State(3)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9303);
            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(2, Leaf(), "child level evaluated first regardless of parent's higher priority");
        }

        [Test]
        public void Grandparent_FallbackFires()
        {
            // Root(0)→Mid(1)→Leaf(2); only Root has a firing transition → Target(3).
            new HFSMBuilder(9304)
                .Default(0)
                .State(0, defaultChildId: 1).To(3, True, priority: 50)
                .State(1, parentId: 0, defaultChildId: 2)
                .State(2, parentId: 1)
                .State(3)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9304);
            Assert.AreEqual(2, Leaf());

            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(3, Leaf(), "grandparent transition fires after leaf+mid miss");
        }

        [Test]
        public void EventGatedParentTransition()
        {
            const int EvtHit = 7;
            // Combat(0) ─[EvtHit]→ Stagger(2); Attack(1) leaf no transition.
            new HFSMBuilder(9305)
                .Default(0)
                .State(0, defaultChildId: 1).To(2, null, priority: 50, eventId: EvtHit)
                .State(1, parentId: 0)
                .State(2)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9305);

            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(1, Leaf(), "no event → parent event-gated transition does not fire");

            HFSMManager.TriggerEvent(ref _frame, _entity, EvtHit);
            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(2, Leaf(), "pending event → parent transition fires from child leaf");
        }

        [Test]
        public void MidAncestorFire_ExitsDescendants_KeepsAncestor()
        {
            // Root(0)→A(1)→B(2)→L(3); A also has child B2(4); A ─To→ B2.
            var aExit = new RecordAction();
            var bExit = new RecordAction();
            var lExit = new RecordAction();
            new HFSMBuilder(9306)
                .Default(0)
                .State(0, defaultChildId: 1)
                .State(1, parentId: 0, defaultChildId: 2).OnExit(aExit).To(4, True, priority: 50)
                .State(2, parentId: 1, defaultChildId: 3).OnExit(bExit)
                .State(3, parentId: 2).OnExit(lExit)
                .State(4, parentId: 1)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9306);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, Chain());

            HFSMManager.Update(ref _frame, _entity);
            CollectionAssert.AreEqual(new[] { 0, 1, 4 }, Chain(), "exit A's subtree (B,L), keep Root+A, enter B2");
            Assert.AreEqual(1, lExit.CallCount, "L exited");
            Assert.AreEqual(1, bExit.CallCount, "B exited");
            Assert.AreEqual(0, aExit.CallCount, "A (the firing ancestor) is NOT exited");
        }

        [Test]
        public void ParentSelfTransition_ResetsSubtree()
        {
            // Root(0)→A(1, dc→L)→L(2); A ─To→ A (itself). Should reset A's subtree, not no-op.
            var aExit = new RecordAction();
            var lExit = new RecordAction();
            var lEnter = new RecordAction();
            new HFSMBuilder(9307)
                .Default(0)
                .State(0, defaultChildId: 1)
                .State(1, parentId: 0, defaultChildId: 2).OnExit(aExit).To(1, True, priority: 50)
                .State(2, parentId: 1).OnEnter(lEnter).OnExit(lExit)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9307);
            Assert.AreEqual(1, lEnter.CallCount, "L entered on Init");

            HFSMManager.Update(ref _frame, _entity);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, Chain(), "chain restored to A's default child");
            Assert.AreEqual(0, aExit.CallCount, "A (target) is not exited");
            Assert.AreEqual(1, lExit.CallCount, "L exited (not a no-op)");
            Assert.AreEqual(2, lEnter.CallCount, "L re-entered as A's default child");
        }

        // ── (B) additive / regression ─────────────────────────────────────────

        [Test]
        public void LeafFires_ParentNotEvaluated()
        {
            // Leaf fires → parent's decision must NOT be evaluated (additive / short-circuit).
            var parentDecision = new CountingDecision(true);
            new HFSMBuilder(9308)
                .Default(0)
                .State(0, defaultChildId: 1).To(3, parentDecision, priority: 50)
                .State(1, parentId: 0).To(2, True, priority: 50)
                .State(2)
                .State(3)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9308);
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(2, Leaf(), "leaf transition fired");
            Assert.AreEqual(0, parentDecision.CallCount, "parent decision not evaluated when leaf fires");
        }

        [Test]
        public void OnUpdate_RunsChainRootToLeaf()
        {
            var order = new List<string>();
            // Combat(0, OnUpdate "combat") → Attack(1, OnUpdate "attack"), no transitions.
            new HFSMBuilder(9309)
                .Default(0)
                .State(0, defaultChildId: 1).OnUpdate(new OrderRecordAction(order, "combat"))
                .State(1, parentId: 0).OnUpdate(new OrderRecordAction(order, "attack"))
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9309);
            HFSMManager.Update(ref _frame, _entity);

            CollectionAssert.AreEqual(new[] { "combat", "attack" }, order, "OnUpdate runs root→leaf (parent first)");
        }

        [Test]
        public void FlatGraph_LeafOnlyBehavior_Unchanged()
        {
            // No hierarchy: behaves exactly like leaf-only.
            new HFSMBuilder(9310)
                .Default(0)
                .State(0).To(1, True, priority: 50)
                .State(1).To(0, new ConstDecision(false), priority: 50)
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9310);
            Assert.AreEqual(0, Leaf());

            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(1, Leaf(), "flat leaf transition fires as before");

            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(1, Leaf(), "no parents to fall back to; false decision keeps state");
        }
    }
}
