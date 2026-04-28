using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Dynamic body in the physics world. Holds rigid body properties, colliders, and transform.
    /// </summary>
    [Serializable]
    public struct FPPhysicsBody
    {
        public int id;
        public FPRigidBody rigidBody;
        public FPCollider collider;
        public FPMeshData meshData;
        public FPVector3 position;
        public FPQuaternion rotation;
        public FPVector3 colliderOffset;
        public bool isTrigger;
        public bool useCCD;
        public bool useSweep;
    }
}
