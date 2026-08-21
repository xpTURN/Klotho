using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Network;

namespace Brawler
{
    enum HitEffectType { HitReaction, Push }

    /// <summary>
    /// ICommandSystem implementation.
    /// - PlayerInputCommand : unified per-tick input — HandleMove (XZ velocity + jump),
    ///   HandleAttack (melee knockback), HandleSkill (per-class branching) by Buttons bits
    /// - SpawnCharacterCommand : Create character entity
    /// - StopCommand : zero XZ velocity (pause)
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

        // Rebake context — per-stage snapshot plus this
        // room's work buffers (null = building placement unavailable for this stage), botFSM
        // executes Swap+Reseed.
        FPNavMeshRebakeContext _rebakeContext;
        BotFSMSystem _botFSM;

        // The active-set derivation, the canonical order and the two audits that make that order
        // safe now live in the core (FPNavMeshPlacementValidator) over the SAME table the driver
        // reads. This system used to carry its own copy of all of it — and a copy of two audits
        // whose whole reason for existing is that a game is expected to get the derivation wrong.
        FPNavMeshPlacementValidator _validator;

        public void SetRebakeContext(
            FPNavMeshRebakeContext context, BotFSMSystem botFSM,
            FPNavMeshPlacementValidator validator = null)
        {
            _rebakeContext = context;
            _botFSM = botFSM;
            _validator = validator;
        }

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

