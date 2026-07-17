using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(4)]
    [KlothoCoreComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SessionParticipantComponent : IComponent
    {
        public int PlayerId;
    }
}
