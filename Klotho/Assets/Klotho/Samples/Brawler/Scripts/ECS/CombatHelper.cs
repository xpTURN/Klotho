using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    public static class CombatHelper
    {
        public static void ApplyKnockback(ref Frame frame, EntityRef target, FPVector2 direction, int basePower)
        {
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

            ref var targetChar = ref frame.Get<CharacterComponent>(target);
            targetChar.KnockbackPower += basePower;

            FP64 forceMag = FP64.FromInt(basePower)
                + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01);
            FPVector2 force = direction * forceMag;

            if (frame.Has<KnockbackComponent>(target))
            {
                ref var kb = ref frame.Get<KnockbackComponent>(target);
                if (force.sqrMagnitude > kb.Force.sqrMagnitude)
                    kb.Force = force;
                kb.InitialDurationTicks = asset.DefaultKnockbackDurationTicks;
                kb.DurationTicks = asset.DefaultKnockbackDurationTicks;
                kb.BlockInput    = true;
            }
            else
            {
                frame.Add(target, new KnockbackComponent
                {
                    Force                = force,
                    InitialDurationTicks = asset.DefaultKnockbackDurationTicks,
                    DurationTicks        = asset.DefaultKnockbackDurationTicks,
                    BlockInput           = true,
                });
            }
        }

        public static void ApplyHitReaction(ref Frame frame, EntityRef target,
            FPVector2 direction, int basePower, int hitReactionTicks)
        {
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

            ref var targetChar = ref frame.Get<CharacterComponent>(target);
            targetChar.KnockbackPower += basePower;
            targetChar.HitReactionTicks = hitReactionTicks;

            FP64 forceMag = (FP64.FromInt(basePower)
                + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01))
                * FP64.FromDouble(0.64);
            FPVector2 force = direction * forceMag;

            if (frame.Has<KnockbackComponent>(target))
            {
                ref var kb = ref frame.Get<KnockbackComponent>(target);
                if (force.sqrMagnitude > kb.Force.sqrMagnitude)
                    kb.Force = force;
                kb.InitialDurationTicks = asset.HitReactionDurationTicks;
                kb.DurationTicks = asset.HitReactionDurationTicks;
            }
            else
            {
                frame.Add(target, new KnockbackComponent { Force = force, InitialDurationTicks = asset.HitReactionDurationTicks, DurationTicks = asset.HitReactionDurationTicks });
            }
        }

        public static void ApplyPush(ref Frame frame, EntityRef target,
            FPVector2 direction, int basePower)
        {
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

            ref var targetChar = ref frame.Get<CharacterComponent>(target);
            targetChar.KnockbackPower += basePower;

            FP64 forceMag = (FP64.FromInt(basePower)
                + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01))
                * FP64.FromDouble(0.64);
            FPVector2 force = direction * forceMag;

            if (frame.Has<KnockbackComponent>(target))
            {
                ref var kb = ref frame.Get<KnockbackComponent>(target);
                if (force.sqrMagnitude > kb.Force.sqrMagnitude)
                    kb.Force = force;
                kb.InitialDurationTicks = asset.PushDurationTicks;
                kb.DurationTicks = asset.PushDurationTicks;
            }
            else
            {
                frame.Add(target, new KnockbackComponent { Force = force, InitialDurationTicks = asset.PushDurationTicks, DurationTicks = asset.PushDurationTicks });
            }
        }

        public static bool IsShielded(ref Frame frame, EntityRef entity)
        {
            if (!frame.Has<SkillCooldownComponent>(entity))
                return false;
            ref readonly var cd = ref frame.GetReadOnly<SkillCooldownComponent>(entity);
            return cd.ShieldTicks > 0;
        }
    }
}
