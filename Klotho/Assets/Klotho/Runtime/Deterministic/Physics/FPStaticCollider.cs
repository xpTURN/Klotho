using System;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Non-moving static collider. Queried via the BVH.
    /// </summary>
    [Serializable]
    public struct FPStaticCollider
    {
        const int BaseSize = 105;  // id(4) + isTrigger(1) + _pad(3) + restitution(8) + friction(8) + collider(81)

        public int id;
        public FPCollider collider;  // shape contains position/rotation internally (stores the FP64-converted result on export)
        public FPMeshData meshData;  // for the Mesh shape
        public bool isTrigger;
        public FP64 restitution;
        public FP64 friction;

        public int GetSerializedSize()
        {
            int size = BaseSize;
            if (collider.type == ShapeType.Mesh && meshData != null)
                size += 4 + meshData.GetSerializedSize();
            return size;
        }

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(id);
            writer.WriteBool(isTrigger);
            writer.WriteByte(0); writer.WriteByte(0); writer.WriteByte(0);  // _pad
            writer.WriteFP(restitution);
            writer.WriteFP(friction);
            writer.WriteFPCollider(collider);
            if (collider.type == ShapeType.Mesh && meshData != null)
            {
                writer.WriteInt32(meshData.GetSerializedSize());
                meshData.Serialize(ref writer);
            }
        }

        public static FPStaticCollider Deserialize(ref SpanReader reader)
        {
            var sc = new FPStaticCollider();
            sc.id          = reader.ReadInt32();
            sc.isTrigger   = reader.ReadBool();
            reader.ReadRawBytes(3);  // _pad
            sc.restitution = reader.ReadFP64();
            sc.friction    = reader.ReadFP64();
            sc.collider    = reader.ReadFPCollider();
            if (sc.collider.type == ShapeType.Mesh)
            {
                reader.ReadInt32();  // meshDataSize — Deserialize handles the exact byte count internally
                sc.meshData = FPMeshData.Deserialize(ref reader);
            }
            return sc;
        }
    }
}
