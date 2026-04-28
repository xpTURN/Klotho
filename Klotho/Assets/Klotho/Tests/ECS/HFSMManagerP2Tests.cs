using System;
using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // ───────────────────────────────────────────────
    // Test helper types
    // ───────────────────────────────────────────────

    /// <summary>AIAction that records call count</summary>
    internal class RecordAction : AIAction
    {
        public int CallCount;
        public override void Execute(ref AIContext context) => CallCount++;
    }

    /// <summary>HFSMDecision that always returns the specified value</summary>
    internal class ConstDecision : HFSMDecision
    {
        private readonly bool _value;
        public ConstDecision(bool value) => _value = value;
        public override bool Decide(ref AIContext context) => _value;
    }

    // ───────────────────────────────────────────────
    // FSM state ID constants (flat FSM, no hierarchy)
    // ───────────────────────────────────────────────

    internal static class StateId
    {
        public const int Idle   = 0;
        public const int Chase  = 1;
        public const int Attack = 2;
    }

    [TestFixture]
    public class HFSMManagerP2Tests
    {
        private const int RootId     = 9001;
        private const int MaxEntities = 16;

        private ILogger _logger;
        private Frame   _frame;
        private EntityRef _entity;

        // Action instances — reused or swapped per test
        private RecordAction _idleEnter, _idleUpdate, _idleExit;
        private RecordAction _chaseEnter, _chaseUpdate, _chaseExit;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("HFSMTests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame  = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();

            _idleEnter   = new RecordAction();
            _idleUpdate  = new RecordAction();
            _idleExit    = new RecordAction();
            _chaseEnter  = new RecordAction();
            _chaseUpdate = new RecordAction();
            _chaseExit   = new RecordAction();
        }

        // ─────────────────────────────────────────
        // Root builder helper
        // ─────────────────────────────────────────

        /// <summary>
        /// Idle(default) ↔ Chase two-state flat FSM.
        /// Flexibly configured via idleTransitions / chaseTransitions arguments.
        /// </summary>
        private HFSMRoot BuildRoot(
            HFSMTransitionNode[] idleTransitions   = null,
            HFSMTransitionNode[] chaseTransitions  = null)
        {
            var root = new HFSMRoot
            {
                RootId        = RootId,
                DefaultStateId = StateId.Idle,
                States        = new HFSMStateNode[3],
            };

            root.States[StateId.Idle] = new HFSMStateNode
            {
                StateId        = StateId.Idle,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _idleEnter },
                OnUpdateActions = new AIAction[] { _idleUpdate },
                OnExitActions   = new AIAction[] { _idleExit },
                Transitions     = idleTransitions ?? Array.Empty<HFSMTransitionNode>(),
            };

            root.States[StateId.Chase] = new HFSMStateNode
            {
                StateId        = StateId.Chase,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _chaseEnter },
                OnUpdateActions = new AIAction[] { _chaseUpdate },
                OnExitActions   = new AIAction[] { _chaseExit },
                Transitions     = chaseTransitions ?? Array.Empty<HFSMTransitionNode>(),
            };

            root.States[StateId.Attack] = new HFSMStateNode
            {
                StateId        = StateId.Attack,
                ParentId       = -1,
                DefaultChildId = -1,
                Transitions    = Array.Empty<HFSMTransitionNode>(),
            };

            HFSMRoot.Register(root);
            return root;
        }

        // ─────────────────────────────────────────
        // Scenario 1: Init → enter default state
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario1_Init_EntersDefaultState()
        {
            BuildRoot();
            HFSMManager.Init(ref _frame, _entity, RootId);

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);

            Assert.AreEqual(StateId.Idle, fsm.ActiveStateIds[0], "ActiveStateIds[0] should be DefaultStateId");
            Assert.AreEqual(1, fsm.ActiveDepth,                  "ActiveDepth should be 1 for leaf-only state");
            Assert.AreEqual(0, fsm.StateElapsedTicks,            "StateElapsedTicks should be 0 after Init");
            Assert.AreEqual(1, _idleEnter.CallCount,             "OnEnterActions should run exactly once");
        }

        // ─────────────────────────────────────────
        // Scenario 2: Update → transition occurs
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario2_Update_Transition_Occurs()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode { Priority = 10, TargetStateId = StateId.Chase, Decision = new ConstDecision(true) },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);
            Assert.AreEqual(1, _idleEnter.CallCount);

            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(1, _idleExit.CallCount,   "OnExit of old state should run once");
            Assert.AreEqual(1, _chaseEnter.CallCount, "OnEnter of new state should run once");
            Assert.AreEqual(StateId.Chase, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0]);
        }

        // ─────────────────────────────────────────
        // Scenario 3: Update → no transition
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario3_Update_NoTransition_TickIncreases()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode { Priority = 10, TargetStateId = StateId.Chase, Decision = new ConstDecision(false) },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);

            HFSMManager.Update(ref _frame, _entity);
            HFSMManager.Update(ref _frame, _entity);

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);

            Assert.AreEqual(StateId.Idle, fsm.ActiveStateIds[0], "State should remain Idle");
            Assert.AreEqual(2, fsm.StateElapsedTicks,            "StateElapsedTicks should increment each tick");
            Assert.AreEqual(0, _idleExit.CallCount,              "OnExit should not run without transition");
            Assert.AreEqual(0, _chaseEnter.CallCount);
        }

        // ─────────────────────────────────────────
        // Scenario 4: One transition per tick (Priority first)
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario4_Update_OnlyFirstPriorityTransitionFires()
        {
            var attackEnter = new RecordAction();
            var root = BuildRoot(
                idleTransitions: new[]
                {
                    // Higher Priority is evaluated first
                    new HFSMTransitionNode { Priority = 20, TargetStateId = StateId.Chase,  Decision = new ConstDecision(true) },
                    new HFSMTransitionNode { Priority = 10, TargetStateId = StateId.Attack, Decision = new ConstDecision(true) },
                });

            root.States[StateId.Attack].OnEnterActions = new AIAction[] { attackEnter };

            HFSMManager.Init(ref _frame, _entity, RootId);
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(StateId.Chase, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0],
                "Only the highest-priority transition should fire");
            Assert.AreEqual(1, _chaseEnter.CallCount);
            Assert.AreEqual(0, attackEnter.CallCount, "Lower-priority transition must not be evaluated");
        }

        // ─────────────────────────────────────────
        // Scenario 5: Self-transition
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario5_SelfTransition_ExitThenEnter()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode { Priority = 10, TargetStateId = StateId.Idle, Decision = new ConstDecision(true) },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);
            // After Init StateElapsedTicks = 0, first Update increments tick++ then self-transition → reset
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(1, _idleExit.CallCount,  "OnExit should run on self-transition");
            Assert.AreEqual(2, _idleEnter.CallCount, "OnEnter should run again on self-transition (1 from Init + 1 from transition)");
            Assert.AreEqual(0, _frame.Get<HFSMComponent>(_entity).StateElapsedTicks,
                "StateElapsedTicks should reset to 0 after self-transition");
        }

        // ─────────────────────────────────────────
        // Scenario 6: API misuse — unregistered rootId
        // ─────────────────────────────────────────

        [Test]
        public void Scenario6_Init_UnknownRootId_ThrowsArgumentException()
        {
            // rootId 99999 is not registered
            Assert.Throws<ArgumentException>(() =>
                HFSMManager.Init(ref _frame, _entity, 99999));
        }

        // ─────────────────────────────────────────
        // Scenario 7: API misuse — duplicate Init
        // ─────────────────────────────────────────

        [Test]
        public void Scenario7_Init_Twice_ThrowsInvalidOperationException()
        {
            BuildRoot();
            HFSMManager.Init(ref _frame, _entity, RootId);

            Assert.Throws<InvalidOperationException>(() =>
                HFSMManager.Init(ref _frame, _entity, RootId));
        }

        // ─────────────────────────────────────────
        // Scenario 8: API misuse — Update without HFSMComponent attached
        // ─────────────────────────────────────────

        [Test]
        public void Scenario8_Update_WithoutComponent_ThrowsInvalidOperationException()
        {
            // Call Update without Init
            Assert.Throws<InvalidOperationException>(() =>
                HFSMManager.Update(ref _frame, _entity));
        }
    }
}
