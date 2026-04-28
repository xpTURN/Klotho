using ZLogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    /// <summary>
    /// On entering the Evade state, determines the evade point and sets the cooldown.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L238-246.
    /// </summary>
    public class EvadeEnterAction : AIAction
    {
        readonly BotBehaviorAsset _behavior;

        public EvadeEnterAction(BotBehaviorAsset behavior)
        {
            _behavior = behavior;
        }

        public override void Execute(ref AIContext context)
        {
            ref var          bot       = ref context.Frame.Get<BotComponent>(context.Entity);
            ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);

            var evadeRaw     = BotFSMHelper.PickEvadePoint(transform.Position, in _behavior);
            var evadeSnapped = BotFSMHelper.SnapDestination(evadeRaw, transform.Position,
                                                            context.NavQuery,
                                                            _behavior.NavSnapMaxDist,
                                                            out bool ok,
                                                            character.PlayerId, "Evade");
            bot.Destination    = evadeSnapped;
            bot.HasDestination = ok;
            bot.EvadeCooldown  = _behavior.EvadeCooldownTicks;
        }
    }

    /// <summary>
    /// Clears the destination. Used when entering the Idle, Attack, or Skill state.
    /// Corresponds to BotNavigationSystem.UpdateDecision() L282.
    /// </summary>
    public class ClearDestinationAction : AIAction
    {
        public override void Execute(ref AIContext context)
        {
            ref var bot = ref context.Frame.Get<BotComponent>(context.Entity);
            bot.HasDestination = false;
        }
    }

    /// <summary>
    /// Skill state OnUpdate: issues the skill command.
    /// Emits the command in OnUpdate before the FSM transition to prevent the missing-command bug.
    /// Corresponds to BotNavigationSystem.EmitSkillCommand() L461-498.
    /// </summary>
    public class SkillUpdateAction : AIAction
    {
        readonly BotDifficultyAsset[] _diffAssets;
        readonly BotBehaviorAsset     _behavior;
        readonly SkillConfigAsset[][] _skills;

        readonly UseSkillCommand _skillCmd = new UseSkillCommand();

        public SkillUpdateAction(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets, SkillConfigAsset[][] skills)
        {
            _behavior   = behavior;
            _diffAssets = diffAssets;
            _skills     = skills;
        }

        public override void Execute(ref AIContext context)
        {
            if (!context.Frame.Has<SkillCooldownComponent>(context.Entity)) return;

            ref var          bot       = ref context.Frame.Get<BotComponent>(context.Entity);
            ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
            ref readonly var cooldown  = ref context.Frame.GetReadOnly<SkillCooldownComponent>(context.Entity);
            ref readonly var selfT     = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

            var targetRef = bot.Target;

            FPVector2 aimDir = FPVector2.Zero;
            if (targetRef.IsValid && context.Frame.Has<TransformComponent>(targetRef))
            {
                ref readonly var targetT = ref context.Frame.GetReadOnly<TransformComponent>(targetRef);
                FPVector3 d   = targetT.Position - selfT.Position;
                FP64      len = FP64.Sqrt(d.x * d.x + d.z * d.z);
                if (len > FP64.Zero)
                    aimDir = new FPVector2(d.x / len, d.z / len);
            }

            byte diff       = bot.Difficulty;
            var  diffAsset  = _diffAssets[diff];
            int  extraDelay = diffAsset.SkillExtraDelay;
            int  classIdx   = character.CharacterClass;

            int slot = BotFSMHelper.SelectSkillSlot(ref context.Frame, context.Entity,
                                                    ref bot, in character,
                                                    selfT.Position, targetRef,
                                                    in cooldown, diff, extraDelay, classIdx,
                                                    in _behavior, in diffAsset, _skills);
            if (slot < 0) return;

            // Mage Skill1: danger evasion — teleport in the opposite direction
            if (classIdx == 1 && slot == 1)
                aimDir = new FPVector2(-aimDir.x, -aimDir.y);

            if (aimDir == FPVector2.Zero)
                aimDir = new FPVector2(FP64.Sin(selfT.Rotation), FP64.Cos(selfT.Rotation));

            _skillCmd.PlayerId     = character.PlayerId;
            _skillCmd.SkillSlot    = slot;
            _skillCmd.AimDirection = aimDir;
            context.CommandSystem.OnCommand(ref context.Frame, _skillCmd);
        }
    }
}
