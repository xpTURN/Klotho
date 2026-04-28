using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Serialization
{
    public static class FPSpanExtensions
    {
        // === SpanWriter ===

        public static void WriteFP(ref this SpanWriter writer, FP64 value)
            => writer.WriteInt64(value.RawValue);

        public static void WriteFP(ref this SpanWriter writer, FPVector2 value)
        {
            writer.WriteInt64(value.x.RawValue);
            writer.WriteInt64(value.y.RawValue);
        }

        public static void WriteFP(ref this SpanWriter writer, FPVector3 value)
        {
            writer.WriteInt64(value.x.RawValue);
            writer.WriteInt64(value.y.RawValue);
            writer.WriteInt64(value.z.RawValue);
        }

        public static void WriteFP(ref this SpanWriter writer, FPVector4 value)
        {
            writer.WriteInt64(value.x.RawValue);
            writer.WriteInt64(value.y.RawValue);
            writer.WriteInt64(value.z.RawValue);
            writer.WriteInt64(value.w.RawValue);
        }

        public static void WriteFP(ref this SpanWriter writer, FPQuaternion value)
        {
            writer.WriteInt64(value.x.RawValue);
            writer.WriteInt64(value.y.RawValue);
            writer.WriteInt64(value.z.RawValue);
            writer.WriteInt64(value.w.RawValue);
        }

        // === SpanReader ===

        public static FP64 ReadFP64(ref this SpanReader reader)
            => FP64.FromRaw(reader.ReadInt64());

        public static FPVector2 ReadFPVector2(ref this SpanReader reader)
            => new FPVector2(
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()));

        public static FPVector3 ReadFPVector3(ref this SpanReader reader)
            => new FPVector3(
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()));

        public static FPVector4 ReadFPVector4(ref this SpanReader reader)
            => new FPVector4(
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()));

        public static FPQuaternion ReadFPQuaternion(ref this SpanReader reader)
            => new FPQuaternion(
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()),
                FP64.FromRaw(reader.ReadInt64()));

        // === FPRigidBody ===

        public static void WriteFPRigidBody(ref this SpanWriter writer, FPRigidBody value)
        {
            writer.WriteFP(value.mass);
            writer.WriteFP(value.inverseMass);
            writer.WriteFP(value.velocity);
            writer.WriteFP(value.force);
            writer.WriteFP(value.angularVelocity);
            writer.WriteFP(value.torque);
            writer.WriteFP(value.linearDamping);
            writer.WriteFP(value.angularDamping);
            writer.WriteFP(value.restitution);
            writer.WriteFP(value.friction);
            writer.WriteBool(value.isKinematic);
            writer.WriteBool(value.isStatic);
        }

        public static FPRigidBody ReadFPRigidBody(ref this SpanReader reader)
        {
            var v = new FPRigidBody();
            v.mass = reader.ReadFP64();
            v.inverseMass = reader.ReadFP64();
            v.velocity = reader.ReadFPVector3();
            v.force = reader.ReadFPVector3();
            v.angularVelocity = reader.ReadFPVector3();
            v.torque = reader.ReadFPVector3();
            v.linearDamping = reader.ReadFP64();
            v.angularDamping = reader.ReadFP64();
            v.restitution = reader.ReadFP64();
            v.friction = reader.ReadFP64();
            v.isKinematic = reader.ReadBool();
            v.isStatic = reader.ReadBool();
            return v;
        }

        // === FPCollider ===
        // Serialization format: type(1) + shape data (always padded to the largest shape = FPBoxShape = 80 bytes)
        // Total fixed size: 81 bytes

        public static void WriteFPCollider(ref this SpanWriter writer, FPCollider value)
        {
            writer.WriteByte((byte)value.type);
            switch (value.type)
            {
                case ShapeType.Box:
                    writer.WriteFP(value.box.halfExtents);
                    writer.WriteFP(value.box.position);
                    writer.WriteFP(value.box.rotation);
                    break;
                case ShapeType.Sphere:
                    writer.WriteFP(value.sphere.radius);
                    writer.WriteFP(value.sphere.position);
                    for (int i = 0; i < 48; i++) writer.WriteByte(0);
                    break;
                case ShapeType.Capsule:
                    writer.WriteFP(value.capsule.halfHeight);
                    writer.WriteFP(value.capsule.radius);
                    writer.WriteFP(value.capsule.position);
                    writer.WriteFP(value.capsule.rotation);
                    for (int i = 0; i < 8; i++) writer.WriteByte(0);
                    break;
                case ShapeType.Mesh:
                    writer.WriteFP(value.mesh.position);
                    writer.WriteFP(value.mesh.rotation);
                    for (int i = 0; i < 24; i++) writer.WriteByte(0);
                    break;
                default:
                    for (int i = 0; i < 80; i++) writer.WriteByte(0);
                    break;
            }
        }

        public static FPCollider ReadFPCollider(ref this SpanReader reader)
        {
            var c = default(FPCollider);
            c.type = (ShapeType)reader.ReadByte();
            switch (c.type)
            {
                case ShapeType.Box:
                    c.box = new FPBoxShape(
                        reader.ReadFPVector3(),
                        reader.ReadFPVector3(),
                        reader.ReadFPQuaternion());
                    break;
                case ShapeType.Sphere:
                    c.sphere = new FPSphereShape(
                        reader.ReadFP64(),
                        reader.ReadFPVector3());
                    reader.ReadRawBytes(48);
                    break;
                case ShapeType.Capsule:
                    c.capsule = new FPCapsuleShape(
                        reader.ReadFP64(),
                        reader.ReadFP64(),
                        reader.ReadFPVector3(),
                        reader.ReadFPQuaternion());
                    reader.ReadRawBytes(8);
                    break;
                case ShapeType.Mesh:
                    c.mesh = new FPMeshShape(
                        reader.ReadFPVector3(),
                        reader.ReadFPQuaternion());
                    reader.ReadRawBytes(24);
                    break;
                default:
                    reader.ReadRawBytes(80);
                    break;
            }
            return c;
        }
    }
}
