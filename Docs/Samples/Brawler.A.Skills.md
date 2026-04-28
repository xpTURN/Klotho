# Brawler Appendix A — Character Skill Specs

> Related: [Brawler.md](Brawler.md) §7 (Events) · §9 (Systems)
> Target: per-class skill branching in `PlatformerCommandSystem` + 4 classes × 2 skills = 8 implementations

---

## A-1. PlatformerCommandSystem Dispatch

On receipt of `UseSkillCommand`, branch on `CharacterClass`.

```csharp
switch (character.CharacterClass)
{
    case 0: skillTargetPos = SkillWarrior(ref frame, caster, cmd, origin2, aimDir2); break;
    case 1: skillTargetPos = SkillMage  (ref frame, caster, cmd, origin2, aimDir2); break;
    case 2: skillTargetPos = SkillRogue (ref frame, caster, cmd, origin2, aimDir2); break;
    case 3: skillTargetPos = SkillKnight(ref frame, caster, cmd, origin2); break;
}
```

Skill parameters are loaded via `frame.AssetRegistry.Get<SkillConfigAsset>(assetId)` and cached internally as a 4×2 matrix in `_skills[class][slot]`.

**Common post-processing** (every skill):
1. Set `character.ActionLockTicks` to `SkillConfigAsset.ActionLockTicks`
2. Set `SkillCooldownComponent.Skill0/1Cooldown` to `SkillConfigAsset.Cooldown`
3. Emit `SkillActionEvent` (Regular) → Animator hook
4. On action completion, emit `ActionCompletedEvent` (Synced) → release the lock

---

## A-2. Eight Skills

### Warrior (CharacterClass = 0)

| Slot | Name | AssetId | Behavior | Key Parameters |
|---|---|---|---|---|
| Skill0 | Melee Circular Slam | 1200 | Apply `KnockbackPower` to all enemies within `RangeSqr` around `origin` | RangeSqr=4, KnockbackPower=20, ActionLock=20 |
| Skill1 | Charge Dash | 1201 | Set XZ velocity `velocity = aimDir * MoveSpeedOrRange`, emit `DashEvent` | MoveSpeedOrRange=12, ActionLock=15 |

```csharp
// Skill0 — uses AreaHitAllEnemies
var sk = _skills[0][0];
return AreaHitAllEnemies(ref frame, caster, origin, sk.RangeSqr, sk.KnockbackPower, HitEffectType.HitReaction) ?? origin;

// Skill1 — set RigidBody.velocity directly + DashEvent
var sk = _skills[0][1];
ref var physics = ref frame.Get<PhysicsBodyComponent>(caster);
physics.RigidBody.velocity.x = aimDir.x * sk.MoveSpeedOrRange;
physics.RigidBody.velocity.z = aimDir.y * sk.MoveSpeedOrRange;
_events.Enqueue(EventPool.Get<DashEvent>().Set(caster, aimDir));
```

### Mage (CharacterClass = 1)

| Slot | Name | AssetId | Behavior | Key Parameters |
|---|---|---|---|---|
| Skill0 | Ranged Shockwave | 1210 | Area hit centered at `origin + aimDir * MoveSpeedOrRange` | MoveSpeedOrRange=6, RangeSqr=4 |
| Skill1 | Teleport | 1211 | Move `Position` to the target instantly + correct ground via `FPStaticBVH` raycast | MoveSpeedOrRange=7 (move distance) |

Mage Skill1 computes the destination's ground Y via `RayCaster` and corrects with `halfHeight + radius - ColliderOffset.y` so the capsule's bottom aligns with the static geometry.

### Rogue (CharacterClass = 2)

| Slot | Name | AssetId | Behavior | Key Parameters |
|---|---|---|---|---|
| Skill0 | Dash + Strike | 1220 | Dash similar to Warrior Skill1, then area hit at `ImpactOffsetDist` from the start point | MoveSpeedOrRange=10, ImpactOffsetDist=2, RangeSqr=2.25 |
| Skill1 | Throwing Dagger | 1221 | Hit the **closest** enemy within range along `aimDir` (dot > 0 filter) | RangeSqr=25 (ranged), KnockbackPower=15 |

```csharp
// Skill1 — find one enemy in front (angle filter via dot product)
var filter = frame.Filter<CharacterComponent, TransformComponent>();
while (filter.Next(out var target)) {
    if (target == caster) continue;
    FPVector2 diff = ToXZ(tt.Position) - origin;
    if (diff.sqrMagnitude > sk.RangeSqr) continue;
    FP64 dot = diff.x * aimDir.x + diff.y * aimDir.y;
    if (dot < FP64.Zero) continue;   // exclude targets behind
    ApplyHit(ref frame, caster, target, aimDir, sk.KnockbackPower, HitEffectType.HitReaction);
    return ToXZ(tt.Position);
}
```

### Knight (CharacterClass = 3)

| Slot | Name | AssetId | Behavior | Key Parameters |
|---|---|---|---|---|
| Skill0 | Shield Reflect | 1230 | Set `SkillCooldownComponent.ShieldTicks` — absorb / reflect knockback | AuxDurationTicks=60 |
| Skill1 | Ground Slam | 1231 | Area hit centered at `origin` + emit `GroundSlamEvent(Position, Radius)` | RangeSqr=9, EffectRadius=3, KnockbackPower=25 |

