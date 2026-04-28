using ZLogger;

using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    /// <summary>
    /// Evade transition condition when approaching an edge or in a high-knockback state.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L188-209.
    /// </summary>
    public class ShouldEvadeDecision : HFSMDecision
    {
        readonly BotBehaviorAsset    _behavior;
        readonly BotDifficultyAsset[] _diffAssets;

        public ShouldEvadeDecision(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets)
        {
            _behavior   = behavior;
            _diffAssets = diffAssets;
        }

        public override bool Decide(ref AIContext context)
        {
            ref readonly var bot       = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
            ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

            if (bot.EvadeCooldown > 0) return false;

            var diffAsset    = _diffAssets[bot.Difficulty];
            FP64 evadeMargin  = diffAsset.EvadeMargin;
            int  knockbackPct = diffAsset.EvadeKnockbackPct;

            FPVector3 pos = transform.Position;
            bool nearEdge     = FP64.Abs(pos.x) >= _behavior.StageBoundary - evadeMargin
                             || FP64.Abs(pos.z) >= _behavior.StageBoundary - evadeMargin;
            bool highKnockback = character.KnockbackPower >= knockbackPct;

            return nearEdge || highKnockback;
        }
    }

    /// <summary>
    /// Whether the bot has arrived within 1m of the Evade destination.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L194-198.
    /// </summary>
    public class EvadeArrivedDecision : HFSMDecision
    {
        public override bool Decide(ref AIContext context)
        {
            ref readonly var bot       = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
            ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

            if (!bot.HasDestination) return true;

            FPVector3 pos  = transform.Position;
            FPVector3 dest = bot.Destination;
            FP64 dx = pos.x - dest.x;
            FP64 dz = pos.z - dest.z;
            return dx * dx + dz * dz <= FP64.FromInt(1);
        }
    }

    /// <summary>
    /// Whether the entity currently has a KnockbackComponent.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L210.
    /// </summary>
    public class IsKnockbackDecision : HFSMDecision
    {
        public override bool Decide(ref AIContext context)
        {
            return context.Frame.Has<KnockbackComponent>(context.Entity);
        }
    }

    /// <summary>
    /// Within melee range + AttackCooldown == 0.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L220.
    /// </summary>
    public class InAttackRangeDecision : HFSMDecision
    {
        readonly BasicAttackConfigAsset _attack;

        public InAttackRangeDecision(BasicAttackConfigAsset attack)
        {
            _attack = attack;
        }

        public override bool Decide(ref AIContext context)
        {
            ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
            if (bot.AttackCooldown != 0) return false;
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
            if (character.ActionLockTicks > 0) return false;

            var targetRef = bot.Target;
            if (!targetRef.IsValid || !context.Frame.Has<TransformComponent>(targetRef)) return false;

            ref readonly var selfT   = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);
            ref readonly var targetT = ref context.Frame.GetReadOnly<TransformComponent>(targetRef);
            FPVector3 d = targetT.Position - selfT.Position;
            return d.x * d.x + d.z * d.z <= _attack.MeleeRangeSqr;
        }
    }

    /// <summary>
    /// Per-class condition for using a skill.
    /// Delegates to BotNavigationSystem.ShouldUseSkill() L287-344.
    /// </summary>
    public class ShouldUseSkillDecision : HFSMDecision
    {
        // Per-class ranged-slot bitmask (bit0=Slot0, bit1=Slot1)
        // Warrior(0): none / Mage(1): Slot0 / Rogue(2): Slot1 / Knight(3): none
        static readonly int[] RangedSlotMask = { 0b00, 0b01, 0b10, 0b00 };

        readonly BotBehaviorAsset    _behavior;
        readonly BotDifficultyAsset[] _diffAssets;
        readonly SkillConfigAsset[][] _skills;

        public ShouldUseSkillDecision(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets, SkillConfigAsset[][] skills)
        {
            _behavior   = behavior;
            _diffAssets = diffAssets;
            _skills     = skills;
        }

        public override bool Decide(ref AIContext context)
        {
            ref var          bot       = ref context.Frame.Get<BotComponent>(context.Entity);
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
            if (character.ActionLockTicks > 0) return false;
            ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

            var targetRef = bot.Target;
            if (!targetRef.IsValid || !context.Frame.Has<TransformComponent>(targetRef)) return false;

            ref readonly var targetT = ref context.Frame.GetReadOnly<TransformComponent>(targetRef);
            FPVector3 d       = targetT.Position - transform.Position;
            FP64      distSqr = d.x * d.x + d.z * d.z;

            if (!BotFSMHelper.ShouldUseSkill(ref context.Frame, context.Entity,
                                             ref bot, in character,
                                             transform.Position, targetRef, distSqr,
                                             in _behavior, in _diffAssets[bot.Difficulty], _skills))
                return false;

            // Run the LOS check only when a ranged skill is planned to be used
            int classIdx = character.CharacterClass;
            int mask     = (uint)classIdx < (uint)RangedSlotMask.Length ? RangedSlotMask[classIdx] : 0;
            if (mask != 0 && context.RayCaster != null)
            {
                FPVector3 eyeOffset = FPVector3.Up * _behavior.EyeHeight;
                FPVector3 from      = transform.Position + eyeOffset;
                FPVector3 to        = targetT.Position   + eyeOffset;
                FPVector3 dir       = to - from;
                FP64      dist      = dir.magnitude;
                if (dist > FP64.Zero)
                {
                    var ray = new FPRay3(from, dir.normalized);
                    bool losHit = context.RayCaster.RayCastStatic(ray, dist, out _, out _, out FP64 hitDist);
                    if (losHit)
                        return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Whether the bot has a valid target.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L214.
    /// </summary>
    public class HasTargetDecision : HFSMDecision
    {
        public override bool Decide(ref AIContext context)
        {
            ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
            return bot.Target.IsValid;
        }
    }

    /// <summary>
    /// No valid target.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L228.
    /// </summary>
    public class NoTargetDecision : HFSMDecision
    {
        public override bool Decide(ref AIContext context)
        {
            ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
            return !bot.Target.IsValid;
        }
    }

    /// <summary>
    /// End the Skill state once the skill-activation ActionLock has been released.
    /// </summary>
    public class SkillActionDoneDecision : HFSMDecision
    {
        public override bool Decide(ref AIContext context)
        {
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
            return character.ActionLockTicks <= 0;
        }
    }
}
