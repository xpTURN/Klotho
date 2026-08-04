using System;

using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for KlothoGenerator. The generator is a prebuilt Roslyn
    /// source generator shipped in the package; if the editor's C# compiler cannot load it, the emit
    /// silently produces nothing and everything downstream fails as "member not found". These tests
    /// assert the emitted members exist AND behave, plus that the emitted assembly registrar was
    /// picked up by the runtime's assembly scan.
    /// </summary>
    [TestFixture]
    public class SourceGeneratorSmokeTests
    {
        [Test]
        public void Component_TypeIdIsRegisteredFromAttribute()
        {
            Assert.AreEqual(9301, ComponentStorageRegistry.GetTypeId<SmokeProbeComponent>());
            Assert.AreEqual(9302, ComponentStorageRegistry.GetTypeId<SmokeBundleComponent>());
            Assert.AreEqual(9303, ComponentStorageRegistry.GetTypeId<SmokeSingletonComponent>());

            // Reverse lookup goes through the generated registrar's type table.
            Assert.AreEqual(typeof(SmokeProbeComponent), ComponentStorageRegistry.GetType(9301));
        }

        [Test]
        public void Component_GeneratedCodecRoundTrips()
        {
            var src = new SmokeProbeComponent
            {
                Counter = 42,
                Flag = true,
                Value = FP64.FromDouble(1.25),
                Position = new FPVector3(1, 2, 3),
            };

            // int + bool + FP64 + FPVector3 = 4 + 1 + 8 + 24
            Assert.AreEqual(37, src.GetSerializedSize());

            Span<byte> buffer = stackalloc byte[src.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            src.Serialize(ref writer);
            Assert.AreEqual(src.GetSerializedSize(), writer.Position, "GetSerializedSize must match bytes written");

            var reader = new SpanReader(buffer.Slice(0, writer.Position));
            var dst = new SmokeProbeComponent();
            dst.Deserialize(ref reader);

            Assert.AreEqual(src.Counter, dst.Counter);
            Assert.AreEqual(src.Flag, dst.Flag);
            Assert.AreEqual(src.Value, dst.Value);
            Assert.AreEqual(src.Position, dst.Position);
        }

        [Test]
        public void Component_GeneratedHashIsStableAndSensitive()
        {
            var a = new SmokeProbeComponent { Counter = 1, Value = FP64.One };
            var b = new SmokeProbeComponent { Counter = 1, Value = FP64.One };
            Assert.AreEqual(a.GetHash(0UL), b.GetHash(0UL), "identical components must hash equal");

            b.Counter = 2;
            Assert.AreNotEqual(a.GetHash(0UL), b.GetHash(0UL), "a changed field must change the hash");
        }

        // [KlothoSerializableStruct] with a fixed buffer — the generator's unsafe emit path, which
        // needs allowUnsafeCode on the consuming assembly and a compiler that accepts the emitted
        // fixed-buffer element access.
        [Test]
        public unsafe void SerializableStruct_FixedBufferRoundTrips()
        {
            var src = new SmokeBundle { RootId = 5, Margin = FP64.FromDouble(0.5) };
            for (int i = 0; i < 4; i++) src.Slots[i] = 100 + i;

            // int + int[4] + FP64 = 4 + 16 + 8
            Assert.AreEqual(28, src.GetSerializedSize());

            Span<byte> buffer = stackalloc byte[src.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            src.Serialize(ref writer);

            var reader = new SpanReader(buffer.Slice(0, writer.Position));
            var dst = new SmokeBundle();
            dst.Deserialize(ref reader);

            Assert.AreEqual(src.RootId, dst.RootId);
            Assert.AreEqual(src.Margin, dst.Margin);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(src.Slots[i], dst.Slots[i], $"Slots[{i}]");
        }

        // A component whose only field is a [KlothoSerializableStruct] — the nested-codec delegation
        // the generator emits instead of inlining the bundle's fields.
        [Test]
        public unsafe void Component_NestedBundleDelegatesToBundleCodec()
        {
            var bundle = new SmokeBundle { RootId = 9, Margin = FP64.FromInt(2) };
            for (int i = 0; i < 4; i++) bundle.Slots[i] = i * 7;

            var src = new SmokeBundleComponent { Bundle = bundle };
            Assert.AreEqual(bundle.GetSerializedSize(), src.GetSerializedSize(), "outer size = bundle size (single field)");

            Span<byte> buffer = stackalloc byte[src.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            src.Serialize(ref writer);

            var reader = new SpanReader(buffer.Slice(0, writer.Position));
            var dst = new SmokeBundleComponent();
            dst.Deserialize(ref reader);

            Assert.AreEqual(bundle.RootId, dst.Bundle.RootId);
            Assert.AreEqual(bundle.Margin, dst.Bundle.Margin);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(bundle.Slots[i], dst.Bundle.Slots[i], $"Slots[{i}]");
        }

        // [KlothoDataAsset(..., AssetId = ...)] makes the generator emit the AssetId property plus a
        // parameterless ctor that seeds it — the single-instance asset lookup path depends on it.
        [Test]
        public void DataAsset_GeneratedAssetIdIsSeeded()
        {
            var asset = new SmokeAsset();
            Assert.AreEqual(9304, asset.AssetId);
        }
    }
}