Shield handling lives in the internal helper `ApplyHit`: when `CombatHelper.IsShielded(ref frame, target)` is true, call **`CombatHelper.ApplyPush` against the attacker in the reverse direction** (push the attacker with `-direction`). There is no separate `ApplyKnockbackReflected` method.

---

## A-3. SkillConfigAsset Tuning Guide

`SkillConfigAsset` field semantics:

| Field | Type | Purpose |
|---|---|---|
| `Cooldown` | int (tick) | Cooldown ticks — assumes 40 ticks/s |
| `ActionLockTicks` | int | Action-lock duration (movement suppressed) |
| `MoveSpeedOrRange` | FP64 | Dash speed or projectile range (meaning varies per skill) |
| `RangeSqr` | FP64 | Hit-detection distance² (compared against FP64 sqrMagnitude) |
| `KnockbackPower` | int | Base knockback (scaled by victim's `KnockbackPower`) |
| `EffectRadius` | FP64 | Visual VFX radius (Knight ground slam, etc.) |
| `AuxDurationTicks` | int | Auxiliary duration (Shield, etc.) |
| `ImpactOffsetDist` | FP64 | Impact-point offset distance (Rogue Skill0, etc.) |

### Recommended Defaults

| Class | Skill | Cooldown | ActionLock | Move/Range | RangeSqr | KB | Aux | Impact |
|---|---|---|---|---|---|---|---|---|
| Warrior | 0 | 60 | 20 | 0 | 4 | 20 | 0 | 0 |
| Warrior | 1 | 80 | 15 | 12 | 0 | 0 | 0 | 0 |
| Mage | 0 | 100 | 25 | 6 | 4 | 18 | 0 | 0 |
| Mage | 1 | 150 | 10 | 7 | 0 | 0 | 0 | 0 |
| Rogue | 0 | 60 | 18 | 10 | 2.25 | 15 | 0 | 2 |
| Rogue | 1 | 90 | 12 | 0 | 25 | 15 | 0 | 0 |
| Knight | 0 | 120 | 8 | 0 | 0 | 0 | 60 | 0 |
| Knight | 1 | 180 | 30 | 0 | 9 | 25 | 0 | 0 |

---

## A-4. Helper Methods

### AreaHitAllEnemies (system-internal)

Applies `ApplyHit` to **every enemy** within range and returns the **average XZ position** of all hits (used as the hit-center for VFX placement). Returns `null` if no targets are hit.

```csharp
private FPVector2? AreaHitAllEnemies(ref Frame frame, EntityRef attacker,
    FPVector2 center, FP64 radiusSqr, int power, HitEffectType effectType)
{
    FPVector2 posSum = FPVector2.Zero;
    int hitCount = 0;

    var filter = frame.Filter<CharacterComponent, TransformComponent>();
    while (filter.Next(out var target))
    {
        if (target == attacker) continue;
        ref readonly var tt = ref frame.GetReadOnly<TransformComponent>(target);
        FPVector2 targetPos = new FPVector2(tt.Position.x, tt.Position.z);
        FPVector2 diff = targetPos - center;
        if (diff.sqrMagnitude > radiusSqr) continue;

        FPVector2 dir = diff.sqrMagnitude > FP64.Epsilon ? diff.Normalized : FPVector2.Up;
        ApplyHit(ref frame, attacker, target, dir, power, effectType);
        posSum += targetPos;
        hitCount++;
    }

    if (hitCount == 0) return null;
    return posSum * (FP64.One / FP64.FromInt(hitCount));
}
```

### ApplyHit (system-internal)

Branches into one of three `CombatHelper` methods based on Shield / effect type.

```csharp
void ApplyHit(ref Frame frame, EntityRef attacker, EntityRef target,
    FPVector2 direction, int basePower, HitEffectType effectType)
{
    // Shield active → reflect: the attacker is pushed in the reverse direction
    if (CombatHelper.IsShielded(ref frame, target))
    {
        CombatHelper.ApplyPush(ref frame, attacker, -direction, basePower);
        return;
    }

    // Normal hit — branch on effect type
    if (effectType == HitEffectType.HitReaction)
        CombatHelper.ApplyHitReaction(ref frame, target, direction, basePower,
                                       hitReactionTicks: _attack.HitStunTicks);
    else
        CombatHelper.ApplyPush(ref frame, target, direction, basePower);
}
```

### Four CombatHelper Methods

| Method | Purpose | KnockbackComponent Duration Source |
|---|---|---|
| `ApplyKnockback` | Strong knockback (skill main hits) | `CombatPhysicsAsset.DefaultKnockbackDurationTicks` |
| `ApplyHitReaction` | Hit stun (basic attacks) | `HitReactionDurationTicks` + sets `Character.HitReactionTicks` |
| `ApplyPush` | Light push (e.g., Shield reflect) | `PushDurationTicks` |
| `IsShielded(ref frame, entity)` | Whether Shield is active | `SkillCooldownComponent.ShieldTicks > 0` |

For the detailed implementation, see [Brawler.B.Systems.md](Brawler.B.Systems.md) §B-5.
