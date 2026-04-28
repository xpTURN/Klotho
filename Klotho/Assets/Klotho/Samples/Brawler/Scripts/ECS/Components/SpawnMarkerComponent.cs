using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(105)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SpawnMarkerComponent : IComponent
    {
        public FPVector2 SpawnPosition;
        public int PlayerId;
    }
}
