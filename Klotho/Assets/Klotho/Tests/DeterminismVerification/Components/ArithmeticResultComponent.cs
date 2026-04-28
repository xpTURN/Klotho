using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(9000)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct ArithmeticResultComponent : IComponent
    {
        public FP64 ScalarResult;
        public FPVector3 VectorResult;
        public FPQuaternion QuatResult;
    }
}
