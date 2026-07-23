using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // ── Test helpers: decisions/actions/sources carrying AIParam fields ──────────

    /// <summary>Decision with one AIParam field — wired or left default per ctor.</summary>
    internal sealed class ParamDecision : HFSMDecision
    {
        private readonly AIParam<int> _value;
        public ParamDecision(AIParam<int> value) => _value = value;
        public ParamDecision() { }   // leaves _value = default(AIParam<int>) → unassigned
        public override bool Decide(ref AIContext context) => _value.Resolve(ref context) > 0;
    }

    /// <summary>Action with one AIParam field — wired or left default per ctor.</summary>
    internal sealed class ParamAction : AIAction
    {
        private readonly AIParam<FP64> _value;
        public ParamAction(AIParam<FP64> value) => _value = value;
        public ParamAction() { }     // leaves _value = default → unassigned
        public override void Execute(ref AIContext context) => _value.Resolve(ref context);
    }

    /// <summary>AIFunction holding a sub-AIParam (chaining) — wired or left default per ctor.</summary>
    internal sealed class PassThroughSource : AIFunction<int>
    {
        private readonly AIParam<int> _inner;
        public PassThroughSource(AIParam<int> inner) => _inner = inner;
        public PassThroughSource() { } // leaves _inner = default → unassigned sub-param
        public override int Resolve(ref AIContext context) => _inner.Resolve(ref context);
    }

    /// <summary>Decision holding a raw AIFunction field (not wrapped in an AIParam) — exercises the
    /// directly-held-source recursion branch of the validator.</summary>
    internal sealed class RawSourceDecision : HFSMDecision
    {
        private readonly PassThroughSource _src;
        public RawSourceDecision(PassThroughSource src) => _src = src;
        public override bool Decide(ref AIContext context) => _src.Resolve(ref context) > 0;
    }

    [TestFixture]
    public class AIParamValidationTests
    {
        private static readonly ConstDecision True = new ConstDecision(true);

        [SetUp]
        public void SetUp() => HFSMRoot.Clear();

        [TearDown]
        public void TearDown() => HFSMRoot.Clear();

        // ── Throws on unassigned AIParam ────────────────────────────────────────

        [Test]
        public void UnassignedAIParam_InDecision_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9300)
                    .Default(0)
                    .State(0).To(1, new ParamDecision(), priority: 50)   // _value never wired
                    .State(1)
                    .Build());
        }

        [Test]
        public void UnassignedAIParam_InAction_Throws()
        {
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9301)
                    .Default(0)
                    .State(0).OnEnter(new ParamAction())                 // _value never wired
                    .Build());
        }

        [Test]
        public void UnassignedSubParam_InAIFunctionSource_Throws()
        {
            // Outer param IS wired (From source), but the source's own sub-AIParam is not → recursion must catch it.
            var decision = new ParamDecision(AIParam.From(new PassThroughSource()));
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9302)
                    .Default(0)
                    .State(0).To(1, decision, priority: 50)
                    .State(1)
                    .Build());
        }

        [Test]
        public void UnassignedSubParam_InRawAIFunctionField_Throws()
        {
            // Source held directly as a field (not via AIParam.From) — validator must still recurse into it.
            var decision = new RawSourceDecision(new PassThroughSource());   // sub-param unwired
            Assert.Throws<HFSMValidationException>(() =>
                new HFSMBuilder(9306)
                    .Default(0)
                    .State(0).To(1, decision, priority: 50)
                    .State(1)
                    .Build());
        }

        // ── Does NOT throw when every AIParam is wired ──────────────────────────

        [Test]
        public void AssignedAIParam_Const_Builds()
        {
            Assert.DoesNotThrow(() =>
                new HFSMBuilder(9303)
                    .Default(0)
                    .State(0).To(1, new ParamDecision(AIParam.Const(1)), priority: 50)
                    .State(1)
                    .Build());
        }

        [Test]
        public void AssignedAIParam_FromChain_Builds()
        {
            var decision = new ParamDecision(AIParam.From(new PassThroughSource(AIParam.Const(5))));
            Assert.DoesNotThrow(() =>
                new HFSMBuilder(9304)
                    .Default(0)
                    .State(0).To(1, decision, priority: 50)
                    .State(1)
                    .Build());
        }

        [Test]
        public void WiredRawAIFunctionField_Builds()
        {
            var decision = new RawSourceDecision(new PassThroughSource(AIParam.Const(3)));
            Assert.DoesNotThrow(() =>
                new HFSMBuilder(9307)
                    .Default(0)
                    .State(0).To(1, decision, priority: 50)
                    .State(1)
                    .Build());
        }

        [Test]
        public void NoParamFields_Builds()
        {
            // Baseline: decisions without any AIParam fields must be unaffected by the new pass.
            Assert.DoesNotThrow(() =>
                new HFSMBuilder(9305)
                    .Default(0)
                    .State(0).To(1, True, priority: 50)
                    .State(1)
                    .Build());
        }
    }
}
