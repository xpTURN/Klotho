using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(104)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SkillCooldownComponent : IComponent
    {
        public int Skill0Cooldown;
        public int Skill1Cooldown;
        public int ShieldTicks;
        public int BoostTicks;
    }
}
