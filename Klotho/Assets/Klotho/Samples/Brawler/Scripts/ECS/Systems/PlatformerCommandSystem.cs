using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Network;

namespace Brawler
{
    enum HitEffectType { HitReaction, Push }

    /// <summary>
    /// ICommandSystem implementation. Handles 4 commands.
    /// - MoveInputCommand : XZ velocity setting + jump Y velocity
    /// - AttackCommand    : Hit handling within melee range
    /// - UseSkillCommand  : Skill branching per character class
    /// - SpawnCharacterCommand : Create character entity
    /// </summary>
    public class PlatformerCommandSystem : ICommandSystem, IInitSystem, ISyncEventSystem
    {
        readonly EventSystem   _events;

        IPhysicsRayCaster      _rayCaster;
        CharacterStatsAsset[]  _stats;       // index = CharacterClass (0~3)
        SkillConfigAsset[][]   _skills;      // [classIdx][slot]
        BasicAttackConfigAsset _attack;
        MovementPhysicsAsset   _movement;
        ItemConfigAsset        _item;

        public PlatformerCommandSystem(EventSystem events)
        {
            _events = events;
        }

        public void SetRayCaster(IPhysicsRayCaster rayCaster)
        {
            _rayCaster = rayCaster;
        }

        public void OnInit(ref Frame frame)
        {
            _stats = new CharacterStatsAsset[4];
            for (int i = 0; i < 4; i++)
                _stats[i] = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + i);

            _skills = new SkillConfigAsset[4][];
            for (int c = 0; c < 4; c++)
            {
                _skills[c] = new SkillConfigAsset[2];
                _skills[c][0] = frame.AssetRegistry.Get<SkillConfigAsset>(_stats[c].Skill0Id);
                _skills[c][1] = frame.AssetRegistry.Get<SkillConfigAsset>(_stats[c].Skill1Id);
            }

            _attack   = frame.AssetRegistry.Get<BasicAttackConfigAsset>(1301);
            _movement = frame.AssetRegistry.Get<MovementPhysicsAsset>(1500);
            _item     = frame.AssetRegistry.Get<ItemConfigAsset>(1400);
        }

        // ────────────────────────────────────────────
        public void OnCommand(ref Frame frame, ICommand command)
        {
            switch (command)
            {
                case MoveInputCommand move:
                    HandleMove(ref frame, move);
                    break;
                case AttackCommand attack:
                    HandleAttack(ref frame, attack);
                    break;
                case UseSkillCommand skill:
                    HandleSkill(ref frame, skill);
                    break;
                case SpawnCharacterCommand spawn:
                    HandleSpawn(ref frame, spawn);
                    break;
                default:
                    break;
            }
        }