            _attack   = frame.AssetRegistry.Get<BasicAttackConfigAsset>();
            _movement = frame.AssetRegistry.Get<MovementPhysicsAsset>();
            _item     = frame.AssetRegistry.Get<ItemConfigAsset>();
        }

        // ────────────────────────────────────────────
        public void OnCommand(ref Frame frame, ICommand command)
        {
            switch (command)
            {
                case PlayerInputCommand input:
                    // Single per-tick input: move/jump, then attack, then skill (order is load-bearing —
                    // HandleMove checks ActionLock before HandleAttack/HandleSkill set it). HandleMove only
                    // when the command carries movement intent (HasMove) so a skill/attack-only command
                    // does not zero the velocity. Attack and skill may both fire in the same tick.
                    if (input.HasMove)
                        HandleMove(ref frame, input);
                    if (input.Attack)
                        HandleAttack(ref frame, input);
                    if (input.HasSkill)
                        HandleSkill(ref frame, input);
                    break;
                case SpawnCharacterCommand spawn:
                    HandleSpawn(ref frame, spawn);
                    break;

                case PlaceBuildingCommand place:
                    HandlePlaceBuilding(ref frame, place);
                    break;

                case PlaceHexBuildingCommand placeHex:
                    HandlePlaceHexBuilding(ref frame, placeHex);
                    break;

                case RemoveBuildingCommand remove:
                    HandleRemoveBuilding(ref frame, remove);
                    break;
                case UseConsumableCommand consume:
                    HandleUseConsumable(ref frame, consume);
                    break;
                case StopCommand stop:
                    HandleStop(ref frame, stop);
                    break;
                default:
                    break;
            }
        }

        // ────────────────────────────────────────────
        // StopCommand: zero XZ velocity and clear input magnitude
        // ────────────────────────────────────────────
        void HandleStop(ref Frame frame, StopCommand cmd)
        {
            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var entity)) return;

            ref var character = ref frame.Get<CharacterComponent>(entity);
            if (character.IsDead) return;

            bool inputBlocked = frame.Has<KnockbackComponent>(entity)
                && frame.GetReadOnly<KnockbackComponent>(entity).BlockInput;
            inputBlocked |= character.ActionLockTicks > 0;
            if (inputBlocked) return;

            ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);
            physics.RigidBody.velocity.x = FP64.Zero;
            physics.RigidBody.velocity.z = FP64.Zero;
            character.InputMagnitude = FP64.Zero;
        }

        // ────────────────────────────────────────────
        // HandleMove: XZ velocity setting + jump
        // ────────────────────────────────────────────
        void HandleMove(ref Frame frame, PlayerInputCommand cmd)
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
        // HandleAttack: Apply knockback to enemies within melee range
        // ────────────────────────────────────────────
        void HandleAttack(ref Frame frame, PlayerInputCommand cmd)
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
        // HandleSkill: Branch per class
        // ────────────────────────────────────────────
        void HandleSkill(ref Frame frame, PlayerInputCommand cmd)
        {
            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var caster)) return;

            ref var character = ref frame.Get<CharacterComponent>(caster);
            if (character.IsDead) return;

            // The skill slot must be acquired in the entitlement loadout (AcquiredSkillMask, set at spawn).
            // Unacquired -> no-op (no cooldown/effect). This is a deterministic check against simulation
            // state — the mask rides the tick-0 snapshot, so every peer decides identically with no network
            // gate (the skill input still arrives as normal per-tick input; only its effect is gated here).
            if ((character.AcquiredSkillMask & (1 << cmd.SkillSlot)) == 0)
            {
                frame.Logger?.KDebug($"[Skill][Skip:NotAcquired] tick={frame.Tick}, player={cmd.PlayerId}, slot={cmd.SkillSlot}");
                return;
            }

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
        FPVector2? SkillWarrior(ref Frame frame, EntityRef caster, PlayerInputCommand cmd, FPVector2 origin, FPVector2 aimDir)
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
        FPVector2? SkillMage(ref Frame frame, EntityRef caster, PlayerInputCommand cmd, FPVector2 origin, FPVector2 aimDir)
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
                tMut.TeleportTick = frame.Tick;
                frame.RefreshPreviousTransform(caster);
                return null;
            }
        }

        // Rogue — Skill0: Short-range dash+strike / Skill1: Thrown dagger (ranged linear hit)
        FPVector2? SkillRogue(ref Frame frame, EntityRef caster, PlayerInputCommand cmd, FPVector2 origin, FPVector2 aimDir)
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
        FPVector2? SkillKnight(ref Frame frame, EntityRef caster, PlayerInputCommand cmd, FPVector2 origin)
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
        // Building place/remove — deterministic rebake at the command tick.
        // Trial rebake IS the placement validation (Rebaker throws on overlap/boundary/
        // outside-walkable — no duplicated validation logic); on success the trial result
        // mesh is used directly (single rebake). Rejection = event + no state change
        // (the rejection verdict is exact-integer, identical on every peer).
        // ────────────────────────────────────────────
        // The GAME POLICY cap — how many buildings this sample lets players stand up. Enforced by
        // the `count >= MaxBuildings` rejection below. A separate concept from the ENGINE STORAGE
        // cap (BuildingComponent's MaxCount): a future build could lower the policy without
        // lowering storage, so these must not be collapsed into one number.
        //
        // Buffers are neither concept — they are DERIVED, each from whichever one it serves. The
        // command paths below serve the policy and size off this; the join-time collect must hold
        // every building that can exist in the frame and sizes off BuildingSlotCapacity instead.
        const int MaxBuildings = 32;

        /// <summary>
        /// Ticks between a placement being accepted and the navmesh changing — the build time, as
        /// a game-design number rather than a derived one.
        ///
        /// <para>Deriving it from the rebake's work would put a performance figure into the
        /// determinism envelope: the swap tick is frame state, so every peer has to agree on it,
        /// and a work estimate is not something the wire carries. A constant sidesteps that
        /// entirely — pacing the slices is then a purely local matter.</para>
        ///
        /// <para><b>It must exceed MaxRollbackTicks</b> (50 by default), for two reasons that
        /// happen to give the same bound. A client only learns of a placement when it re-executes
        /// the verified tick, by which point its own head is already lead ticks ahead, so a delay
        /// inside that window buys no slicing room at all. And with consecutive swap boundaries at
        /// least K apart, no rollback window can contain two of them — which is what lets the
        /// derived cache hold exactly two mesh states instead of a history.</para>
        ///
        /// <para>Folded into the game fingerprint (BotFSMSystem): no state hash covers it, so two
        /// builds disagreeing here would swap on different ticks and diverge quietly.</para>
        /// </summary>
        public const int BuildDelayTicks = 60;

        /// <summary>
        /// Engine storage bound for <see cref="BuildingComponent"/>, read from the frozen layout
        /// rather than transcribed from the attribute. Reading it means a buffer sized off this
        /// cannot fall out of step with a raised MaxCount — which is exactly how the join path used
        /// to end up rebaking a strict subset while everyone else rebaked the whole set.
        /// </summary>
        public static int BuildingSlotCapacity =>
            ComponentStorageRegistry.GetLayout(
                ComponentStorageRegistry.GetTypeId<BuildingComponent>()).SlotCapacity;

        /// <summary>
        /// Brawler lets players pack buildings wall-to-wall (shared edge / shared corner); only
        /// overlapping interiors are refused. ONE source for every rebake in the sample — the
        /// trial rebake below IS the validation, and the join-time rebake replays the set it
        /// accepted, so if the two used different rules a rejoining peer would throw on a
        /// placement the placing peer had accepted, and the mismatch would surface as a load
        /// failure rather than as a rejection.
        ///
        /// Same reason it is a constant and not a setting: it must be identical on every peer and
        /// in every replay. A build that flipped it would diverge from recordings made before —
        /// for the boundary axis specifically, a recording that contains a placement REJECTED
        /// under the old policy replays as accepted under the new one, so flipping this orphans
        /// exactly those recordings (accepted placements all replay unchanged).
        ///
        /// The walkable-BOUNDARY axis is ClipOverlap: a footprint may hang past any wall at any
        /// angle — it is clipped to the walkable region and the clipped shape is carved, which is
        /// what lets a placement seal a corridor whose walls have no lattice point to sit flush
        /// against (Docs/IMP/IMP96/Plan-BoundaryOverlapClip.md). Neighbouring wall runs merge into
        /// one obstacle; the clip-specific refusals are all deterministic and worded in
        /// Describe below.
        /// </summary>
        public static readonly FPBuildingPlacementRules PlacementRules =
            new FPBuildingPlacementRules(allowBuildingTouch: true, FPBoundaryPlacementPolicy.ClipOverlap);

        /// <summary>Log-friendly shape name — a bare pair of indices would make the log unreadable
        /// once the catalog holds two families.</summary>
        static string ShapeName(int shape, int orientation)
        {
            return shape == BrawlerBuildingShapes.HexShape
                ? "hex"
                : $"box@{orientation}/{BrawlerBuildingShapes.Directions}";
        }

        /// <summary>
        /// Player-facing text for a refused placement.
        ///
        /// <para>This method is the point of the whole change. Before the rebaker reported a
        /// reason, the only thing a game had was an English sentence built inside the engine, so
        /// every refusal collapsed into one — telling a player "invalid placement" whether they
        /// had overlapped a neighbour, hugged a wall, or aimed off the map. Branching on a value
        /// is what lets the four cases say four different things, and a localised build would
        /// switch on the same enum instead of matching text.</para>
        ///
        /// <para>The wire-level <c>RejectionReason</c> stays <c>InvalidPlacement</c> for all of
        /// them: that enum has fixed byte values and adding to it is a protocol change, which is
        /// a separate decision from being able to tell the cases apart locally.</para>
        /// </summary>
        static string Describe(in FPBuildingRejectionInfo r)
        {
            switch (r.Reason)
            {
                case FPBuildingRejection.BuildingsOverlap:
                    // Deliberately "too close" rather than "overlapping": with touching forbidden
                    // this also fires on exact contact, and the separating-axis test answers one
                    // bool — it cannot tell the two apart, so neither should the wording.
                    return $"too close to another building (#{r.IndexA} and #{r.IndexB})";
                case FPBuildingRejection.TouchesWalkableBoundary:
                    return "too close to the edge of the walkable area";
                case FPBuildingRejection.OutsideWalkableRegion:
                    // Under ClipOverlap this verdict means "clips to nothing" — the footprint
                    // never reaches the walkable area at all, not merely "centre is off-mesh".
                    return "not on the walkable area";
                case FPBuildingRejection.SwallowsBakedHole:
                    return $"would cover a gap in the ground at "
                        + $"({r.Site.x.ToDouble():F1}, {r.Site.y.ToDouble():F1})";
                // ClipOverlap refusals. All deterministic, all fixable by nudging the ghost —
                // the wording tells the player to move rather than naming the geometry.
                case FPBuildingRejection.ExactBoundaryContact:
                    return "resting exactly on the edge of the walkable area — move it a little";
                case FPBuildingRejection.ClipCandidateMissing:
                    return "barely grazes the walkable area — move it further in or out";
                case FPBuildingRejection.ClipSplitsWalkableRegion:
                case FPBuildingRejection.ClipRunsInterleave:
                    return "the ground under it is too broken up to build across";
                default:
                    return $"placement refused ({r.Reason})";
            }
        }

        void RejectBuilding(ref Frame frame, CommandBase cmd, string log)
        {
            frame.Logger?.KWarning($"[Building][Reject] tick={frame.Tick}, player={cmd.PlayerId}: {log}");
            var rejectEvt = EventPool.Get<CommandRejectedSimEvent>();
            rejectEvt.PlayerId      = cmd.PlayerId;
            rejectEvt.CommandTypeId = cmd.CommandTypeId;
            rejectEvt.ReasonEnum    = RejectionReason.InvalidPlacement;
            _events.Enqueue(rejectEvt);
        }

        void HandlePlaceBuilding(ref Frame frame, PlaceBuildingCommand cmd)
        {
            // Both indices arrive from the network, so both are untrusted. Checked here rather than
            // in the shared body because it is the one thing the two place commands do NOT have in
            // common — the hexagon command carries no indices at all (BrawlerBuildingShapes).
            if (!BrawlerBuildingShapes.IsValidPlacement(cmd.ShapeId, cmd.Orientation))
            {
                RejectBuilding(ref frame, cmd,
                    $"shape {cmd.ShapeId} turned {cmd.Orientation} is not in the shape catalog");
                return;
            }
            PlaceBuildingAt(ref frame, cmd, cmd.ShapeId, cmd.Orientation, cmd.Centre);
        }

        void HandlePlaceHexBuilding(ref Frame frame, PlaceHexBuildingCommand cmd)
        {
            // The shape is a constant, not payload — a hexagon has no orientation to send, so there
            // is nothing here to validate and nothing on the wire that could be wrong.
            PlaceBuildingAt(ref frame, cmd, BrawlerBuildingShapes.HexShape, 0, cmd.Centre);
        }

        /// <summary>
        /// The half of placement both commands share: grid check, cap, trial rebake (which IS the
        /// validation), component, swap. Shared rather than duplicated because the trial rebake and
        /// the join-time rebake have to accept exactly the same set — two copies of this that
        /// drifted would show up as a rejoining peer throwing on a placement the placing peer had
        /// accepted, which surfaces as a load failure and not as a rejection.
        /// </summary>
        void PlaceBuildingAt(
            ref Frame frame, CommandBase cmd, int shapeId, int orientation, FPVector3 centre)
        {
            if (_rebakeContext == null || _botFSM == null)
            {
                RejectBuilding(ref frame, cmd, "rebake unavailable on this stage");
                return;
            }

            // Before the trial rebake: an off-grid centre would throw inside it and come back as
            // "invalid placement", sending whoever reads the log to look at the position when the
            // problem is the quantisation.
            if (!FPGeoPredicates.IsOnGrid(centre.x) || !FPGeoPredicates.IsOnGrid(centre.z))
            {
                RejectBuilding(ref frame, cmd, "building centre is not on the predicate snap grid");
                return;
            }

            // Validated against the set as it will be WHEN THIS LANDS, not as it is now. Checking
            // against the present set would let two buildings queued in the same window pass
            // without seeing each other, and both would then land on a mesh that rejects the
            // pair — a rejection with no command to refuse, arriving after the placement was
            // already reported as accepted.
            int effectiveTick = frame.Tick + BuildDelayTicks;

            // The survey derives the set live at effectiveTick, orders it canonically and runs the
            // two audits — one implementation, shared with the driver, so what this accepts is by
            // construction what the driver will bake.
            int count = _validator.Survey(ref frame, effectiveTick);

            // The policy cap goes BETWEEN the survey and the trial rebake: refusing "you already
            // have 32" must not pay for a rebake first. A separate concept from the engine STORAGE
            // cap the survey's buffers are sized from.
            if (count >= MaxBuildings)
            {
                RejectBuilding(ref frame, cmd, $"building cap reached ({MaxBuildings})");
                return;
            }

            // A refused placement comes back as a VALUE — it is the normal outcome of a player
            // pointing somewhere they cannot build. A malformed request still throws, and that
            // catch stays: a stale shape id is this code's bug, and reporting it as "you cannot
            // build there" would show a developer error to the player on every peer at once.
            var candidate = new FPBuildingPlacement(
                shapeId, orientation, centre.x, centre.z, centre.y);
            FPBuildingRejectionInfo rejection;
            try
            {
                if (!_validator.TryPreviewWith(
                        _rebakeContext, candidate, out rejection, frame.Logger))
                {
                    RejectBuilding(ref frame, cmd, Describe(rejection));
                    return;
                }
            }
            catch (System.ArgumentException e)
            {
                RejectBuilding(ref frame, cmd, e.Message);
                return;
            }

            EntityRef entity = frame.CreateEntity();
            frame.Add(entity, new BuildingComponent
            {
                Centre = centre, ShapeId = shapeId, Orientation = orientation,
                OwnerSlot = cmd.PlayerId, Sequence = _validator.NextSequence,
                EffectiveTick = effectiveTick,
                RemovalEffectiveTick = int.MaxValue,
            });
            // The component IS the whole outcome. Nothing swaps here: this tick may be a client
            // prediction that a rollback throws away, and the navmesh does not roll back with the
            // frame — a swap performed here would survive the rewind and the re-executed ticks
            // would run against a mesh their own frame does not describe.
            //
            // NavMeshEffectiveTickSystem installs the mesh on the tick the component says, deriving
            // it from the frame, so a rollback across that tick reproduces the swap instead of
            // needing it undone. The rebake above was the validation and nothing else, and
            // TryPreviewPlacements is what makes that structural: it hands back no mesh and
            // discards its own output, so the pump's rebake patches the live mesh rather than
            // diffing against one that was never installed.
            frame.Logger?.KInformation(
                $"[Building][Placed] tick={frame.Tick}, player={cmd.PlayerId}, entity={entity}, "
                + $"shape={ShapeName(shapeId, orientation)}, total={count + 1}");
        }

        void HandleRemoveBuilding(ref Frame frame, RemoveBuildingCommand cmd)
        {
            if (_rebakeContext == null || _botFSM == null)
            {
                RejectBuilding(ref frame, cmd, "rebake unavailable on this stage");
                return;
            }

            EntityRef target = EntityRef.FromId(cmd.TargetEntityId);
            // IsAlive first, because it is the only half of this pair that reads the VERSION.
            // Frame.Has passes entity.Index alone, so on a slot that was freed and handed to a
            // NEW building it answers about the new occupant — and every step after that then
            // worked on the wrong entity: the collect below did not skip it (EntityRef equality
            // does compare version), so it stayed in the rebake input, and DestroyEntity strips
            // components BY INDEX unconditionally, so it deleted a building that was still
            // standing while its hole stayed carved in the navmesh. No rejection was raised, and
            // every peer did it identically, so no check would have asked why.
            //
            // Not reachable from this sample as it is wired — the only sender is the bot demo,
            // which reads the id from a live filter and dispatches in the same tick. A game that
            // puts a remove button on the network opens the window immediately, which is the
            // reachability this sample is responsible for.
            if (!frame.Entities.IsAlive(target) || !frame.Has<BuildingComponent>(target))
            {
                RejectBuilding(ref frame, cmd, $"building not found: {cmd.TargetEntityId}");
                return;
            }

            // Already scheduled: refuse rather than move the date. Two removes on one building
            // would otherwise leave the second one's tick standing, and a player double-clicking
            // is the ordinary way to send them.
            if (frame.GetReadOnly<BuildingComponent>(target).RemovalEffectiveTick != int.MaxValue)
            {
                RejectBuilding(ref frame, cmd, $"building already being demolished: {cmd.TargetEntityId}");
                return;
            }

            int removalTick = frame.Tick + BuildDelayTicks;

            // Excluded by SEQUENCE, not by entity: at this moment the target is still inside its own
            // tick window, so nothing else drops it. The survey refuses (-1) when two live buildings
            // share that number — with a duplicate, "the one to exclude" is not a well-defined
            // building, and removing whichever the comparison reached first would delete one that is
            // still standing while its hole stayed carved in the mesh. Excluding by EntityRef used to
            // be immune to that (it compares a version), so this is the one place the shared
            // derivation is stricter rather than merely equivalent.
            int targetSequence = frame.GetReadOnly<BuildingComponent>(target).Sequence;
            int count = _validator.Survey(ref frame, removalTick, skipSequence: targetSequence);
            if (count < 0)
            {
                RejectBuilding(ref frame, cmd,
                    $"ambiguous demolition target: Sequence {targetSequence} is shared by more than "
                    + $"one live building");
                return;
            }

            // Removal shrinks the set, so no GEOMETRY check can newly fail. The catalog lookup
            // and the grid check run BEFORE the geometry though, and those read stored state this
            // command did not produce: a BuildingComponent restored from a FullState written by a
            // build with a different shape catalog names an entry this one does not hold, and
            // that throws ArgumentException.
            //
            // Uncaught it would not stop at this command. The throw leaves EcsSimulation.Tick
            // through the command loop, so the rest of the tick's commands and every update
            // system are skipped and frame.Tick never increments — the same tick re-executes and
            // throws again, forever. Rejecting also repairs the common case: the building that
            // cannot be resolved is often the one being removed, and the collect above has
            // already left it out.
            //
            // Only the ArgumentException catch remains: a refused PLACEMENT now returns false,
            // and on a shrinking set no geometry check can produce one anyway.
            try
            {
                if (!_validator.TryPreview(
                        _rebakeContext, out FPBuildingRejectionInfo rejection, frame.Logger))
                {
                    RejectBuilding(ref frame, cmd, Describe(rejection));
                    return;
                }
            }
            catch (System.ArgumentException e)
            {
                RejectBuilding(ref frame, cmd, e.Message);
                return;
            }

            // A tombstone, not a destroy. The building keeps its hole in the mesh until the tick it
            // is due, and destroying it now would take the component that says so — leaving a peer
            // that joins during the window unable to reproduce a hole nothing in the frame
            // describes. NavMeshEffectiveTickSystem destroys it on that tick, having installed the
            // mesh without it.
            frame.Get<BuildingComponent>(target).RemovalEffectiveTick = removalTick;
            frame.Logger?.KInformation(
                $"[Building][Demolishing] tick={frame.Tick}, player={cmd.PlayerId}, entity={target}, "
                + $"effective={removalTick}, remaining={count}");
        }

        // ────────────────────────────────────────────
        // SpawnCharacterCommand: Create character from prototype
        // ────────────────────────────────────────────
        void HandleSpawn(ref Frame frame, SpawnCharacterCommand cmd)
        {
            frame.Logger?.KDebug($"[Spawn][Recv] tick={frame.Tick}, player={cmd.PlayerId}, class={cmd.CharacterClass}");

            int classId = cmd.CharacterClass;
            if ((uint)classId >= (uint)_stats.Length)
            {
                frame.Logger?.KError($"[Spawn][Reject:InvalidClass] tick={frame.Tick}, player={cmd.PlayerId}, classId={classId} (valid range: 0~{_stats.Length - 1})");
                return;
            }

            // Prevent duplicate creation if a character already exists for this player
            if (TryFindCharacter(ref frame, cmd.PlayerId, out _))
            {
                frame.Logger?.KError($"[Spawn][Reject:Duplicate] tick={frame.Tick}, player={cmd.PlayerId} already has a character");

                var rejectEvt = EventPool.Get<CommandRejectedSimEvent>();
                rejectEvt.PlayerId      = cmd.PlayerId;
                rejectEvt.CommandTypeId = cmd.CommandTypeId;
                rejectEvt.ReasonEnum    = RejectionReason.Duplicate;
                _events.Enqueue(rejectEvt);
                return;
            }

            var spawnPos = new FPVector3(cmd.SpawnPosition.x, FP64.FromDouble(0.5), cmd.SpawnPosition.y);
            EntityRef entity = classId switch
            {
                0 => frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos }),
                1 => frame.CreateEntity(new MagePrototype    { SpawnPosition = spawnPos }),
                2 => frame.CreateEntity(new RoguePrototype   { SpawnPosition = spawnPos }),
                3 => frame.CreateEntity(new KnightPrototype  { SpawnPosition = spawnPos }),
                _ => throw new System.ArgumentOutOfRangeException(nameof(classId)),
            };
            frame.Add(entity, new ErrorCorrectionTargetComponent());

            ref var character  = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId        = cmd.PlayerId;
            character.StockCount      = 3;

            // Apply the tick-0 entitlement loadout seed for this player's chosen class. The seed
            // (LoadoutSeedComponent) was written deterministically in OnInitializeWorld from each peer's
            // verified entitlement, so every peer sets the same masks here. Extract the 2 skill bits for the
            // spawned class; carry the consumable mask onto the character for the in-match check.
            if (TryFindLoadoutSeed(ref frame, cmd.PlayerId, out var seed))
            {
                character.AcquiredSkillMask   = (seed.OwnedSkillMask >> (classId * 2)) & 0b11;
                character.OwnedConsumableMask = seed.OwnedConsumableMask;
            }
            else
            {
                // Fallback → full (no lockout). Defensive only: tick-0 players are seeded in
                // OnInitializeWorld and late-joiners at their join tick — P2P keeps the seed ahead of
                // the spawn via the joiner-side cmd.Tick floor + join-command ordering; the dedicated
                // server does the same via join-command injection + the reliable placement floor.
                // Kept as a safety net: a seedless spawn degrades to the unrestricted loadout instead
                // of locking the character out.
                character.AcquiredSkillMask   = 0b11;
                character.OwnedConsumableMask = ~0;
            }

            ref var owner  = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId  = cmd.PlayerId;

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

            frame.Logger?.KInformation($"[Spawn][Commit] tick={frame.Tick}, player={cmd.PlayerId}, class={cmd.CharacterClass}, entity={entity.Index}, version={entity.Version}, pos=({cmd.SpawnPosition.x},{cmd.SpawnPosition.y})");
        }

        // ────────────────────────────────────────────
        // Common helpers
        // ────────────────────────────────────────────

        // ────────────────────────────────────────────
        // UseConsumableCommand: apply the issuer's consumable effect to their own character.
        // Demo effect — a "repair" consumable that reduces accumulated knockback (lower % = harder to
        // launch), the natural heal in this knockback combat model. Deterministic integer math only
        // (no float drift / RNG / culture). A real game would switch on cmd.ConsumableId; this demo has
        // one consumable, so the effect is fixed.
        //
        // Ownership: checked here against simulation state — the owned-consumable mask seeded onto the
        // character at spawn from the verified entitlement. This runs identically on every peer (P2P has no
        // server authority to drop), so an unowned use no-ops on all peers without divergence. On a
        // dedicated server the reliable-command entitlement gate also drops an unowned use before this tick
        // — redundant with this check but harmless.
        // ────────────────────────────────────────────
        void HandleUseConsumable(ref Frame frame, UseConsumableCommand cmd)
        {
            frame.Logger?.KDebug($"[Consumable][Recv] tick={frame.Tick}, player={cmd.PlayerId}, consumableId={cmd.ConsumableId}");

            if (!TryFindCharacter(ref frame, cmd.PlayerId, out var entity))
            {
                frame.Logger?.KDebug($"[Consumable][Skip:NoCharacter] tick={frame.Tick}, player={cmd.PlayerId}");
                return;
            }
            ref var character = ref frame.Get<CharacterComponent>(entity);

            // Idempotency + processed-marker. Advance LastConsumableUseSeq for EVERY received use (before the
            // dead/owned checks) so it records "processed", not "applied". A reliable retry of the same use
            // lands on a later tick with the same UseSeq → skipped here (exactly-once). Crucially, advancing
            // on the no-effect paths (dead / unowned) too lets the issuer's handle resolve via Confirm (which
            // observes this seq) — else an unowned use on the P2P legacy retry path would retry forever with
            // no apply to observe. Rides the snapshot, so a rollback restores it and re-sim re-processes at
            // the canonical tick.
            if (cmd.UseSeq <= character.LastConsumableUseSeq)
            {
                frame.Logger?.KDebug($"[Consumable][Skip:Duplicate] tick={frame.Tick}, player={cmd.PlayerId}, useSeq={cmd.UseSeq} <= last={character.LastConsumableUseSeq}");
                return;
            }
            character.LastConsumableUseSeq = cmd.UseSeq;

            if (character.IsDead)
            {
                frame.Logger?.KDebug($"[Consumable][Skip:Dead] tick={frame.Tick}, player={cmd.PlayerId}");
                return;
            }

            // Owned-consumable check: ConsumableId 100 -> bit0 (mirrors the seed namespace in BrawlerSimSetup).
            int consumableBit = cmd.ConsumableId - 100;
            if ((uint)consumableBit >= 32u || (character.OwnedConsumableMask & (1 << consumableBit)) == 0)
            {
                frame.Logger?.KDebug($"[Consumable][Skip:NotOwned] tick={frame.Tick}, player={cmd.PlayerId}, consumableId={cmd.ConsumableId}");
                return;
            }

            const int RepairAmount = 30;
            int before = character.KnockbackPower;
            character.KnockbackPower = before > RepairAmount ? before - RepairAmount : 0;
            // Repair applied — before/after lets one confirm the consumable was actually consumed (an unowned
            // use is dropped by the authority before this point, so it never logs an Apply).
            frame.Logger?.KInformation($"[Consumable][Apply] tick={frame.Tick}, player={cmd.PlayerId}, consumableId={cmd.ConsumableId}, knockback {before}->{character.KnockbackPower} (repair={before - character.KnockbackPower})");
        }

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

        // The per-player tick-0 entitlement loadout seed (written in OnInitializeWorld).
        bool TryFindLoadoutSeed(ref Frame frame, int playerId, out LoadoutSeedComponent result)
        {
            var filter = frame.Filter<LoadoutSeedComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var seed = ref frame.GetReadOnly<LoadoutSeedComponent>(entity);
                if (seed.PlayerId != playerId) continue;
                result = seed;
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
