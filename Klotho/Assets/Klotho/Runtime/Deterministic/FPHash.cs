using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Deterministic
{
    /// <summary>
    /// FNV-1a helper that folds FP types and primary value types into an accumulating hash.
    /// </summary>
    public static class FPHash
    {
        public const ulong FNV_OFFSET = 14695981039346656037UL;
        public const ulong FNV_PRIME = 1099511628211UL;

        public static ulong Hash(ulong current, FP64 value)
        {
            current ^= (ulong)value.RawValue;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, FPVector2 value)
        {
            current ^= (ulong)value.x.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.y.RawValue;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, FPVector3 value)
        {
            current ^= (ulong)value.x.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.y.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.z.RawValue;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, FPVector4 value)
        {
            current ^= (ulong)value.x.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.y.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.z.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.w.RawValue;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, FPQuaternion value)
        {
            current ^= (ulong)value.x.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.y.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.z.RawValue;
            current *= FNV_PRIME;
            current ^= (ulong)value.w.RawValue;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, int value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, bool value)
        {
            current ^= (ulong)(value ? 1 : 0);
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, long value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, byte value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, short value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, uint value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, ulong value)
        {
            current ^= value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong Hash(ulong current, ushort value)
        {
            current ^= (ulong)value;
            current *= FNV_PRIME;
            return current;
        }

        public static ulong HashBytes(ulong current, ReadOnlySpan<byte> data)
        {
            int i = 0;
            int longLen = data.Length - 7;
            for (; i < longLen; i += 8)
            {
                ulong v = (ulong)data[i]
                    | ((ulong)data[i + 1] << 8)
                    | ((ulong)data[i + 2] << 16)
                    | ((ulong)data[i + 3] << 24)
                    | ((ulong)data[i + 4] << 32)
                    | ((ulong)data[i + 5] << 40)
                    | ((ulong)data[i + 6] << 48)
                    | ((ulong)data[i + 7] << 56);
                current ^= v;
                current *= FNV_PRIME;
            }
            for (; i < data.Length; i++)
            {
                current ^= data[i];
                current *= FNV_PRIME;
            }
            return current;
        }

        public static ulong Hash(ulong current, FPRigidBody value)
        {
            current = Hash(current, value.mass);
            current = Hash(current, value.inverseMass);
            current = Hash(current, value.velocity);
            current = Hash(current, value.force);
            current = Hash(current, value.angularVelocity);
            current = Hash(current, value.torque);
            current = Hash(current, value.linearDamping);
            current = Hash(current, value.angularDamping);
            current = Hash(current, value.restitution);
            current = Hash(current, value.friction);
            current = Hash(current, value.isKinematic);
            current = Hash(current, value.isStatic);
            return current;
        }

        public static ulong Hash(ulong current, FPCollider value)
        {
            current = Hash(current, (byte)value.type);
            switch (value.type)
            {
                case ShapeType.Box:
                    current = Hash(current, value.box.halfExtents);
                    current = Hash(current, value.box.position);
                    current = Hash(current, value.box.rotation);
                    break;
                case ShapeType.Sphere:
                    current = Hash(current, value.sphere.radius);
                    current = Hash(current, value.sphere.position);
                    break;
                case ShapeType.Capsule:
                    current = Hash(current, value.capsule.halfHeight);
                    current = Hash(current, value.capsule.radius);
                    current = Hash(current, value.capsule.position);
                    current = Hash(current, value.capsule.rotation);
                    break;
                case ShapeType.Mesh:
                    current = Hash(current, value.mesh.position);
                    current = Hash(current, value.mesh.rotation);
                    break;
            }
            return current;
        }
    }
}
