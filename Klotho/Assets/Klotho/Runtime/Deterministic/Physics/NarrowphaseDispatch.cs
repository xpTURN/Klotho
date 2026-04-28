using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Narrowphase collision test dispatch per shape pair.
    /// </summary>
    public static class NarrowphaseDispatch
    {
        public static bool Test(ref FPSphereShape a, ref FPSphereShape b, out FPContact contact)
        {
            return CollisionTests.SphereSphere(ref a, ref b, out contact);
        }

        public static bool Test(ref FPSphereShape a, ref FPBoxShape b, out FPContact contact)
        {
            return CollisionTests.SphereBox(ref a, ref b, out contact);
        }

        public static bool Test(ref FPBoxShape a, ref FPSphereShape b, out FPContact contact)
        {
            bool hit = CollisionTests.SphereBox(ref b, ref a, out contact);
            if (hit) contact = contact.Flipped();
            return hit;
        }

        public static bool Test(ref FPSphereShape a, ref FPCapsuleShape b, out FPContact contact)
        {
            return CollisionTests.SphereCapsule(ref a, ref b, out contact);
        }

        public static bool Test(ref FPCapsuleShape a, ref FPSphereShape b, out FPContact contact)
        {
            bool hit = CollisionTests.SphereCapsule(ref b, ref a, out contact);
            if (hit) contact = contact.Flipped();
            return hit;
        }

        public static bool Test(ref FPBoxShape a, ref FPBoxShape b, out FPContact contact)
        {
            return CollisionTests.BoxBox(ref a, ref b, out contact);
        }

        public static bool Test(ref FPBoxShape a, ref FPCapsuleShape b, out FPContact contact)
        {
            return CollisionTests.BoxCapsule(ref a, ref b, out contact);
        }

        public static bool Test(ref FPCapsuleShape a, ref FPBoxShape b, out FPContact contact)
        {
            bool hit = CollisionTests.BoxCapsule(ref b, ref a, out contact);
            if (hit) contact = contact.Flipped();
            return hit;
        }

        public static bool Test(ref FPCapsuleShape a, ref FPCapsuleShape b, out FPContact contact)
        {
            return CollisionTests.CapsuleCapsule(ref a, ref b, out contact);
        }

        public static bool Test(ref FPCollider a, ref FPCollider b, out FPContact contact)
        {
            return Test(ref a, null, ref b, null, out contact);
        }

        public static bool Test(
            ref FPCollider a, FPMeshData meshDataA,
            ref FPCollider b, FPMeshData meshDataB,
            out FPContact contact)
        {
            switch (a.type, b.type)
            {
                case (ShapeType.Sphere, ShapeType.Sphere):
                    return CollisionTests.SphereSphere(ref a.sphere, ref b.sphere, out contact);
                case (ShapeType.Sphere, ShapeType.Box):
                    {
                        bool hit = CollisionTests.SphereBox(ref a.sphere, ref b.box, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                case (ShapeType.Sphere, ShapeType.Capsule):
                    return CollisionTests.SphereCapsule(ref a.sphere, ref b.capsule, out contact);
                case (ShapeType.Box, ShapeType.Sphere):
                    {
                        bool hit = CollisionTests.SphereBox(ref b.sphere, ref a.box, out contact);
                        return hit;
                    }
                case (ShapeType.Box, ShapeType.Box):
                    return CollisionTests.BoxBox(ref a.box, ref b.box, out contact);
                case (ShapeType.Box, ShapeType.Capsule):
                    return CollisionTests.BoxCapsule(ref a.box, ref b.capsule, out contact);
                case (ShapeType.Capsule, ShapeType.Sphere):
                    {
                        bool hit = CollisionTests.SphereCapsule(ref b.sphere, ref a.capsule, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                case (ShapeType.Capsule, ShapeType.Box):
                    {
                        bool hit = CollisionTests.BoxCapsule(ref b.box, ref a.capsule, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                case (ShapeType.Capsule, ShapeType.Capsule):
                    return CollisionTests.CapsuleCapsule(ref a.capsule, ref b.capsule, out contact);
                case (ShapeType.Sphere, ShapeType.Mesh):
                    return CollisionTests.SphereMesh(ref a.sphere, ref b.mesh, meshDataB, out contact);
                case (ShapeType.Mesh, ShapeType.Sphere):
                    {
                        bool hit = CollisionTests.SphereMesh(ref b.sphere, ref a.mesh, meshDataA, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                case (ShapeType.Box, ShapeType.Mesh):
                    return CollisionTests.BoxMesh(ref a.box, ref b.mesh, meshDataB, out contact);
                case (ShapeType.Mesh, ShapeType.Box):
                    {
                        bool hit = CollisionTests.BoxMesh(ref b.box, ref a.mesh, meshDataA, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                case (ShapeType.Capsule, ShapeType.Mesh):
                    return CollisionTests.CapsuleMesh(ref a.capsule, ref b.mesh, meshDataB, out contact);
                case (ShapeType.Mesh, ShapeType.Capsule):
                    {
                        bool hit = CollisionTests.CapsuleMesh(ref b.capsule, ref a.mesh, meshDataA, out contact);
                        if (hit) contact = contact.Flipped();
                        return hit;
                    }
                default:
                    contact = default;
                    return false;
            }
        }

        public static int TestMulti(
            ref FPCollider a, FPMeshData meshDataA,
            ref FPCollider b, FPMeshData meshDataB,
            FPContact[] buffer, int maxContacts)
        {
            switch (a.type, b.type)
            {
                case (ShapeType.Sphere, ShapeType.Mesh):
                    return CollisionTests.SphereMeshMulti(ref a.sphere, ref b.mesh, meshDataB, buffer, maxContacts);
                case (ShapeType.Mesh, ShapeType.Sphere):
                    {
                        int cnt = CollisionTests.SphereMeshMulti(ref b.sphere, ref a.mesh, meshDataA, buffer, maxContacts);
                        for (int i = 0; i < cnt; i++) buffer[i] = buffer[i].Flipped();
                        return cnt;
                    }
                case (ShapeType.Capsule, ShapeType.Mesh):
                    return CollisionTests.CapsuleMeshMulti(ref a.capsule, ref b.mesh, meshDataB, buffer, maxContacts);
                case (ShapeType.Mesh, ShapeType.Capsule):
                    {
                        int cnt = CollisionTests.CapsuleMeshMulti(ref b.capsule, ref a.mesh, meshDataA, buffer, maxContacts);
                        for (int i = 0; i < cnt; i++) buffer[i] = buffer[i].Flipped();
                        return cnt;
                    }
                case (ShapeType.Box, ShapeType.Mesh):
                    return CollisionTests.BoxMeshMulti(ref a.box, ref b.mesh, meshDataB, buffer, maxContacts);
                case (ShapeType.Mesh, ShapeType.Box):
                    {
                        int cnt = CollisionTests.BoxMeshMulti(ref b.box, ref a.mesh, meshDataA, buffer, maxContacts);
                        for (int i = 0; i < cnt; i++) buffer[i] = buffer[i].Flipped();
                        return cnt;
                    }
                default:
                    if (Test(ref a, meshDataA, ref b, meshDataB, out var c))
                    { buffer[0] = c; return 1; }
                    return 0;
            }
        }

        public static bool RayCast(FPRay3 ray, ref FPCollider collider, FPMeshData meshData,
            out FP64 t, out FPVector3 normal)
        {
            switch (collider.type)
            {
                case ShapeType.Sphere: return CollisionTests.RaySphere(ray, ref collider.sphere, out t, out normal);
                case ShapeType.Box: return CollisionTests.RayBox(ray, ref collider.box, out t, out normal);
                case ShapeType.Capsule: return CollisionTests.RayCapsule(ray, ref collider.capsule, out t, out normal);
                case ShapeType.Mesh: return CollisionTests.RayMesh(ray, ref collider.mesh, meshData, out t, out normal);
                default: t = default; normal = default; return false;
            }
        }

        public static FP64 Distance(
            ref FPCollider a,
            ref FPCollider b,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            switch (a.type, b.type)
            {
                case (ShapeType.Sphere, ShapeType.Sphere):
                    return CollisionTests.DistanceSphereSphere(ref a.sphere, ref b.sphere, out normal, out closestA, out closestB);
                case (ShapeType.Sphere, ShapeType.Box):
                    return CollisionTests.DistanceSphereBox(ref a.sphere, ref b.box, out normal, out closestA, out closestB);
                case (ShapeType.Box, ShapeType.Sphere):
                    {
                        FP64 d = CollisionTests.DistanceSphereBox(ref b.sphere, ref a.box, out normal, out closestB, out closestA);
                        normal = -normal;
                        return d;
                    }
                case (ShapeType.Sphere, ShapeType.Capsule):
                    return CollisionTests.DistanceSphereCapsule(ref a.sphere, ref b.capsule, out normal, out closestA, out closestB);
                case (ShapeType.Capsule, ShapeType.Sphere):
                    {
                        FP64 d = CollisionTests.DistanceSphereCapsule(ref b.sphere, ref a.capsule, out normal, out closestB, out closestA);
                        normal = -normal;
                        return d;
                    }
                case (ShapeType.Box, ShapeType.Box):
                    return CollisionTests.DistanceBoxBox(ref a.box, ref b.box, out normal, out closestA, out closestB);
                case (ShapeType.Box, ShapeType.Capsule):
                    return CollisionTests.DistanceBoxCapsule(ref a.box, ref b.capsule, out normal, out closestA, out closestB);
                case (ShapeType.Capsule, ShapeType.Box):
                    {
                        FP64 d = CollisionTests.DistanceBoxCapsule(ref b.box, ref a.capsule, out normal, out closestB, out closestA);
                        normal = -normal;
                        return d;
                    }
                case (ShapeType.Capsule, ShapeType.Capsule):
                    return CollisionTests.DistanceCapsuleCapsule(ref a.capsule, ref b.capsule, out normal, out closestA, out closestB);
                default:
                    normal = FPVector3.Up;
                    closestA = default;
                    closestB = default;
                    return FP64.MaxValue;
            }
        }
    }
}
