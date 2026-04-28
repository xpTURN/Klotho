using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(21)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct HealthComponent : IComponent
    {
        public int MaxHealth;
        public int CurrentHealth;
    }
}
