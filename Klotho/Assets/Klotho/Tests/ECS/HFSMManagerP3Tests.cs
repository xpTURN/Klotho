using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // ───────────────────────────────────────────────
    // P3 hierarchical FSM state IDs
    // ───────────────────────────────────────────────
    //
    //  Root (rootLevel, ParentId=-1)  ← not RootId, States array index
    //  ├── CombatState (default, ParentId=RootLevel)
    //  │   ├── Idle (default, ParentId=CombatState)
    //  │   └── Chase (ParentId=CombatState)
    //  └── DeathState (ParentId=RootLevel)
    //
    // States array: [RootLevel, CombatState, Idle, Chase, DeathState]

    internal static class P3StateId
    {
        public const int RootLevel   = 0;
        public const int CombatState = 1;
        public const int Idle        = 2;
        public const int Chase       = 3;
        public const int DeathState  = 4;
    }

    [TestFixture]
    public class HFSMManagerP3Tests
    {
        private const int RootId      = 9002;
        private const int MaxEntities = 16;

        private ILogger   _logger;
        private Frame     _frame;
        private EntityRef _entity;

        // Action instances
        private RecordAction _combatEnter, _combatUpdate, _combatExit;
        private RecordAction _idleEnter,   _idleUpdate,   _idleExit;
        private RecordAction _chaseEnter,  _chaseUpdate,  _chaseExit;
        private RecordAction _deathEnter,  _deathUpdate,  _deathExit;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("HFSMTestsP3");
        }

        [SetUp]
        public void SetUp()
        {
            _frame  = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();

            _combatEnter = new RecordAction(); _combatUpdate = new RecordAction(); _combatExit = new RecordAction();
            _idleEnter   = new RecordAction(); _idleUpdate   = new RecordAction(); _idleExit   = new RecordAction();
            _chaseEnter  = new RecordAction(); _chaseUpdate  = new RecordAction(); _chaseExit  = new RecordAction();
            _deathEnter  = new RecordAction(); _deathUpdate  = new RecordAction(); _deathExit  = new RecordAction();
        }

        // ─────────────────────────────────────────
        // Root builder
        // ─────────────────────────────────────────

        /// <summary>
        /// idleTransitions  : list of Idle → X transitions
        /// chaseTransitions : list of Chase → X transitions
        /// </summary>
        private HFSMRoot BuildHierarchyRoot(
            HFSMTransitionNode[] idleTransitions  = null,
            HFSMTransitionNode[] chaseTransitions = null)
        {
            var root = new HFSMRoot
            {
                RootId         = RootId,
                DefaultStateId = P3StateId.RootLevel,
                States         = new HFSMStateNode[5],
            };

            // RootLevel: top-level — DefaultChild = CombatState
            root.States[P3StateId.RootLevel] = new HFSMStateNode
            {
                StateId        = P3StateId.RootLevel,
                ParentId       = -1,
                DefaultChildId = P3StateId.CombatState,
                Transitions    = Array.Empty<HFSMTransitionNode>(),
            };

            // CombatState: DefaultChild = Idle
            root.States[P3StateId.CombatState] = new HFSMStateNode
            {
                StateId         = P3StateId.CombatState,
                ParentId        = P3StateId.RootLevel,
                DefaultChildId  = P3StateId.Idle,
                OnEnterActions  = new AIAction[] { _combatEnter },
                OnUpdateActions = new AIAction[] { _combatUpdate },
                OnExitActions   = new AIAction[] { _combatExit },
                Transitions     = Array.Empty<HFSMTransitionNode>(),
            };

            // Idle: leaf
            root.States[P3StateId.Idle] = new HFSMStateNode
            {
                StateId         = P3StateId.Idle,
                ParentId        = P3StateId.CombatState,
                DefaultChildId  = -1,
                OnEnterActions  = new AIAction[] { _idleEnter },
                OnUpdateActions = new AIAction[] { _idleUpdate },
                OnExitActions   = new AIAction[] { _idleExit },
                Transitions     = idleTransitions ?? Array.Empty<HFSMTransitionNode>(),
            };

            // Chase: leaf
            root.States[P3StateId.Chase] = new HFSMStateNode
            {
                StateId         = P3StateId.Chase,
                ParentId        = P3StateId.CombatState,
                DefaultChildId  = -1,
                OnEnterActions  = new AIAction[] { _chaseEnter },
                OnUpdateActions = new AIAction[] { _chaseUpdate },
                OnExitActions   = new AIAction[] { _chaseExit },
                Transitions     = chaseTransitions ?? Array.Empty<HFSMTransitionNode>(),
            };

            // DeathState: leaf, different branch
            root.States[P3StateId.DeathState] = new HFSMStateNode
            {
                StateId         = P3StateId.DeathState,
                ParentId        = P3StateId.RootLevel,
                DefaultChildId  = -1,
                OnEnterActions  = new AIAction[] { _deathEnter },
                OnUpdateActions = new AIAction[] { _deathUpdate },
                OnExitActions   = new AIAction[] { _deathExit },
                Transitions     = Array.Empty<HFSMTransitionNode>(),
            };

            HFSMRoot.Register(root);
            return root;
        }

        // ─────────────────────────────────────────
        // Scenario 1: Transition within same parent (Idle→Chase)
        //   LCA = CombatState → CombatState OnExit/OnEnter must NOT run
        //   Only Idle.OnExit + Chase.OnEnter run
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario1_SameParent_Transition_Idle_To_Chase()
        {
            BuildHierarchyRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = P3StateId.Chase,
                        Decision      = new ConstDecision(true),
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);
            // After Init: RootLevel.OnEnter(none) → CombatState.OnEnter → Idle.OnEnter
            Assert.AreEqual(1, _combatEnter.CallCount, "CombatState OnEnter on Init");
            Assert.AreEqual(1, _idleEnter.CallCount,   "Idle OnEnter on Init");

            HFSMManager.Update(ref _frame, _entity);

            // LCA = CombatState → Combat's OnExit/OnEnter must not run
            Assert.AreEqual(0, _combatExit.CallCount,  "CombatState OnExit must NOT run (LCA)");
            Assert.AreEqual(1, _combatEnter.CallCount, "CombatState OnEnter must NOT run again");

            Assert.AreEqual(1, _idleExit.CallCount,   "Idle OnExit must run");
            Assert.AreEqual(1, _chaseEnter.CallCount, "Chase OnEnter must run");
            Assert.AreEqual(0, _chaseExit.CallCount);

            // verify ActiveState
            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(P3StateId.RootLevel,   fsm.ActiveStateIds[0], "depth0 = RootLevel");
            Assert.AreEqual(P3StateId.CombatState, fsm.ActiveStateIds[1], "depth1 = CombatState");
            Assert.AreEqual(P3StateId.Chase,        fsm.ActiveStateIds[2], "depth2 = Chase");
            Assert.AreEqual(3, fsm.ActiveDepth);
        }

        // ─────────────────────────────────────────
        // Scenario 2: Transition across branches (Idle→DeathState)
        //   LCA = RootLevel
        //   OnExit: Idle → CombatState
        //   OnEnter: DeathState
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario2_DifferentBranch_Transition_Idle_To_Death()
        {
            BuildHierarchyRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = P3StateId.DeathState,
                        Decision      = new ConstDecision(true),
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);
            HFSMManager.Update(ref _frame, _entity);

            // LCA = RootLevel → Root has no OnExit/OnEnter (OnExit/OnEnterActions null)
            Assert.AreEqual(1, _idleExit.CallCount,   "Idle OnExit must run");
            Assert.AreEqual(1, _combatExit.CallCount, "CombatState OnExit must run (different branch)");

            Assert.AreEqual(0, _combatEnter.CallCount - 1, "CombatState OnEnter must NOT run again (was 1 at Init)");
            // Precisely: 1 call on Init, no additional calls after Update
            Assert.AreEqual(1, _combatEnter.CallCount);

            Assert.AreEqual(1, _deathEnter.CallCount, "DeathState OnEnter must run");
            Assert.AreEqual(0, _deathExit.CallCount);

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(P3StateId.RootLevel,  fsm.ActiveStateIds[0], "depth0 = RootLevel");
            Assert.AreEqual(P3StateId.DeathState, fsm.ActiveStateIds[1], "depth1 = DeathState");
            Assert.AreEqual(2, fsm.ActiveDepth);
        }

        // ─────────────────────────────────────────
        // Scenario 3: Init auto-enters the DefaultChild chain
        //   RootLevel → CombatState(DefaultChild) → Idle(DefaultChild)
        //   ActiveDepth = 3
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario3_Init_DefaultChild_Chain()
        {
            BuildHierarchyRoot();

            HFSMManager.Init(ref _frame, _entity, RootId);

            // OnEnter chain: RootLevel(none) → CombatState → Idle
            Assert.AreEqual(1, _combatEnter.CallCount, "CombatState OnEnter on Init");
            Assert.AreEqual(1, _idleEnter.CallCount,   "Idle OnEnter on Init");
            Assert.AreEqual(0, _chaseEnter.CallCount);
            Assert.AreEqual(0, _deathEnter.CallCount);

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(3, fsm.ActiveDepth, "ActiveDepth = 3 (RootLevel/CombatState/Idle)");
            Assert.AreEqual(P3StateId.RootLevel,   fsm.ActiveStateIds[0]);
            Assert.AreEqual(P3StateId.CombatState, fsm.ActiveStateIds[1]);
            Assert.AreEqual(P3StateId.Idle,        fsm.ActiveStateIds[2]);
        }

        // ─────────────────────────────────────────
        // Scenario 4: Self-transition (leaf within hierarchy)
        //   LCA = Idle itself → OnExit → OnEnter
        //   CombatState OnExit/OnEnter must NOT run
        // ─────────────────────────────────────────

        [Test]
        public unsafe void Scenario4_SelfTransition_InHierarchy()
        {
            BuildHierarchyRoot(
                idleTransitions: new[]
                {
                    new HFSMTransitionNode
                    {
                        Priority      = 10,
                        TargetStateId = P3StateId.Idle,
                        Decision      = new ConstDecision(true),
                    },
                });

            HFSMManager.Init(ref _frame, _entity, RootId);
            HFSMManager.Update(ref _frame, _entity);

            Assert.AreEqual(1, _idleExit.CallCount,  "Idle OnExit must run on self-transition");
            Assert.AreEqual(2, _idleEnter.CallCount, "Idle OnEnter: 1 from Init + 1 from self-transition");

            Assert.AreEqual(0, _combatExit.CallCount,  "CombatState OnExit must NOT run");
            Assert.AreEqual(1, _combatEnter.CallCount, "CombatState OnEnter must NOT run again");

            ref var fsm = ref _frame.Get<HFSMComponent>(_entity);
            Assert.AreEqual(0, fsm.StateElapsedTicks, "StateElapsedTicks reset after self-transition");
            Assert.AreEqual(3, fsm.ActiveDepth);
            Assert.AreEqual(P3StateId.Idle, fsm.ActiveStateIds[2]);
        }
    }
}
