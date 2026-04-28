#pragma warning disable KLSG_ECS004

using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(25)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PhysicsBodyComponent : IComponent
    {
        public FPRigidBody RigidBody;
        public FPCollider Collider;
        public FPVector3 ColliderOffset;
    }
}
