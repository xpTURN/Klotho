using System;
using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Collider attached to a physics body. Holds Sphere, Box, Capsule, or Mesh shape based on ShapeType.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Explicit)]
    public struct FPCollider
    {
        [FieldOffset(0)]
        public ShapeType type;

        [FieldOffset(8)]
        public FPBoxShape box;

        [FieldOffset(8)]
        public FPSphereShape sphere;

        [FieldOffset(8)]
        public FPCapsuleShape capsule;

        [FieldOffset(8)]
        public FPMeshShape mesh;

        public static FPCollider FromBox(FPBoxShape shape)
        {
            var c = default(FPCollider);
            c.type = ShapeType.Box;
            c.box = shape;
            return c;
        }

        public static FPCollider FromSphere(FPSphereShape shape)
        {
            var c = default(FPCollider);
            c.type = ShapeType.Sphere;
            c.sphere = shape;
            return c;
        }

        public static FPCollider FromCapsule(FPCapsuleShape shape)
        {
            var c = default(FPCollider);
            c.type = ShapeType.Capsule;
            c.capsule = shape;
            return c;
        }

        public static FPCollider FromMesh(FPMeshShape shape)
        {
            var c = default(FPCollider);
            c.type = ShapeType.Mesh;
            c.mesh = shape;
            return c;
        }

        public FPBounds3 GetWorldBounds(FPMeshData meshData = null)
        {
            switch (type)
            {
                case ShapeType.Box: return box.GetWorldBounds();
                case ShapeType.Sphere: return sphere.GetWorldBounds();
                case ShapeType.Capsule: return capsule.GetWorldBounds();
                case ShapeType.Mesh: return mesh.GetWorldBounds(meshData);
                default: return default;
            }
        }
    }
}
