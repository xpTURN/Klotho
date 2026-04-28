using System;
using System.IO;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Binary serialization/deserialization of static collider arrays.
    /// </summary>
    public static class FPStaticColliderSerializer
    {
        const uint Magic = 0x46505343;  // "FPSC"
        const ushort Version = 1;
        const int HeaderSize = 8;

        // Called from the Editor — write directly via SpanWriter, then File.WriteAllBytes
        public static void Save(FPStaticCollider[] colliders, string assetPath)
        {
            int size = HeaderSize;
            foreach (var c in colliders) size += c.GetSerializedSize();

            var buf = new byte[size];
            var writer = new SpanWriter(buf);

            writer.WriteUInt32(Magic);
            writer.WriteUInt16(Version);
            writer.WriteUInt16((ushort)colliders.Length);

            foreach (var c in colliders) c.Serialize(ref writer);

            File.WriteAllBytes(assetPath, buf);
        }

        // Called at runtime
        public static List<FPStaticCollider> Load(string path)
        {
            var buffer = System.IO.File.ReadAllBytes(path);
            return Load(buffer);
        }

        // Called at runtime (after TextAsset.bytes or Addressables load)
        public static List<FPStaticCollider> Load(ReadOnlySpan<byte> buffer)
        {
            var reader = new SpanReader(buffer);

            uint magic = reader.ReadUInt32();
            ushort version = reader.ReadUInt16();
            int count = reader.ReadUInt16();

            if (magic != Magic)
                throw new InvalidOperationException($"Invalid FPSC magic: 0x{magic:X8}");

            var result = new List<FPStaticCollider>(count);
            for (int i = 0; i < count; i++)
                result.Add(FPStaticCollider.Deserialize(ref reader));
                
            return result;
        }
    }
}
