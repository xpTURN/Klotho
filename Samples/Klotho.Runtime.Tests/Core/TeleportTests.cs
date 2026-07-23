using NUnit.Framework;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Validates read/write behavior of the <see cref="TransformComponent.TeleportTick"/> field at the unit level.
    /// Engine-side teleport detection logic (pre/post-rollback TeleportTick comparison in ComputeErrorDeltas) is covered in integration tests.
    /// </summary>
    [TestFixture]
    public class TeleportTests
    {
        [Test]
        public void TransformComponent_DefaultTeleportTickIsZero()
        {
            var t = new TransformComponent();
            Assert.AreEqual(0, t.TeleportTick);
        }

        [Test]
        public void TransformComponent_TeleportTickReadWrite()
        {
            var t = new TransformComponent { TeleportTick = 42 };
            Assert.AreEqual(42, t.TeleportTick);

            t.TeleportTick = 100;
            Assert.AreEqual(100, t.TeleportTick);
        }

        [Test]
        public void TransformComponent_TeleportTickIndependentOfOtherFields()
        {
            // TeleportTick is independent of other fields (Position/Rotation/PreviousPosition/Scale).
            var t = new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3)),
                PreviousPosition = new FPVector3(FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6)),
                Rotation = FP64.FromInt(1),
                PreviousRotation = FP64.FromInt(2),
                TeleportTick = 999,
            };

            Assert.AreEqual(999, t.TeleportTick);
            Assert.AreEqual(FP64.FromInt(1), t.Position.x);
            Assert.AreEqual(FP64.FromInt(4), t.PreviousPosition.x);
            Assert.AreEqual(FP64.FromInt(1), t.Rotation);
        }
    }
}
