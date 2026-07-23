using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>IKLogger that records emitted messages per level.</summary>
    internal sealed class RecordLogger : IKLogger
    {
        public readonly List<(KLogLevel Level, string Message)> Entries = new List<(KLogLevel, string)>();
        public bool IsEnabled(KLogLevel level) => true;
        public void Log(KLogLevel level, string message, Exception exception) => Entries.Add((level, message));
        public int WarningCount
        {
            get
            {
                int n = 0;
                foreach (var e in Entries) if (e.Level == KLogLevel.Warning) n++;
                return n;
            }
        }
    }

    [TestFixture]
    public class HFSMBuilderTests
    {
        private static readonly ConstDecision True = new ConstDecision(true);

        // ── Valid graphs ──────────────────────────────────────────────────────

        [Test]
        public void ValidFlatGraph_BuildsAndRegisters()
        {
            const int rootId = 9100;
            var root = new HFSMBuilder(rootId)
                .Default(0)
                .State(0).To(1, True, priority: 50)
                .State(1).To(0, True, priority: 50)
                .Build();

            Assert.IsTrue(HFSMRoot.Has(rootId));
            Assert.AreEqual(rootId, root.RootId);
            Assert.AreEqual(0, root.DefaultStateId);
            Assert.AreEqual(2, root.States.Length);
            Assert.AreEqual(0, root.States[0].StateId, "States must be dense: index == StateId");
            Assert.AreEqual(1, root.States[1].StateId);
        }

        [Test]
        public void Transitions_SortedByDescendingPriority()
        {
            var root = new HFSMBuilder(9101)
                .Default(0)
                .State(0)
                    .To(1, True, priority: 10)
                    .To(2, True, priority: 90)
                    .To(3, True, priority: 50)
                .State(1).State(2).State(3)
                .Build();

            var t = root.States[0].Transitions;
            Assert.AreEqual(90, t[0].Priority);
            Assert.AreEqual(50, t[1].Priority);
            Assert.AreEqual(10, t[2].Priority);
        }

        [Test]
        public void Transitions_EqualPriority_KeepsDeclarationOrder_Stable()
        {
            var root = new HFSMBuilder(9102)
                .Default(0)
                .State(0)
                    .To(1, True, priority: 50)
                    .To(2, True, priority: 50)
                .State(1).State(2)
                .Build();

            var t = root.States[0].Transitions;
            Assert.AreEqual(1, t[0].TargetStateId, "equal priority keeps .To() call order");
            Assert.AreEqual(2, t[1].TargetStateId);
        }

        [Test]
        public void MultipleTransitionsToSameTarget_Allowed()
        {
            var root = new HFSMBuilder(9103)
                .Default(0)
                .State(0)
                    .To(1, True, priority: 80)
                    .To(1, True, priority: 50)
                .State(1)
                .Build();

            Assert.AreEqual(2, root.States[0].Transitions.Length);
        }

        // ── Structural failures (throw regardless of logger) ──────────────────

        [Test]
        public void DuplicateStateId_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9104).Default(0).State(0).State(0).Build());
        }

        [Test]
        public void DefaultNotSet_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9105).State(0).Build());
        }

        [Test]
        public void DefaultNotDeclared_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9106).Default(5).State(0).Build());
        }

        [Test]
        public void DanglingTransitionTarget_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9107).Default(0).State(0).To(9, True, priority: 50).Build());
        }

        [Test]
        public void DanglingParent_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9108).Default(0).State(0, parentId: 7).Build());
        }

        [Test]
        public void DanglingDefaultChild_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9109).Default(0).State(0, defaultChildId: 7).Build());
        }

        [Test]
        public void NonDenseStates_Throws()
        {
            // ids 0 and 2 declared, index 1 missing → gap
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9110).Default(0).State(0).State(2).Build());
        }

        [Test]
        public void OnEnterSetTwice_Throws()
        {
            var a = new RecordAction();
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9111).Default(0).State(0).OnEnter(a).OnEnter(a).Build());
        }

        // ── Advisory findings: warn by default, throw under strict ────────────

        [Test]
        public void Unreachable_WarnsByDefault_ThrowsInStrict()
        {
            var logger = new RecordLogger();
            // state 1 is declared but never reached from default 0
            new HFSMBuilder(9112, logger).Default(0).State(0).State(1).Build();
            Assert.AreEqual(1, logger.WarningCount, "unreachable state should warn, not throw");

            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9113).Default(0).State(0).State(1).Build(strict: true));
        }

        [Test]
        public void SelfTransition_WarnsByDefault_ThrowsInStrict()
        {
            var logger = new RecordLogger();
            new HFSMBuilder(9114, logger).Default(0).State(0).To(0, True, priority: 10).Build();
            Assert.AreEqual(1, logger.WarningCount, "self-transition should warn, not throw");

            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9115).Default(0).State(0).To(0, True, priority: 10).Build(strict: true));
        }

        [Test]
        public void DuplicatePriority_WarnsByDefault_ThrowsInStrict()
        {
            var logger = new RecordLogger();
            new HFSMBuilder(9116, logger)
                .Default(0)
                .State(0).To(1, True, priority: 50).To(2, True, priority: 50)
                .State(1).State(2)
                .Build();
            Assert.AreEqual(1, logger.WarningCount, "duplicate priority should warn, not throw");

            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9117)
                    .Default(0)
                    .State(0).To(1, True, priority: 50).To(2, True, priority: 50)
                    .State(1).State(2)
                    .Build(strict: true));
        }

        [Test]
        public void NullLogger_AdvisorySilent_NoThrow()
        {
            // self-transition with no logger: silent, still builds
            var root = new HFSMBuilder(9118).Default(0).State(0).To(0, True, priority: 10).Build();
            Assert.IsNotNull(root);
        }

        // ── Hierarchy structural failures (throw regardless of logger) ─────────

        [Test]
        public void DepthExceedsMaxDepth_Throws()
        {
            // 9-deep default-child chain (states 0..8) > MaxDepth (8). Consistent + anchored, so the depth
            // rule is what fires.
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9119).Default(0)
                    .State(0, defaultChildId: 1)
                    .State(1, parentId: 0, defaultChildId: 2)
                    .State(2, parentId: 1, defaultChildId: 3)
                    .State(3, parentId: 2, defaultChildId: 4)
                    .State(4, parentId: 3, defaultChildId: 5)
                    .State(5, parentId: 4, defaultChildId: 6)
                    .State(6, parentId: 5, defaultChildId: 7)
                    .State(7, parentId: 6, defaultChildId: 8)
                    .State(8, parentId: 7)
                    .Build());
        }

        [Test]
        public void ParentCycle_Throws()
        {
            // states 1↔2 form a ParentId cycle (default 0 is a root so anchoring passes first).
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9120).Default(0)
                    .State(0)
                    .State(1, parentId: 2)
                    .State(2, parentId: 1)
                    .Build());
        }

        [Test]
        public void SelfParent_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9121).Default(0).State(0).State(1, parentId: 1).Build());
        }

        [Test]
        public void SelfDefaultChild_Throws()
        {
            // state 1 declares itself as default child → its ParentId (-1) != 1 (consistency).
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9122).Default(0).State(0).State(1, defaultChildId: 1).Build());
        }

        [Test]
        public void DefaultChildParentInconsistent_Throws()
        {
            // state 0's default child is 1, but 1.ParentId is -1 (not 0).
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9123).Default(0).State(0, defaultChildId: 1).State(1).Build());
        }

        [Test]
        public void DefaultStateNotRoot_Throws()
        {
            // consistent tree (0 composite → child 1), but default points to non-root child 1 (anchoring).
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9124).Default(1).State(0, defaultChildId: 1).State(1, parentId: 0).Build());
        }

        // ── Hierarchy valid cases (no false positives) ────────────────────────

        [Test]
        public void ValidComposite_Builds()
        {
            var root = new HFSMBuilder(9125).Default(0)
                .State(0, defaultChildId: 1)
                .State(1, parentId: 0)
                .Build();
            Assert.IsNotNull(root);
            Assert.AreEqual(1, root.States[0].DefaultChildId);
            Assert.AreEqual(0, root.States[1].ParentId);
        }

        [Test]
        public void NonRootTransitionTarget_Builds()
        {
            // transition from root 0 into non-root child 2 (hierarchical entry) — anchoring must NOT reject this.
            var root = new HFSMBuilder(9126).Default(0)
                .State(0).To(2, True, priority: 50)
                .State(1, defaultChildId: 2)
                .State(2, parentId: 1)
                .Build();
            Assert.IsNotNull(root);
        }

        [Test]
        public void MultiRoot_Builds()
        {
            // several top-level (ParentId == -1) states, Brawler-style.
            var root = new HFSMBuilder(9127).Default(0)
                .State(0).To(1, True, priority: 50)
                .State(1).To(2, True, priority: 50)
                .State(2).To(0, True, priority: 50)
                .Build();
            Assert.IsNotNull(root);
        }

        [Test]
        public void DepthExactlyMaxDepth_Builds()
        {
            // 8-deep chain (states 0..7) == MaxDepth, must pass the boundary.
            var root = new HFSMBuilder(9128).Default(0)
                .State(0, defaultChildId: 1)
                .State(1, parentId: 0, defaultChildId: 2)
                .State(2, parentId: 1, defaultChildId: 3)
                .State(3, parentId: 2, defaultChildId: 4)
                .State(4, parentId: 3, defaultChildId: 5)
                .State(5, parentId: 4, defaultChildId: 6)
                .State(6, parentId: 5, defaultChildId: 7)
                .State(7, parentId: 6)
                .Build();
            Assert.IsNotNull(root);
        }
    }
}
