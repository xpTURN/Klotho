using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(102)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct ItemComponent : IComponent
    {
        public int ItemType;         // 0=Shield, 1=Boost, 2=Bomb
        public int RemainingTicks;   // Item lifetime
        public long EntityId;        // EntityRef.ToId() — used as view registry key
    }
}
