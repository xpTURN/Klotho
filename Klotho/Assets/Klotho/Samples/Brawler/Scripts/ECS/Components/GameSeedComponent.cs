using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(107)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GameSeedComponent : IComponent
    {
        public ulong WorldSeed;
    }
}
