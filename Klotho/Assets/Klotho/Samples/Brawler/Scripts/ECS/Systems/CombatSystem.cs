using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// Each tick, detects hitbox overlaps to add KnockbackComponent and emit events.
    /// - Body contact between characters: radius-based circular overlap → mutual contact knockback + AttackHitEvent
    /// - Item contact: when a character enters the ItemComponent radius, emit ItemPickedUpEvent and remove the entity
    /// </summary>
    public class CombatSystem : ISystem
    {
        static readonly ulong ItemPickupFeatureKey = 0x4954454D5049434B; // "ITEMPICK"

        readonly EventSystem _events;
        readonly List<EntityRef> _candidateBuffer = new();
        readonly List<EntityRef> _destroyBuffer = new();

        public CombatSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            HandleCharacterContact(ref frame);
            HandleItemPickup(ref frame);
        }

        // ────────────────────────────────────────────
        // Body-contact detection between characters (O(n²), up to 4 players)
        // ────────────────────────────────────────────
        void HandleCharacterContact(ref Frame frame)
        {
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

            var filterA = frame.Filter<CharacterComponent, TransformComponent>();
            while (filterA.Next(out var entityA))
            {
                ref readonly var charA = ref frame.GetReadOnly<CharacterComponent>(entityA);
                if (charA.IsDead) continue;

                ref readonly var transA = ref frame.GetReadOnly<TransformComponent>(entityA);

                var filterB = frame.Filter<CharacterComponent, TransformComponent>();
                while (filterB.Next(out var entityB))
                {
                    // Prevent duplicate processing of the same pair (process once in ascending Index order)
                    if (entityB.Index <= entityA.Index) continue;

                    ref readonly var charB = ref frame.GetReadOnly<CharacterComponent>(entityB);
                    if (charB.IsDead) continue;

                    ref readonly var transB = ref frame.GetReadOnly<TransformComponent>(entityB);

                    FPVector2 diff = ToXZ(transB.Position) - ToXZ(transA.Position);
                    if (diff.sqrMagnitude > asset.BodyRadiusSqr) continue;

                    FPVector2 dirAtoB = diff.sqrMagnitude > FP64.Zero ? diff.normalized : FPVector2.Right;

                    // Bidirectional contact knockback
                    ApplyContact(ref frame, entityB,  dirAtoB, asset.ContactPower);
                    ApplyContact(ref frame, entityA, -dirAtoB, asset.ContactPower);

                    var contactEvt = EventPool.Get<AttackHitEvent>();
                    contactEvt.Attacker = entityA;
                    contactEvt.Target = entityB;
                    contactEvt.KnockbackAdded = asset.ContactPower;
                    contactEvt.HitPoint = ToXZ(transB.Position);
                    _events.Enqueue(contactEvt);
                }
            }
        }

        // ────────────────────────────────────────────
        // Item contact detection
        // ────────────────────────────────────────────
        void HandleItemPickup(ref Frame frame)
        {
            var seedFilter = frame.Filter<GameSeedComponent>();
            if (!seedFilter.Next(out var seedEntity)) return;
            ref readonly var seedComp = ref frame.GetReadOnly<GameSeedComponent>(seedEntity);

            var itemAsset = frame.AssetRegistry.Get<ItemConfigAsset>(1400);
            _destroyBuffer.Clear();
            var itemFilter = frame.Filter<ItemComponent, TransformComponent>();
            while (itemFilter.Next(out var item))
            {
                ref readonly var itemComp  = ref frame.GetReadOnly<ItemComponent>(item);
                ref readonly var itemTrans = ref frame.GetReadOnly<TransformComponent>(item);
                FPVector2 itemXZ = ToXZ(itemTrans.Position);

                _candidateBuffer.Clear();
                var charFilter = frame.Filter<CharacterComponent, TransformComponent>();
                while (charFilter.Next(out var character))
                {
                    ref readonly var charComp = ref frame.GetReadOnly<CharacterComponent>(character);
                    if (charComp.IsDead) continue;

                    ref readonly var charTrans = ref frame.GetReadOnly<TransformComponent>(character);
                    FPVector2 diff = ToXZ(charTrans.Position) - itemXZ;
                    if (diff.sqrMagnitude > itemAsset.PickupRadiusSqr) continue;

                    _candidateBuffer.Add(character);
                }

                if (_candidateBuffer.Count == 0) continue;

                EntityRef picker;
                if (_candidateBuffer.Count == 1)
                {
                    picker = _candidateBuffer[0];
                }
                else
                {
                    var rng = DeterministicRandom.FromSeed(
                        seedComp.WorldSeed, ItemPickupFeatureKey, (ulong)frame.Tick ^ (ulong)item.Index);
                    picker = _candidateBuffer[rng.NextInt(0, _candidateBuffer.Count)];
                }

                ApplyItemEffect(ref frame, picker, itemComp.ItemType, itemXZ);

                var pickupEvt = EventPool.Get<ItemPickedUpEvent>();
                pickupEvt.Character     = picker;
                pickupEvt.ItemType      = itemComp.ItemType;
                pickupEvt.ItemPosition  = itemXZ;
                _events.Enqueue(pickupEvt);
                _destroyBuffer.Add(item);
            }

            for (int i = 0; i < _destroyBuffer.Count; i++)
                frame.DestroyEntity(_destroyBuffer[i]);
        }

        void ApplyItemEffect(ref Frame frame, EntityRef character, int itemType, FPVector2 itemPos)
        {
            switch (itemType)
            {
                case 0: // Shield
                    ApplyShieldEffect(ref frame, character);
                    break;
                case 1: // Boost
                    ApplyBoostEffect(ref frame, character);
                    break;
                case 2: // Bomb
                    ApplyBombEffect(ref frame, character, itemPos);
                    break;
            }
        }

        void ApplyShieldEffect(ref Frame frame, EntityRef character)
        {
            if (!frame.Has<SkillCooldownComponent>(character)) return;
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);
            ref var cd = ref frame.Get<SkillCooldownComponent>(character);
            cd.ShieldTicks = asset.ShieldDurationTicks;
        }

        void ApplyBoostEffect(ref Frame frame, EntityRef character)
        {
            if (!frame.Has<SkillCooldownComponent>(character)) return;
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);
            ref var cd = ref frame.Get<SkillCooldownComponent>(character);
            cd.BoostTicks = asset.BoostDurationTicks;
        }

        void ApplyBombEffect(ref Frame frame, EntityRef picker, FPVector2 bombPos)
        {
            var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

            var filter = frame.Filter<CharacterComponent, TransformComponent, PhysicsBodyComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var ch = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (ch.IsDead) continue;

                ref readonly var trans = ref frame.GetReadOnly<TransformComponent>(entity);
                FPVector2 charXZ = new FPVector2(trans.Position.x, trans.Position.z);
                FPVector2 diff = charXZ - bombPos;
                if (diff.sqrMagnitude > asset.BombRadiusSqr) continue;

                if (CombatHelper.IsShielded(ref frame, entity)) continue;

                FPVector2 dir = diff.sqrMagnitude > FP64.Zero ? diff.normalized : FPVector2.Up;
                CombatHelper.ApplyKnockback(ref frame, entity, dir, asset.BombBasePower);

                ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);
                phys.RigidBody.velocity.x = dir.x * asset.BombImpulse;
                phys.RigidBody.velocity.z = dir.y * asset.BombImpulse;
            }
        }

        // ────────────────────────────────────────────
        // Contact-knockback apply helper
        // ────────────────────────────────────────────
        void ApplyContact(ref Frame frame, EntityRef target, FPVector2 direction, int power)
        {
            if (CombatHelper.IsShielded(ref frame, target))
                return;

            CombatHelper.ApplyPush(ref frame, target, direction, power);
        }

        static FPVector2 ToXZ(FPVector3 v) => new FPVector2(v.x, v.z);
    }
}
