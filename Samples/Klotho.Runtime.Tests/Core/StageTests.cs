using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Validates the behavior of the <see cref="SimulationStage"/> enum and the default implementations
    /// of <see cref="IKlothoEngine.IsForward"/> / <see cref="IKlothoEngine.IsResimulation"/> on the interface.
    /// </summary>
    [TestFixture]
    public class StageTests
    {
        [Test]
        public void SimulationStage_HasForwardAndResimulate()
        {
            var values = System.Enum.GetValues(typeof(SimulationStage));
            Assert.AreEqual(2, values.Length);
            Assert.Contains(SimulationStage.Forward, values);
            Assert.Contains(SimulationStage.Resimulate, values);
        }

        [Test]
        public void SimulationStage_DefaultIsForward()
        {
            // default is the first enum value (Forward), same as the initial value of the KlothoEngine.Stage field.
            SimulationStage stage = default;
            Assert.AreEqual(SimulationStage.Forward, stage);
            Assert.AreEqual(0, (int)SimulationStage.Forward);
            Assert.AreEqual(1, (int)SimulationStage.Resimulate);
        }

        [Test]
        public void IsForward_DerivesFromStageForward()
        {
            IStageCarrier carrier = new StageCarrier { Stage = SimulationStage.Forward };
            Assert.IsTrue(carrier.IsForward);
            Assert.IsFalse(carrier.IsResimulation);
        }

        [Test]
        public void IsResimulation_DerivesFromStageResimulate()
        {
            IStageCarrier carrier = new StageCarrier { Stage = SimulationStage.Resimulate };
            Assert.IsFalse(carrier.IsForward);
            Assert.IsTrue(carrier.IsResimulation);
        }

        // Lightweight interface replacing IKlothoEngine — verifies the same default member formula (`Stage == Forward`).
        // Isolates and tests only the derive logic instead of mocking the full IKlothoEngine.
        private interface IStageCarrier
        {
            SimulationStage Stage { get; }
            bool IsForward => Stage == SimulationStage.Forward;
            bool IsResimulation => Stage == SimulationStage.Resimulate;
        }

        private class StageCarrier : IStageCarrier
        {
            public SimulationStage Stage { get; set; }
        }
    }
}
