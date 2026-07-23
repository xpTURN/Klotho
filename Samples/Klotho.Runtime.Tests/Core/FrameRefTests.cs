using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Validates the basic behavior of the FrameRef struct.
    /// </summary>
    [TestFixture]
    public class FrameRefTests
    {
        [Test]
        public void Constructor_StoresTickAndKind()
        {
            var fr = new FrameRef(42, null, FrameKind.Verified);
            Assert.AreEqual(42, fr.Tick);
            Assert.IsNull(fr.Frame);
            Assert.AreEqual(FrameKind.Verified, fr.Kind);
        }

        [Test]
        public void None_ReturnsInvalidTickAndNullFrame()
        {
            var fr = FrameRef.None(FrameKind.Predicted);
            Assert.AreEqual(-1, fr.Tick);
            Assert.IsNull(fr.Frame);
            Assert.AreEqual(FrameKind.Predicted, fr.Kind);
        }

        [Test]
        public void None_PreservesRequestedKind()
        {
            foreach (FrameKind kind in System.Enum.GetValues(typeof(FrameKind)))
            {
                var fr = FrameRef.None(kind);
                Assert.AreEqual(kind, fr.Kind, $"Kind mismatch for {kind}");
                Assert.AreEqual(-1, fr.Tick);
                Assert.IsNull(fr.Frame);
            }
        }

        [Test]
        public void FrameKind_HasFourExpectedValues()
        {
            // 4 Frame Reference kinds. When a new Kind is added, this test must be updated together.
            var kinds = System.Enum.GetValues(typeof(FrameKind));
            Assert.AreEqual(4, kinds.Length);
            Assert.Contains(FrameKind.Verified, kinds);
            Assert.Contains(FrameKind.Predicted, kinds);
            Assert.Contains(FrameKind.PredictedPrevious, kinds);
            Assert.Contains(FrameKind.PreviousUpdatePredicted, kinds);
        }
    }
}
