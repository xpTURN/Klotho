using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// [Primitive] is what makes the entity-component inspector print a value type on one line via
    /// ToString() instead of recursing into its public fields. Its only consumer lives in the Unity
    /// editor assembly (ComponentReflectionCache.IsPrimitive) — which cannot be reached from here — so
    /// these tests pin the marker itself, which is the part that gets forgotten on a new value type.
    /// Nothing about serialization, hashing, or the wire depends on it.
    /// </summary>
    [TestFixture]
    public class InspectorPrimitiveMarkerTests
    {
        private static bool IsMarked<T>() => typeof(T).GetCustomAttribute<PrimitiveAttribute>() != null;

        // Without the marker these render as "Bytes (fixed, no reader registered)" plus a Length row:
        // the fixed buffer is a public field, so the inspector recurses and never calls ToString(),
        // and a name/label field shows as anything but its text.
        [Test]
        public void FixedStrings_AreMarkedPrimitive()
        {
            Assert.IsTrue(IsMarked<FixedString32>(), "FixedString32 must be [Primitive]");
            Assert.IsTrue(IsMarked<FixedString64>(), "FixedString64 must be [Primitive]");
        }

        // The types that already relied on this stay marked — the same omission would make a
        // TransformComponent expand into raw fixed-point integers.
        [Test]
        public void DeterministicValueTypes_StayMarkedPrimitive()
        {
            Assert.IsTrue(IsMarked<FP64>());
            Assert.IsTrue(IsMarked<FPVector2>());
            Assert.IsTrue(IsMarked<FPVector3>());
            Assert.IsTrue(IsMarked<FPVector4>());
            Assert.IsTrue(IsMarked<FPQuaternion>());
            Assert.IsTrue(IsMarked<EntityRef>());
        }

        // ToString() is the readable form the inspector falls back on, so it has to be an override —
        // the marker alone would otherwise print the type name.
        [Test]
        public void MarkedTypes_OverrideToString()
        {
            var fs = FixedString32.FromString("hello");
            Assert.AreEqual("hello", fs.ToString());

            var v = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(-3));
            StringAssert.StartsWith("(", v.ToString());
            StringAssert.Contains("1.0000", v.ToString());
        }
    }
}
