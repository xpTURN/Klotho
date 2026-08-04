using System;

using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for the wire layer. SpanWriter/SpanReader are ref structs
    /// over <c>Span&lt;byte&gt;</c> and FixedString uses fixed buffers, so this covers the parts of
    /// the package that lean hardest on the editor's C# version and on Mono's unsafe/Span support.
    /// Byte-level expectations are goldens from the .NET 8 build — the wire format must be identical
    /// across runtimes for a Unity 2022 client to talk to a .NET server.
    /// </summary>
    [TestFixture]
    public class SerializationSmokeTests
    {
        [Test]
        public void SpanWriterReader_PrimitivesRoundTrip()
        {
            Span<byte> buffer = stackalloc byte[64];
            var writer = new SpanWriter(buffer);

            writer.WriteByte(0xAB);
            writer.WriteBool(true);
            writer.WriteInt16(-1234);
            writer.WriteUInt16(65535);
            writer.WriteInt32(int.MinValue);
            writer.WriteUInt32(uint.MaxValue);
            writer.WriteInt64(long.MinValue);
            writer.WriteUInt64(ulong.MaxValue);
            writer.WriteString("klotho 2022");

            int written = writer.Position;

            var reader = new SpanReader(buffer.Slice(0, written));
            Assert.AreEqual(0xAB, reader.ReadByte());
            Assert.AreEqual(true, reader.ReadBool());
            Assert.AreEqual(-1234, reader.ReadInt16());
            Assert.AreEqual(65535, reader.ReadUInt16());
            Assert.AreEqual(int.MinValue, reader.ReadInt32());
            Assert.AreEqual(uint.MaxValue, reader.ReadUInt32());
            Assert.AreEqual(long.MinValue, reader.ReadInt64());
            Assert.AreEqual(ulong.MaxValue, reader.ReadUInt64());
            Assert.AreEqual("klotho 2022", reader.ReadString());
            Assert.AreEqual(0, reader.Remaining, "reader must consume exactly what the writer produced");
        }

        [Test]
        public void SpanWriter_LittleEndianLayout_MatchesWireFormat()
        {
            Span<byte> buffer = stackalloc byte[8];
            var writer = new SpanWriter(buffer);
            writer.WriteInt32(0x01020304);

            Assert.AreEqual(0x04, buffer[0]);
            Assert.AreEqual(0x03, buffer[1]);
            Assert.AreEqual(0x02, buffer[2]);
            Assert.AreEqual(0x01, buffer[3]);
        }

        [Test]
        public void FixedString_RoundTripsThroughFixedBuffer()
        {
            var s32 = FixedString32.FromString("hello");
            Assert.AreEqual("hello", s32.ToString());
            Assert.AreEqual(5, s32.Length);
            Assert.AreEqual(s32, FixedString32.FromString("hello"));
            Assert.AreNotEqual(s32, FixedString32.FromString("hellp"));

            var s64 = FixedString64.FromString("a longer value that still fits in sixty-two by");
            Assert.AreEqual("a longer value that still fits in sixty-two by", s64.ToString());
        }

        [Test]
        public void Component_WireBytesAndHash_MatchDotnetGoldens()
        {
            var probe = new SmokeProbeComponent
            {
                Counter = 42,
                Flag = true,
                Value = FP64.FromDouble(1.25),
                Position = new FPVector3(1, 2, 3),
            };

            Assert.AreEqual(0xAE0C45EAC6F9EFBDUL, probe.GetHash(0UL), "component hash must match the .NET 8 golden");

            Span<byte> buffer = stackalloc byte[probe.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            probe.Serialize(ref writer);

            const string ExpectedHex =
                "2A000000" +                 // Counter = 42
                "01" +                       // Flag = true
                "0000004001000000" +         // Value = 1.25 (raw 0x140000000)
                "0000000001000000" +         // Position.x = 1
                "0000000002000000" +         // Position.y = 2
                "0000000003000000";          // Position.z = 3

            Assert.AreEqual(ExpectedHex, ToHex(buffer.Slice(0, writer.Position)));
        }

        [Test]
        public unsafe void SerializableStruct_HashMatchesDotnetGolden()
        {
            var bundle = new SmokeBundle { RootId = 5, Margin = FP64.FromDouble(0.5) };
            for (int i = 0; i < 4; i++) bundle.Slots[i] = 100 + i;

            Assert.AreEqual(0x158EA48439F32821UL, bundle.GetHash(0UL), "bundle hash must match the .NET 8 golden");
        }

        private static string ToHex(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string Digits = "0123456789ABCDEF";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }
}
