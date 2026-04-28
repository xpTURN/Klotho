using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Systems
{
    /// <summary>
    /// ECS combat system: an Entity with a CombatComponent applies damage to enemy Entities within attack range.
    /// Filter: CombatComponent + TransformComponent + OwnerComponent (attacker)
    /// Target Filter: HealthComponent + TransformComponent + OwnerComponent (target)
    /// </summary>
    public class CombatSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var attackerFilter = frame.Filter<CombatComponent, TransformComponent, OwnerComponent>();
            while (attackerFilter.Next(out var attacker))
            {
                ref readonly var combat = ref frame.GetReadOnly<CombatComponent>(attacker);
                ref readonly var attackerTransform = ref frame.GetReadOnly<TransformComponent>(attacker);
                ref readonly var attackerOwner = ref frame.GetReadOnly<OwnerComponent>(attacker);

                FP64 attackRangeSq = FP64.FromInt(combat.AttackRange) * FP64.FromInt(combat.AttackRange);

                var targetFilter = frame.Filter<HealthComponent, TransformComponent, OwnerComponent>();
                while (targetFilter.Next(out var target))
                {
                    if (target.Index == attacker.Index)
                        continue;

                    ref readonly var targetOwner = ref frame.GetReadOnly<OwnerComponent>(target);
                    if (targetOwner.OwnerId == attackerOwner.OwnerId)
                        continue;

                    ref readonly var targetTransform = ref frame.GetReadOnly<TransformComponent>(target);
                    FPVector3 diff = targetTransform.Position - attackerTransform.Position;
                    FP64 distSq = diff.x * diff.x + diff.y * diff.y + diff.z * diff.z;

                    if (distSq > attackRangeSq)
                        continue;

                    ref var health = ref frame.Get<HealthComponent>(target);
                    health.CurrentHealth -= combat.AttackDamage;
                    if (health.CurrentHealth < 0)
                        health.CurrentHealth = 0;
                }
            }
        }
    }
}
