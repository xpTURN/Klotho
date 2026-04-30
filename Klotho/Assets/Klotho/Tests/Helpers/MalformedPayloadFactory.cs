using System;
using System.Buffers.Binary;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Generates malformed wire payloads for L1/L2 robustness tests.
    /// Each helper produces a byte array that begins with a NetworkMessageType id
    /// followed by a body that intentionally violates the wire format.
    /// </summary>
    public static class MalformedPayloadFactory
    {
        /// <summary>
        /// type-id only (1 byte). For message types whose body is non-empty, this triggers
        /// an "out of range" read in the generated DeserializeData, exercising L1's catch.
        /// </summary>
        public static byte[] EmptyBody(NetworkMessageType type)
        {
            return new byte[] { (byte)type };
        }

        /// <summary>
        /// type-id + an int32 string-length prefix that lies about the actual payload size.
        /// declaredLen is written in the prefix; only actualLen bytes follow.
        /// Use declaredLen > actualLen to trigger a truncation read.
        /// </summary>
        public static byte[] TruncatedString(NetworkMessageType type, int declaredLen, int actualLen)
        {
            byte[] data = new byte[1 + 4 + actualLen];
            data[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), declaredLen);
            // bytes [5 .. 5+actualLen) are left zero — irrelevant to the truncation test
            return data;
        }

        /// <summary>
        /// type-id + an int32 length field set to a negative value.
        /// </summary>
        public static byte[] NegativeLength(NetworkMessageType type)
        {
            byte[] data = new byte[1 + 4];
            data[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), -1);
            return data;
        }

        /// <summary>
        /// Single byte with an unregistered type-id.
        /// </summary>
        public static byte[] UnknownType(byte typeId)
        {
            return new byte[] { typeId };
        }

        /// <summary>
        /// type-id + an int32 length field set to int.MaxValue.
        /// </summary>
        public static byte[] OverflowLength(NetworkMessageType type)
        {
            byte[] data = new byte[1 + 4];
            data[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), int.MaxValue);
            return data;
        }

        /// <summary>
        /// Random byte sequence with deterministic seed. The first byte may or may not
        /// match a registered type — useful for fuzz-style coverage of the L1 catch.
        /// </summary>
        public static byte[] RandomGarbage(int seed, int length)
        {
            var rng = new Random(seed);
            byte[] data = new byte[length];
            rng.NextBytes(data);
            return data;
        }
    }
}
