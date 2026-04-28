using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(22)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct VelocityComponent : IComponent
    {
        public FPVector3 Velocity;
    }
}
