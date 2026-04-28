using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(24)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CombatComponent : IComponent
    {
        public int AttackDamage;
        public int AttackRange;
    }
}