        // ────────────────────────────────────────────
        // MoveInputCommand: XZ velocity setting + jump
        // ────────────────────────────────────────────
        void HandleMove(ref Frame frame, MoveInputCommand cmd)
        {
            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var entity)) return;

            ref var character = ref frame.Get<CharacterComponent>(entity);
            if (character.IsDead) return;

            ref var physics   = ref frame.Get<PhysicsBodyComponent>(entity);
            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);

            int classIdx = character.CharacterClass;
            if ((uint)classIdx >= (uint)_stats.Length) return;
            FP64 speed = _stats[classIdx].MoveSpeed;

            ref readonly var cooldown = ref frame.GetReadOnly<SkillCooldownComponent>(entity);
            if (cooldown.BoostTicks > 0)
                speed = speed * _item.BoostSpeedMultiplier;

            character.InputMagnitude = cmd.HorizontalAxis * cmd.HorizontalAxis + cmd.VerticalAxis * cmd.VerticalAxis;

            bool inputBlocked = frame.Has<KnockbackComponent>(entity)
                && frame.GetReadOnly<KnockbackComponent>(entity).BlockInput;
            inputBlocked |= character.ActionLockTicks > 0;

            if (!inputBlocked)
            {
                physics.RigidBody.velocity.x = cmd.HorizontalAxis * speed;
                physics.RigidBody.velocity.z = cmd.VerticalAxis   * speed;

                if (cmd.JumpPressed && character.IsGrounded)
                {
                    physics.RigidBody.velocity.y = _movement.JumpSpeed;
                    character.IsJumping = true;
                    var jumpEvt = EventPool.Get<JumpEvent>();
                    jumpEvt.Character = entity;
                    _events.Enqueue(jumpEvt);
                }
            }
        }

        // ────────────────────────────────────────────
        // AttackCommand: Apply knockback to enemies within melee range
        // ────────────────────────────────────────────
        void HandleAttack(ref Frame frame, AttackCommand cmd)
        {
            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var attacker)) return;

            ref readonly var attChar  = ref frame.GetReadOnly<CharacterComponent>(attacker);
            if (attChar.IsDead) return;
            if (attChar.ActionLockTicks > 0) return;

            ref readonly var attTrans = ref frame.GetReadOnly<TransformComponent>(attacker);

            FPVector2 aimDir = cmd.AimDirection.sqrMagnitude > FP64.Zero
                            ? cmd.AimDirection.normalized
                            : new FPVector2(FP64.Sin(attTrans.Rotation), FP64.Cos(attTrans.Rotation));

            var actEvt = EventPool.Get<AttackActionEvent>();
            actEvt.Attacker         = attacker;
            actEvt.AttackerPosition = ToXZ(attTrans.Position);
            actEvt.AimDirection     = aimDir;
            _events.Enqueue(actEvt);

            ref var attTransMut = ref frame.Get<TransformComponent>(attacker);
            attTransMut.Rotation = FP64.Atan2(aimDir.x, aimDir.y);

            ref var attCharMut = ref frame.Get<CharacterComponent>(attacker);
            attCharMut.ActionLockTicks = _attack.ActionLockTicks;
            attCharMut.ActiveSkillSlot = -1;

            var filter = frame.Filter<CharacterComponent, TransformComponent>();
            while (filter.Next(out var target))
            {
                if (target == attacker) continue;
                ref readonly var targetChar  = ref frame.GetReadOnly<CharacterComponent>(target);
                if (targetChar.IsDead) continue;

                ref readonly var targetTrans = ref frame.GetReadOnly<TransformComponent>(target);
                FPVector2 diff = ToXZ(targetTrans.Position) - ToXZ(attTrans.Position);

                if (diff.sqrMagnitude > _attack.MeleeRangeSqr) continue;

                FPVector2 hitDir = diff.sqrMagnitude > FP64.Zero ? diff.normalized : aimDir;
                ApplyHit(ref frame, attacker, target, hitDir, _attack.BasePower, HitEffectType.HitReaction);
                var hitEvt = EventPool.Get<AttackHitEvent>();
                hitEvt.Attacker       = attacker;
                hitEvt.Target         = target;
                hitEvt.KnockbackAdded = _attack.BasePower;
                hitEvt.HitPoint       = ToXZ(targetTrans.Position);
                _events.Enqueue(hitEvt);
            }
        }

        // ────────────────────────────────────────────
        // UseSkillCommand: Branch per class
        // ────────────────────────────────────────────
        void HandleSkill(ref Frame frame, UseSkillCommand cmd)
        {
            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var caster)) return;

            ref var character = ref frame.Get<CharacterComponent>(caster);
            if (character.IsDead) return;

            ref var cooldown = ref frame.Get<SkillCooldownComponent>(caster);
            bool slot0 = cmd.SkillSlot == 0;

            if (slot0 && cooldown.Skill0Cooldown > 0) return;
            if (!slot0 && cooldown.Skill1Cooldown > 0) return;

            ref readonly var casterTrans = ref frame.GetReadOnly<TransformComponent>(caster);
            FPVector2 aimDir2 = cmd.AimDirection.sqrMagnitude > FP64.Zero
                            ? cmd.AimDirection.normalized
                            : new FPVector2(FP64.Sin(casterTrans.Rotation), FP64.Cos(casterTrans.Rotation));
            FPVector2 origin2 = ToXZ(casterTrans.Position);

            FPVector2? skillTargetPos = null;
            switch (character.CharacterClass)
            {
                case 0: skillTargetPos = SkillWarrior(ref frame, caster, cmd, origin2, aimDir2); break;
                case 1: skillTargetPos = SkillMage(ref frame, caster, cmd, origin2, aimDir2); break;
                case 2: skillTargetPos = SkillRogue(ref frame, caster, cmd, origin2, aimDir2); break;
                case 3: skillTargetPos = SkillKnight(ref frame, caster, cmd, origin2); break;
            }

            var skillEvt = EventPool.Get<SkillActionEvent>();
            skillEvt.Caster         = caster;
            skillEvt.ClassIndex     = character.CharacterClass;
            skillEvt.SkillSlot      = cmd.SkillSlot;
            skillEvt.CasterPosition = origin2;
            skillEvt.AimDirection   = aimDir2;
            skillEvt.TargetPosition = skillTargetPos ?? (origin2 + aimDir2 * _skills[character.CharacterClass][cmd.SkillSlot].MoveSpeedOrRange);
            _events.Enqueue(skillEvt);

            ref var casterTransMut = ref frame.Get<TransformComponent>(caster);
            casterTransMut.Rotation = FP64.Atan2(aimDir2.x, aimDir2.y);

            int classIdx2 = character.CharacterClass;
            var skillAsset = _skills[classIdx2][cmd.SkillSlot];
            character.ActionLockTicks = skillAsset.ActionLockTicks;
            character.ActiveSkillSlot = cmd.SkillSlot;

            if (slot0) cooldown.Skill0Cooldown = skillAsset.Cooldown;
            else       cooldown.Skill1Cooldown = skillAsset.Cooldown;
        }

        // Warrior — Skill0: Melee circular smash / Skill1: Charge dash
        FPVector2? SkillWarrior(ref Frame frame, EntityRef caster, UseSkillCommand cmd, FPVector2 origin, FPVector2 aimDir)
        {
            if (cmd.SkillSlot == 0)
            {
                var sk = _skills[0][0];
                return AreaHitAllEnemies(ref frame, caster, origin, sk.RangeSqr, sk.KnockbackPower, HitEffectType.HitReaction) ?? origin;
            }
            else
            {
                var sk = _skills[0][1];
                ref var physics = ref frame.Get<PhysicsBodyComponent>(caster);
                physics.RigidBody.velocity.x = aimDir.x * sk.MoveSpeedOrRange;
                physics.RigidBody.velocity.z = aimDir.y * sk.MoveSpeedOrRange;
                var dashEvt = EventPool.Get<DashEvent>();
                dashEvt.Character = caster;
                dashEvt.Direction = aimDir;
                _events.Enqueue(dashEvt);
                return null;
            }
        }

        // Mage — Skill0: (projectile planned, currently ranged impact) / Skill1: Teleport
        FPVector2? SkillMage(ref Frame frame, EntityRef caster, UseSkillCommand cmd, FPVector2 origin, FPVector2 aimDir)
        {
            if (cmd.SkillSlot == 0)
            {
                var sk = _skills[1][0];
                FPVector2 impact = origin + aimDir * sk.MoveSpeedOrRange;
                return AreaHitAllEnemies(ref frame, caster, impact, sk.RangeSqr, sk.KnockbackPower, HitEffectType.HitReaction) ?? impact;
            }
            else
            {
                var sk = _skills[1][1];
                FPVector2 dest = origin + aimDir * sk.MoveSpeedOrRange;
                ref var tMut   = ref frame.Get<TransformComponent>(caster);
                FP64 destY = tMut.Position.y;

                if (_rayCaster != null)
                {
                    ref readonly var phys = ref frame.GetReadOnly<PhysicsBodyComponent>(caster);
                    if (phys.Collider.type == ShapeType.Capsule)
                    {
                        FP64 halfH = phys.Collider.capsule.halfHeight;
                        FP64 r     = phys.Collider.capsule.radius;
                        FP64 probeHeight = _movement.MaxFallProbe;

                        FP64 rayY     = destY + phys.ColliderOffset.y + halfH + r + probeHeight;
                        var rayOrigin = new FPVector3(dest.x, rayY, dest.y);
                        var downRay   = new FPRay3(rayOrigin, -FPVector3.Up);
                        if (_rayCaster.RayCastStatic(downRay, probeHeight + probeHeight, out var hitPt, out _, out _))
                        {
                            FP64 groundY = hitPt.y + halfH + r - phys.ColliderOffset.y;
                            if (groundY > destY)
                                destY = groundY;
                        }
                    }
                }

                tMut.Position  = new FPVector3(dest.x, destY, dest.y);
                tMut.PreviousPosition = tMut.Position;
                tMut.PreviousRotation = tMut.Rotation;
                tMut.TeleportTick = frame.Tick;
                return null;
            }
        }

        // Rogue — Skill0: Short-range dash+strike / Skill1: Thrown dagger (ranged linear hit)
        FPVector2? SkillRogue(ref Frame frame, EntityRef caster, UseSkillCommand cmd, FPVector2 origin, FPVector2 aimDir)
        {
            if (cmd.SkillSlot == 0)
            {
                var sk = _skills[2][0];
                ref var physics = ref frame.Get<PhysicsBodyComponent>(caster);
                physics.RigidBody.velocity.x = aimDir.x * sk.MoveSpeedOrRange;
                physics.RigidBody.velocity.z = aimDir.y * sk.MoveSpeedOrRange;
                var dashEvt = EventPool.Get<DashEvent>();
                dashEvt.Character = caster;
                dashEvt.Direction = aimDir;
                _events.Enqueue(dashEvt);
                FPVector2 dashImpact = origin + aimDir * sk.ImpactOffsetDist;
                return AreaHitAllEnemies(ref frame, caster, dashImpact, sk.RangeSqr, sk.KnockbackPower, HitEffectType.HitReaction) ?? dashImpact;
            }
            else
            {
                var sk = _skills[2][1];
                // Hit the first enemy within the linear range
                var filter = frame.Filter<CharacterComponent, TransformComponent>();
                while (filter.Next(out var target))
                {
                    if (target == caster) continue;
                    ref readonly var tc = ref frame.GetReadOnly<CharacterComponent>(target);
                    if (tc.IsDead) continue;
                    ref readonly var tt = ref frame.GetReadOnly<TransformComponent>(target);
                    FPVector2 diff = ToXZ(tt.Position) - origin;
                    if (diff.sqrMagnitude > sk.RangeSqr) continue;
                    FP64 dot = diff.x * aimDir.x + diff.y * aimDir.y;
                    if (dot < FP64.Zero) continue;
                    ApplyHit(ref frame, caster, target, aimDir, sk.KnockbackPower, HitEffectType.HitReaction);
                    var rogueHitEvt = EventPool.Get<AttackHitEvent>();
                    rogueHitEvt.Attacker       = caster;
                    rogueHitEvt.Target         = target;
                    rogueHitEvt.KnockbackAdded = sk.KnockbackPower;
                    rogueHitEvt.HitPoint       = ToXZ(tt.Position);
                    _events.Enqueue(rogueHitEvt);
                    return ToXZ(tt.Position);
                }
                return null;
            }
        }

        // Knight — Skill0: Shield reflect (absorb hit events) / Skill1: Ground slam
        FPVector2? SkillKnight(ref Frame frame, EntityRef caster, UseSkillCommand cmd, FPVector2 origin)
        {
            if (cmd.SkillSlot == 0)
            {
                var sk = _skills[3][0];
                ref var cooldown = ref frame.Get<SkillCooldownComponent>(caster);
                cooldown.ShieldTicks = sk.AuxDurationTicks;
                return null;
            }
            else
            {
                var sk = _skills[3][1];
                FPVector2 slamCenter = AreaHitAllEnemies(ref frame, caster, origin, sk.RangeSqr, sk.KnockbackPower, HitEffectType.HitReaction) ?? origin;
                var slamEvt = EventPool.Get<GroundSlamEvent>();
                slamEvt.Character = caster;
                slamEvt.Position  = origin;
                slamEvt.Radius    = sk.EffectRadius;
                _events.Enqueue(slamEvt);
                return slamCenter;
            }
        }

        // ────────────────────────────────────────────
        // SpawnCharacterCommand: Create character from prototype
        // ────────────────────────────────────────────
        void HandleSpawn(ref Frame frame, SpawnCharacterCommand cmd)
        {
            frame.Logger?.ZLogDebug($"[Spawn][Recv] tick={frame.Tick}, player={cmd.PlayerId}, class={cmd.CharacterClass}");

            int classId = cmd.CharacterClass;
            if ((uint)classId >= (uint)_stats.Length)
            {
                frame.Logger?.ZLogError($"[Spawn][Reject:InvalidClass] tick={frame.Tick}, player={cmd.PlayerId}, classId={classId} (valid range: 0~{_stats.Length - 1})");
                return;
            }

            // Prevent duplicate creation if a character already exists for this player
            if (TryFindCharacter(ref frame, cmd.PlayerId, out _))
            {
                frame.Logger?.ZLogError($"[Spawn][Reject:Duplicate] tick={frame.Tick}, player={cmd.PlayerId} already has a character");

                var rejectEvt = EventPool.Get<CommandRejectedSimEvent>();
                rejectEvt.PlayerId      = cmd.PlayerId;
                rejectEvt.CommandTypeId = cmd.CommandTypeId;
                rejectEvt.ReasonEnum    = RejectionReason.Duplicate;
                _events.Enqueue(rejectEvt);
                return;
            }

            var entity = frame.CreateEntity(_stats[classId].PrototypeId);
            frame.Add(entity, new ErrorCorrectionTargetComponent());

            ref var character  = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId        = cmd.PlayerId;
            character.StockCount      = 3;

            ref var owner  = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId  = cmd.PlayerId;

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(cmd.SpawnPosition.x, FP64.FromDouble(0.5), cmd.SpawnPosition.y);
            transform.PreviousPosition = transform.Position;
            transform.PreviousRotation = transform.Rotation;

            // Create respawn position marker (referenced by RespawnSystem)
            var marker = frame.CreateEntity();
            frame.Add(marker, new SpawnMarkerComponent
            {
                SpawnPosition = cmd.SpawnPosition,
                PlayerId      = cmd.PlayerId,
            });

            var spawnEvt = EventPool.Get<CharacterSpawnedEvent>();
            spawnEvt.PlayerId       = cmd.PlayerId;
            spawnEvt.CharacterClass = classId;
            _events.Enqueue(spawnEvt);

            frame.Logger?.ZLogInformation($"[Spawn][Commit] tick={frame.Tick}, player={cmd.PlayerId}, class={cmd.CharacterClass}, entity={entity.Index}, version={entity.Version}, pos=({cmd.SpawnPosition.x},{cmd.SpawnPosition.y})");
        }

        // ────────────────────────────────────────────
        // Common helpers
        // ────────────────────────────────────────────

        bool TryFindCharacter(ref Frame frame, int playerId, out EntityRef result)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent, PhysicsBodyComponent, TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId != playerId) continue;
                result = entity;
                return true;
            }
            result = default;
            return false;
        }

        FPVector2? AreaHitAllEnemies(ref Frame frame, EntityRef attacker, FPVector2 center, FP64 radiusSqr, int power, HitEffectType effectType)
        {
            FPVector2 posSum = FPVector2.Zero;
            int hitCount = 0;
            var filter = frame.Filter<CharacterComponent, TransformComponent>();
            while (filter.Next(out var target))
            {
                if (target == attacker) continue;
                ref readonly var tc = ref frame.GetReadOnly<CharacterComponent>(target);
                if (tc.IsDead) continue;
                ref readonly var tt = ref frame.GetReadOnly<TransformComponent>(target);
                FPVector2 diff = ToXZ(tt.Position) - center;
                if (diff.sqrMagnitude > radiusSqr) continue;
                FPVector2 hitDir = diff.sqrMagnitude > FP64.Zero ? diff.normalized : FPVector2.Right;
                ApplyHit(ref frame, attacker, target, hitDir, power, effectType);
                var areaHitEvt = EventPool.Get<AttackHitEvent>();
                areaHitEvt.Attacker       = attacker;
                areaHitEvt.Target         = target;
                areaHitEvt.KnockbackAdded = power;
                areaHitEvt.HitPoint       = ToXZ(tt.Position);
                _events.Enqueue(areaHitEvt);
                posSum += ToXZ(tt.Position);
                hitCount++;
            }
            if (hitCount == 0) return null;
            return posSum * (FP64.One / FP64.FromInt(hitCount));
        }

        void ApplyHit(ref Frame frame, EntityRef attacker, EntityRef target, FPVector2 direction, int basePower, HitEffectType effectType)
        {
            // When shield is active, reflect knockback back to the attacker
            if (CombatHelper.IsShielded(ref frame, target))
            {
                CombatHelper.ApplyPush(ref frame, attacker, -direction, basePower);
                return;
            }

            if (effectType == HitEffectType.HitReaction)
                CombatHelper.ApplyHitReaction(ref frame, target, direction, basePower, hitReactionTicks: _attack.HitStunTicks);
            else
                CombatHelper.ApplyPush(ref frame, target, direction, basePower);
        }

        public void EmitSyncEvents(ref Frame frame)
        {
            if (frame.EventRaiser == null) return;

            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                var evt = EventPool.Get<CharacterSpawnedEvent>();
                evt.PlayerId = character.PlayerId;
                evt.CharacterClass = character.CharacterClass;
                frame.EventRaiser.RaiseEvent(evt);
            }
        }

        static FPVector2 ToXZ(FPVector3 v) => new FPVector2(v.x, v.z);

    }
}
