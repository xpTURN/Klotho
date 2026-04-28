using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(9001)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct RandomStateComponent : IComponent
    {
        public FP64 Value;
        public FPVector3 Direction;
        public int BranchResult;
    }
}
