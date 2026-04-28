using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(1)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct TransformComponent : IComponent
    {
        public FPVector3 Position;
        public FP64 Rotation;
        public FPVector3 Scale;

        public FPVector3 PreviousPosition;
        public FP64 PreviousRotation;

        public int TeleportTick;
    }
}
