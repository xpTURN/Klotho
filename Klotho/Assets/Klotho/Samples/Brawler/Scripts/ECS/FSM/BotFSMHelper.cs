using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    /// <summary>
    /// Pure-logic helper extracted from BotNavigationSystem.
    /// Shared by BotFSMSystem, Decision, and Action classes.
    /// </summary>
    public static class BotFSMHelper
    {
        // ── ValidateTarget ────────────────────────────────────────────────────

        /// <summary>
        /// Target validity check. Resets Target to None if the target is dead or no longer exists.
        /// Corresponds to BotNavigationSystem.UpdateDecision() L152-163.
        /// </summary>
        public static void ValidateTarget(ref Frame frame, ref BotComponent bot)
        {
            var target = bot.Target;
            if (!target.IsValid) return;

            bool valid = frame.Has<CharacterComponent>(target)
                      && !frame.GetReadOnly<CharacterComponent>(target).IsDead;
            if (!valid)
                bot.Target = EntityRef.None;
        }

        // ── SelectTarget ──────────────────────────────────────────────────────

        /// <summary>
        /// Select the best target when there is no valid one.
        /// Corresponds to BotNavigationSystem.SelectTarget() L362-403.
        /// </summary>
        public static EntityRef SelectTarget(ref Frame frame, EntityRef self,
                                             in CharacterComponent selfChar,
                                             FPVector3 selfPos, BotDifficulty difficulty,
                                             in BotBehaviorAsset behavior)
        {
            EntityRef best      = EntityRef.None;
            FP64      bestScore = FP64.MaxValue;

            var filter = frame.Filter<TransformComponent, CharacterComponent>();
            while (filter.Next(out var candidate))
            {
                if (candidate == self) continue;
                ref readonly var cChar = ref frame.GetReadOnly<CharacterComponent>(candidate);
                if (cChar.IsDead) continue;
                if (cChar.PlayerId == selfChar.PlayerId) continue;

                ref readonly var cT = ref frame.GetReadOnly<TransformComponent>(candidate);
                FPVector3 d      = cT.Position - selfPos;
                FP64      distSqr = d.x * d.x + d.z * d.z;

                FP64 score;
                switch (difficulty)
                {
                    case BotDifficulty.Normal:
                        score = distSqr - behavior.TargetScoreKnockbackFactor * FP64.FromInt(cChar.KnockbackPower);
                        break;
                    case BotDifficulty.Hard:
                        score = distSqr - behavior.TargetScoreKnockbackFactor * FP64.FromInt(cChar.KnockbackPower)
                              + behavior.TargetScoreStockFactor * FP64.FromInt(cChar.StockCount);
                        break;
                    default: // Easy
                        score = distSqr;
                        break;
                }

                if (score < bestScore || (score == bestScore && candidate.Index < best.Index))
                {
                    bestScore = score;
                    best      = candidate;
                }
            }

            return best;
        }

        // ── UpdateDestination ─────────────────────────────────────────────────

        /// <summary>
        /// Update the destination according to the current state.
        /// Corresponds to the switch block in BotNavigationSystem.UpdateDecision() L252-284.
        /// </summary>
        public static void UpdateDestination(ref Frame frame, EntityRef entity,
                                             ref BotComponent bot,
                                             in CharacterComponent character,
                                             FPNavMeshQuery query,
                                             in BotBehaviorAsset behavior,
                                             ILogger logger = null)
        {
            bool prevHasDest = bot.HasDestination;
            int leafStateId = HFSMManager.GetLeafStateId(ref frame, entity);
            switch (leafStateId)
            {
                case BotStateId.Chase:
                    var target = bot.Target;
                    if (target.IsValid && frame.Has<TransformComponent>(target))
                    {
                        ref readonly var selfT   = ref frame.GetReadOnly<TransformComponent>(entity);
                        FPVector3 targetPos      = frame.GetReadOnly<TransformComponent>(target).Position;

                        // Stop moving when the distance to the target is within ChaseStopDistance.
                        // Prevents the physics-collision rubbing that occurs even when the Attack
                        // transition (InAttackRangeDecision) is suppressed by cooldown/ActionLock.
                        FPVector3 diff = targetPos - selfT.Position;
                        FP64 distSqr = diff.x * diff.x + diff.z * diff.z;
                        if (distSqr <= behavior.ChaseStopDistance * behavior.ChaseStopDistance)
                        {
                            bot.HasDestination = false;
                        }
                        else
                        {
                            FPVector3 snapped = SnapDestination(targetPos, selfT.Position, query,
                                                                behavior.NavSnapMaxDist,
                                                                out bool ok, character.PlayerId, "Chase");
                            bot.Destination    = snapped;
                            bot.HasDestination = ok;
                        }
                    }
                    break;

                case BotStateId.Evade:
                    // The destination is decided once in EvadeEnterAction.
                    break;

                case BotStateId.Attack:
                case BotStateId.Skill:
                case BotStateId.Idle:
                    bot.HasDestination = false;
                    break;
            }
        }

        // ── SnapDestination ───────────────────────────────────────────────────

        /// <summary>
        /// Snap the destination onto the NavMesh.
        /// Corresponds to BotNavigationSystem.SnapDestination() L585-618.
        /// </summary>
        public static FPVector3 SnapDestination(FPVector3 desired, FPVector3 fallbackPos,
                                                FPNavMeshQuery query,
                                                FP64 navSnapMaxDist,
                                                out bool snapOk,
                                                int playerId = -1, string context = null,
                                                ILogger logger = null)
        {
            if (query == null)
            {
                snapOk = true;
                return desired;
            }

            FPVector2 desiredXZ = new FPVector2(desired.x, desired.z);

            int onMeshTri = query.FindTriangle(desiredXZ);
            if (onMeshTri >= 0)
            {
                snapOk = true;
                return desired;
            }

            FPVector2 snapped = query.ProjectToNavMesh(desiredXZ, navSnapMaxDist, out int triIdx);
            if (triIdx >= 0)
            {
                FP64 height = query.SampleHeight(snapped, triIdx);
                snapOk = true;
                return new FPVector3(snapped.x, height, snapped.y);
            }

            logger?.ZLogWarning($"[BotFSM] P{playerId} {context}: SnapFailed | desired=({desired.x.ToFloat():F2},{desired.z.ToFloat():F2}) self=({fallbackPos.x.ToFloat():F2},{fallbackPos.z.ToFloat():F2})");
            snapOk = false;
            return desired;
        }

        // ── PickEvadePoint ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the safe point farthest from the current position.
        /// Corresponds to BotNavigationSystem.PickEvadePoint() L568-583.
        /// </summary>
        public static FPVector3 PickEvadePoint(FPVector3 position, in BotBehaviorAsset behavior)
        {
            var points = behavior.EvadePoints;
            FP64 bestDistSqr = FP64.FromInt(-1);
            int  bestIdx     = 0;
            for (int i = 0; i < points.Length; i++)
            {
                FPVector3 d      = points[i] - position;
                FP64      distSqr = d.x * d.x + d.z * d.z;
                if (distSqr > bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestIdx     = i;
                }
            }
            return new FPVector3(points[bestIdx].x, position.y, points[bestIdx].z);
        }

        // ── ShouldUseSkill ────────────────────────────────────────────────────

        /// <summary>
        /// Decide the skill-use condition per class.
        /// Corresponds to BotNavigationSystem.ShouldUseSkill() L287-344.
        /// </summary>
        public static bool ShouldUseSkill(ref Frame frame, EntityRef entity, ref BotComponent bot,
                                          in CharacterComponent character,
                                          FPVector3 position, EntityRef target, FP64 distSqr,
                                          in BotBehaviorAsset behavior,
                                          in BotDifficultyAsset diffAsset,
                                          SkillConfigAsset[][] skills)
        {
            if (!frame.Has<SkillCooldownComponent>(entity)) return false;
            ref readonly var cooldown  = ref frame.GetReadOnly<SkillCooldownComponent>(entity);

            byte diff       = bot.Difficulty;
            int  extraDelay = diffAsset.SkillExtraDelay;
            int  classIdx   = character.CharacterClass;

            switch (classIdx)
            {
                case 0: // Warrior
                    if (cooldown.Skill0Cooldown <= extraDelay && distSqr <= skills[0][0].RangeSqr)
                        return true;
                    if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay)
                    {
                        ref readonly var targetT = ref frame.GetReadOnly<TransformComponent>(target);
                        FPVector3 dashDest = targetT.Position;
                        if (FP64.Abs(dashDest.x) < behavior.StageBoundary && FP64.Abs(dashDest.z) < behavior.StageBoundary)
                            return true;
                    }
                    return false;

                case 1: // Mage
                {
                    var sk0 = skills[1][0];
                    FP64 mageOffset = sk0.MoveSpeedOrRange - FP64.FromInt(2);
                    FP64 mageMinSqr = mageOffset * mageOffset;
                    FP64 mageRange  = sk0.MoveSpeedOrRange + FP64.FromInt(2);
                    FP64 mageMaxSqr = mageRange * mageRange;
                    if (cooldown.Skill0Cooldown <= extraDelay && distSqr >= mageMinSqr && distSqr <= mageMaxSqr)
                        return true;
                    if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay)
                    {
                        bool inDanger = FP64.Abs(position.x) >= behavior.StageBoundary - diffAsset.EvadeMargin
                                     || FP64.Abs(position.z) >= behavior.StageBoundary - diffAsset.EvadeMargin
                                     || character.KnockbackPower >= diffAsset.EvadeKnockbackPct;
                        if (inDanger) return true;
                    }
                    return false;
                }

                case 2: // Rogue
                {
                    var sk0 = skills[2][0];
                    if (cooldown.Skill0Cooldown <= extraDelay && distSqr >= sk0.RangeSqr && distSqr <= skills[2][1].RangeSqr / FP64.FromInt(4))
                        return true;
                    if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay && distSqr <= skills[2][1].RangeSqr)
                        return true;
                    return false;
                }

                case 3: // Knight
                    if (cooldown.Skill0Cooldown <= extraDelay && cooldown.ShieldTicks == 0 && distSqr <= skills[0][0].RangeSqr * FP64.FromInt(2))
                        return true;
                    if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay)
                    {
                        int nearCount = CountNearEnemies(ref frame, entity, character.PlayerId, position, skills[3][1].RangeSqr);
                        if (nearCount >= 2) return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        // ── SelectSkillSlot ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the skill-slot number to activate per class/difficulty. Returns -1 if activation is not possible.
        /// Corresponds to BotNavigationSystem.SelectSkillSlot() L500-566.
        /// </summary>
        public static int SelectSkillSlot(ref Frame frame, EntityRef entity,
                                          ref BotComponent bot, in CharacterComponent character,
                                          FPVector3 position, EntityRef target,
                                          in SkillCooldownComponent cooldown,
                                          byte diff, int extraDelay, int classIdx,
                                          in BotBehaviorAsset behavior,
                                          in BotDifficultyAsset diffAsset,
                                          SkillConfigAsset[][] skills)
        {
            FP64 distSqr = FP64.Zero;
            if (frame.Has<TransformComponent>(target))
            {
                ref readonly var targetT = ref frame.GetReadOnly<TransformComponent>(target);
                FPVector3 d = targetT.Position - position;
                distSqr = d.x * d.x + d.z * d.z;
            }

            switch (classIdx)
            {
                case 0: // Warrior
                    if (diff == (byte)BotDifficulty.Hard)
                    {
                        if (cooldown.Skill1Cooldown <= extraDelay) return 1;
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                    }
                    else
                    {
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                        if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay) return 1;
                    }
                    return -1;

                case 1: // Mage
                {
                    var sk0 = skills[1][0];
                    FP64 mageOffset = sk0.MoveSpeedOrRange - FP64.FromInt(2);
                    FP64 mageMinSqr = mageOffset * mageOffset;
                    FP64 mageRange  = sk0.MoveSpeedOrRange + FP64.FromInt(2);
                    FP64 mageMaxSqr = mageRange * mageRange;
                    if (diff == (byte)BotDifficulty.Hard)
                    {
                        bool inDanger = FP64.Abs(position.x) >= behavior.StageBoundary - diffAsset.EvadeMargin
                                     || FP64.Abs(position.z) >= behavior.StageBoundary - diffAsset.EvadeMargin
                                     || character.KnockbackPower >= diffAsset.EvadeKnockbackPct;
                        if (inDanger && cooldown.Skill1Cooldown <= extraDelay) return 1;
                        if (cooldown.Skill0Cooldown <= extraDelay && distSqr >= mageMinSqr && distSqr <= mageMaxSqr) return 0;
                    }
                    else
                    {
                        if (cooldown.Skill0Cooldown <= extraDelay && distSqr >= mageMinSqr && distSqr <= mageMaxSqr) return 0;
                        if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay) return 1;
                    }
                    return -1;
                }

                case 2: // Rogue
                    if (diff == (byte)BotDifficulty.Hard)
                    {
                        if (cooldown.Skill1Cooldown <= extraDelay) return 1;
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                    }
                    else
                    {
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                        if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay) return 1;
                    }
                    return -1;

                case 3: // Knight
                    if (diff == (byte)BotDifficulty.Hard)
                    {
                        int nearCount = CountNearEnemies(ref frame, entity, character.PlayerId, position, skills[3][1].RangeSqr);
                        if (nearCount >= 2 && cooldown.Skill1Cooldown <= extraDelay) return 1;
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                    }
                    else
                    {
                        if (cooldown.Skill0Cooldown <= extraDelay) return 0;
                        if (diff >= (byte)BotDifficulty.Normal && cooldown.Skill1Cooldown <= extraDelay) return 1;
                    }
                    return -1;

                default:
                    return -1;
            }
        }

        // ── CountNearEnemies ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the number of enemies within the specified range.
        /// Corresponds to BotNavigationSystem.CountNearEnemies() L346-360.
        /// </summary>
        public static int CountNearEnemies(ref Frame frame, EntityRef self, int selfPlayerId,
                                           FPVector3 position, FP64 rangeSqr)
        {
            int count  = 0;
            var filter = frame.Filter<TransformComponent, CharacterComponent>();
            while (filter.Next(out var candidate))
            {
                if (candidate == self) continue;
                ref readonly var cChar = ref frame.GetReadOnly<CharacterComponent>(candidate);
                if (cChar.IsDead || cChar.PlayerId == selfPlayerId) continue;
                ref readonly var cT = ref frame.GetReadOnly<TransformComponent>(candidate);
                FPVector3 d = cT.Position - position;
                if (d.x * d.x + d.z * d.z <= rangeSqr) count++;
            }
            return count;
        }
    }
}
