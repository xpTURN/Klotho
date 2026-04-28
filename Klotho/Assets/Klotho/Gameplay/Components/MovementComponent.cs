using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(23)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MovementComponent : IComponent
    {
        public FP64 MoveSpeed;
        public FPVector3 TargetPosition;
        public bool IsMoving;
    }
}
