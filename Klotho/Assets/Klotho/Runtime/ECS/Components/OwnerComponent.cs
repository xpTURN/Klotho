using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(2)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct OwnerComponent : IComponent
    {
        public int OwnerId;
    }
}
