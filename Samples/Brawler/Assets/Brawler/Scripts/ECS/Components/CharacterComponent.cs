using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoComponent(100, MaxCount = 18)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CharacterComponent : IComponent
    {
        public int PlayerId;
        public int CharacterClass;   // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
        public int StockCount;       // Remaining stocks
        public int KnockbackPower;   // Currently accumulated knockback value (%)
        public bool IsDead;
        public int RespawnTimer;     // Respawn countdown ticks
        public FP64 InputMagnitude;  // Magnitude of input axis (0 = no input; for View animation)
        public bool IsGrounded;      // Updated every tick by GroundClampSystem
        public bool IsJumping;       // Set by PlatformerCommandSystem on jump, cleared by GroundClampSystem on landing
        public FPVector3 GroundNormal; // Updated every tick by GroundClampSystem (for slope projection)
        public int HitReactionTicks;  // > 0: hit animation in progress (for attack/push)
        public int ActionLockTicks;   // > 0: attack/skill activation animation in progress, input blocked
        public int ActiveSkillSlot;   // Last activated skill slot (0/1, -1=none) — for View animation
        // Entitlement loadout, set at spawn from the per-player LoadoutSeedComponent (real players) or to
        // full (bots / no entitlement). A set bit means that skill slot / consumable is acquired/owned.
        public int AcquiredSkillMask;     // bit0=skill slot0, bit1=skill slot1 acquired for this class. 0b11 = both.
        public int OwnedConsumableMask;   // consumable id offset -> bit (id 100 -> bit0). Gates in-match consumable use.
        // Highest UseConsumableCommand.UseSeq applied to this character. A use with UseSeq <= this is a
        // re-delivered duplicate (a reliable retry landing on a later tick) and is ignored — exactly-once
        // apply. Default 0; issued UseSeq is >= 1, so the first use always applies.
        public int LastConsumableUseSeq;
    }
}
