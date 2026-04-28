using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    public enum BotState : byte
    {
        Idle,
        Chase,
        Attack,
        Evade,
        Skill,
    }

    public enum BotDifficulty : byte
    {
        Easy   = 0,
        Normal = 1,
        Hard   = 2,
    }

    [KlothoComponent(110)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BotComponent : IComponent
    {
        public byte State;
        public EntityRef Target;
        public int StateTimer;
        public int AttackCooldown;
        public int DecisionCooldown;
        public byte Difficulty;
        public FPVector3 Destination;
        public bool HasDestination;
        public int EvadeCooldown;
    }
}
