using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>AIAction that records whether the context carried a non-null Logger service.</summary>
    internal sealed class ContextProbeAction : AIAction
    {
        public bool HadLogger;
        public int CallCount;
        public override void Execute(ref AIContext context)
        {
            HadLogger = context.Logger != null;
            CallCount++;
        }
    }

    /// <summary>
    /// Work item B — MED-1 (Init context injection) + MINOR-b (Deinit: OnExit chain + component removal).
    /// </summary>
    [TestFixture]
    public class HFSMLifecycleTests
    {
        private const int MaxEntities = 16;

        private IKLogger _logger;
        private Frame _frame;
        private EntityRef _entity;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = loggerFactory.CreateLogger("HFSMLifecycleTests");
        }

        [SetUp]
        public void SetUp()
        {
            HFSMRoot.Clear();
            _frame  = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();
        }

        [TearDown]
        public void TearDown() => HFSMRoot.Clear();

        // ── MED-1: Init context injection ─────────────────────────────────────

        [Test]
        public void Init_WithContext_PassesServicesToOnEnter()
        {
            var probe = new ContextProbeAction();
            new HFSMBuilder(9601).Default(0).State(0).OnEnter(probe).Build();

            var context = new AIContext { Frame = _frame, Entity = _entity, Logger = _logger };
            HFSMManager.Init(ref _frame, _entity, 9601, ref context);

            Assert.IsTrue(probe.HadLogger, "OnEnter at init should see the supplied context's services");
        }

        [Test]
        public void Init_NoContext_OnEnterGetsNullServices()
        {
            var probe = new ContextProbeAction();
            new HFSMBuilder(9602).Default(0).State(0).OnEnter(probe).Build();

            HFSMManager.Init(ref _frame, _entity, 9602);

            Assert.IsFalse(probe.HadLogger, "parameterless Init runs OnEnter with a service-less context");
        }

        // ── MINOR-b: Deinit ───────────────────────────────────────────────────

        [Test]
        public void Deinit_RunsOnExitLeafToRoot_ThenRemovesComponent()
        {
            var order = new List<string>();
            new HFSMBuilder(9603)
                .Default(0)
                .State(0, defaultChildId: 1).OnExit(new OrderRecordAction(order, "parent"))
                .State(1, parentId: 0).OnExit(new OrderRecordAction(order, "child"))
                .Build();
            HFSMManager.Init(ref _frame, _entity, 9603);

            HFSMManager.Deinit(ref _frame, _entity);

            Assert.AreEqual(new[] { "child", "parent" }, order.ToArray(), "OnExit runs leaf→root");
            Assert.IsFalse(_frame.Has<HFSMComponent>(_entity), "component removed after Deinit");
        }

        [Test]
        public void Deinit_NoComponent_IsNoOp()
        {
            Assert.DoesNotThrow(() => HFSMManager.Deinit(ref _frame, _entity));
        }

        [Test]
        public void EnterExit_AreReverseSymmetric()
        {
            var order = new List<string>();
            new HFSMBuilder(9604)
                .Default(0)
                .State(0, defaultChildId: 1)
                    .OnEnter(new OrderRecordAction(order, "enter-parent"))
                    .OnExit(new OrderRecordAction(order, "exit-parent"))
                .State(1, parentId: 0)
                    .OnEnter(new OrderRecordAction(order, "enter-child"))
                    .OnExit(new OrderRecordAction(order, "exit-child"))
                .Build();

            HFSMManager.Init(ref _frame, _entity, 9604);   // enter root→leaf
            HFSMManager.Deinit(ref _frame, _entity);       // exit leaf→root

            Assert.AreEqual(
                new[] { "enter-parent", "enter-child", "exit-child", "exit-parent" },
                order.ToArray());
        }

        // ── Q3 mechanic: Deinit → re-Init re-entry (ResetBotState pattern) ─────

        [Test]
        public void DeinitThenInit_ReentersAtDefault()
        {
            new HFSMBuilder(9605)
                .Default(0)
                .State(0).To(1, new ConstDecision(true), priority: 10)
                .State(1)
                .Build();

            HFSMManager.Init(ref _frame, _entity, 9605);
            HFSMManager.Update(ref _frame, _entity);       // 0 → 1
            Assert.AreEqual(1, HFSMManager.GetLeafStateId(ref _frame, _entity));

            HFSMManager.Deinit(ref _frame, _entity);
            HFSMManager.Init(ref _frame, _entity, 9605);   // re-enter at default 0
            Assert.AreEqual(0, HFSMManager.GetLeafStateId(ref _frame, _entity));
        }
    }
}
