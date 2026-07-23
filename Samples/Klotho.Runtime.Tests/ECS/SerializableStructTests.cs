using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS.Tests
{
    // ── Test value bundles (generated codec via [KlothoSerializableStruct]) ──

    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal unsafe partial struct TestBundle
    {
        public int RootId;
        public fixed int Slots[4];
        public FP64 Margin;
        public byte Flag;
    }

    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal partial struct TestOuter
    {
        public int Tag;
        public TestBundle Inner;   // nested [KlothoSerializableStruct] → recursive delegation
    }

    [TestFixture]
    public class SerializableStructTests
    {
        private static unsafe TestBundle MakeBundle()
        {
            var b = new TestBundle { RootId = 7, Margin = FP64.FromInt(3), Flag = 0xAB };
            for (int i = 0; i < 4; i++) b.Slots[i] = 100 + i;
            return b;
        }

        [Test]
        public unsafe void Bundle_RoundTrips()
        {
            var src = MakeBundle();

            Span<byte> buf = stackalloc byte[src.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            src.Serialize(ref writer);

            // GetSerializedSize must equal the bytes actually written (release builds skip bounds check).
            Assert.AreEqual(src.GetSerializedSize(), writer.Position, "GetSerializedSize mismatch");
            Assert.AreEqual(4 + 4 * 4 + 8 + 1, src.GetSerializedSize(), "expected 29 bytes (int + int[4] + FP64 + byte)");

            var reader = new SpanReader(buf.Slice(0, writer.Position));
            var dst = new TestBundle();
            dst.Deserialize(ref reader);

            Assert.AreEqual(src.RootId, dst.RootId);
            Assert.AreEqual(src.Margin, dst.Margin);
            Assert.AreEqual(src.Flag, dst.Flag);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(src.Slots[i], dst.Slots[i], $"Slots[{i}]");
        }

        [Test]
        public unsafe void Bundle_Hash_IsSensitiveAndStable()
        {
            var a = MakeBundle();
            var b = MakeBundle();
            Assert.AreEqual(a.GetHash(0UL), b.GetHash(0UL), "identical bundles must hash equal");

            b.Slots[2] = 999;   // change one fixed-buffer element
            Assert.AreNotEqual(a.GetHash(0UL), b.GetHash(0UL), "a changed field must change the hash");
        }

        [Test]
        public unsafe void Outer_NestedBundle_RoundTripsAndHashes()
        {
            var src = new TestOuter { Tag = 42, Inner = MakeBundle() };

            Span<byte> buf = stackalloc byte[src.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            src.Serialize(ref writer);
            Assert.AreEqual(src.GetSerializedSize(), writer.Position);
            Assert.AreEqual(4 + (4 + 4 * 4 + 8 + 1), src.GetSerializedSize(), "outer = Tag + bundle(29)");

            var reader = new SpanReader(buf.Slice(0, writer.Position));
            var dst = new TestOuter();
            dst.Deserialize(ref reader);

            Assert.AreEqual(src.Tag, dst.Tag);
            Assert.AreEqual(src.Inner.RootId, dst.Inner.RootId);
            Assert.AreEqual(src.Inner.Margin, dst.Inner.Margin);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(src.Inner.Slots[i], dst.Inner.Slots[i]);

            // Nested field participates in the outer hash.
            var changed = new TestOuter { Tag = 42, Inner = MakeBundle() };
            changed.Inner.RootId = -1;
            Assert.AreNotEqual(src.GetHash(0UL), changed.GetHash(0UL), "nested field must affect outer hash");
        }
    }
}
