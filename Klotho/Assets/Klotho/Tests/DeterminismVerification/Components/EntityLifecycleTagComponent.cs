using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(9002)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct EntityLifecycleTagComponent : IComponent
    {
        public int SpawnedAtTick;
        public int LifetimeTicks;
    }
}
