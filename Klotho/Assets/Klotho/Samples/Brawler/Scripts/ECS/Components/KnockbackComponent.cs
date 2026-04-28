using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(103)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct KnockbackComponent : IComponent
    {
        public FPVector2 Force;
        public int InitialDurationTicks;
        public int DurationTicks;
        public bool BlockInput;  // true: blocks movement/jump (bomb/trap), false: keeps controls (attack/push)
    }
}
