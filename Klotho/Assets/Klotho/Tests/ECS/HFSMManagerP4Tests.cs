using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class HFSMManagerP4Tests
    {
        private const int RootId      = 9004;
        private const int MaxEntities = 16;

        // Event ID constants
        private const int EventHit   = 1;
        private const int EventStun  = 2;

        private ILogger   _logger;
        private Frame     _frame;
        private EntityRef _entity;

        private RecordAction _idleEnter,  _idleUpdate,  _idleExit;
        private RecordAction _chaseEnter, _chaseUpdate, _chaseExit;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("HFSMTestsP4");
        }

        [SetUp]
        public void SetUp()
        {
            _frame  = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();

            _idleEnter  = new RecordAction(); _idleUpdate  = new RecordAction(); _idleExit  = new RecordAction();
            _chaseEnter = new RecordAction(); _chaseUpdate = new RecordAction(); _chaseExit = new RecordAction();
        }

        // ─────────────────────────────────────────
        // Root builder
        // ─────────────────────────────────────────

        private HFSMRoot BuildRoot(
            HFSMTransitionNode[] idleTransitions  = null,
            HFSMTransitionNode[] chaseTransitions = null)
        {
            var root = new HFSMRoot
            {
                RootId         = RootId,
                DefaultStateId = StateId.Idle,
                States         = new HFSMStateNode[3],
            };

            root.States[StateId.Idle] = new HFSMStateNode
            {
                StateId         = StateId.Idle,
                ParentId        = -1,
                DefaultChildId  = -1,
                OnEnterActions  = new AIAction[] { _idleEnter },
                OnUpdateActions = new AIAction[] { _idleUpdate },
                OnExitActions   = new AIAction[] { _idleExit },
                Transitions     = idleTransitions ?? Array.Empty<HFSMTransitionNode>(),
            };

            root.States[StateId.Chase] = new HFSMStateNode
            {
                StateId         = StateId.Chase,
                ParentId        = -1,
                DefaultChildId  = -1,
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
        // Scenario 1: Inject event → transition on next tick
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario1_TriggerEvent_TransitionOnNextUpdate()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = StateId.Chase,
                        EventId       = EventHit,
                        Decision      = new ConstDecision(true),
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);

            // Update without event → no transition
            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(StateId.Idle, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0],
                "Without event, transition should not occur");

            // Inject event
            bool result = HFSMManager.TriggerEvent(ref _frame, _entity, EventHit);
            Assert.IsTrue(result, "TriggerEvent should return true when queue has space");

            // Transition occurs on next Update
            HFSMManager.Update(ref _frame, _entity);
            Assert.AreEqual(StateId.Chase, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0],
                "Event-triggered transition should occur on next Update");
            Assert.AreEqual(1, _idleExit.CallCount);
            Assert.AreEqual(1, _chaseEnter.CallCount);
        }

        // ─────────────────────────────────────────
        // Scenario 2: EventId transition skipped without event
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario2_NoEvent_EventTransitionSkipped()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = StateId.Chase,
                        EventId       = EventHit,        // event required
                        Decision      = new ConstDecision(true),
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);

            // Update multiple ticks without injecting an event
            HFSMManager.Update(ref _frame, _entity);
            HFSMManager.Update(ref _frame, _entity);
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(StateId.Idle, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0],
                "Without matching event, transition must not occur");
            Assert.AreEqual(0, _idleExit.CallCount);
            Assert.AreEqual(0, _chaseEnter.CallCount);
        }

        // ─────────────────────────────────────────
        // Scenario 3: Queue overflow drop
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario3_EventQueueOverflow_DropsAndReturnsFalse()
        {
            BuildRoot();
            HFSMManager.Init(ref _frame, _entity, RootId);

            // Up to 4 succeed
            Assert.IsTrue(HFSMManager.TriggerEvent(ref _frame, _entity, 1));
            Assert.IsTrue(HFSMManager.TriggerEvent(ref _frame, _entity, 2));
            Assert.IsTrue(HFSMManager.TriggerEvent(ref _frame, _entity, 3));
            Assert.IsTrue(HFSMManager.TriggerEvent(ref _frame, _entity, 4));

            // 5th is dropped
            Assert.IsFalse(HFSMManager.TriggerEvent(ref _frame, _entity, 5),
                "5th TriggerEvent should return false (queue full)");

            // Verify the existing 4 are retained
            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(4, fsm.PendingEventCount, "PendingEventCount should remain 4");
            Assert.AreEqual(1, fsm.PendingEventIds[0]);
            Assert.AreEqual(2, fsm.PendingEventIds[1]);
            Assert.AreEqual(3, fsm.PendingEventIds[2]);
            Assert.AreEqual(4, fsm.PendingEventIds[3]);
        }

        // ─────────────────────────────────────────
        // Scenario 4: Cleared at end of tick
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario4_PendingEvents_ClearedAfterUpdate()
        {
            BuildRoot();
            HFSMManager.Init(ref _frame, _entity, RootId);

            // Inject events
            HFSMManager.TriggerEvent(ref _frame, _entity, EventHit);
            HFSMManager.TriggerEvent(ref _frame, _entity, EventStun);

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(2, fsm.PendingEventCount, "Events should be queued before Update");

            // Run Update → events cleared even if no transition occurred
            HFSMManager.Update(ref _frame, _entity);

            fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(0, fsm.PendingEventCount,
                "PendingEventCount should be 0 after Update regardless of transition");
        }

        // ─────────────────────────────────────────
        // Scenario 5: Event + Decision combination
        //   EventId matches but Decision false → no transition
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario5_EventMatches_ButDecisionFalse_NoTransition()
        {
            BuildRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = StateId.Chase,
                        EventId       = EventHit,
                        Decision      = new ConstDecision(false),  // event matches but Decision rejects
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);

            HFSMManager.TriggerEvent(ref _frame, _entity, EventHit);
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(StateId.Idle, _frame.Get<HFSMComponent>(_entity).ActiveStateIds[0],
                "Event matches but Decision false → no transition");
            Assert.AreEqual(0, _idleExit.CallCount);
            Assert.AreEqual(0, _chaseEnter.CallCount);
        }
    }
}
