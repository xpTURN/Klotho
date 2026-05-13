# Brawler Appendix B — System Algorithms

> Related: [Brawler.md](Brawler.md) §9 (System implementation & registration)
> Target: core logic of the 16 systems + `CombatHelper`
>
> ⚠️ **Note**: The system code snippets in this appendix are **algorithm-illustration examples**, not verbatim quotes from the source. Field names (`CharacterComponent.IsJumping/IsGrounded/GroundNormal/ActionLockTicks/HitReactionTicks`, `KnockbackComponent.InitialDurationTicks/BlockInput`, etc.) and method signatures (`CombatHelper.ApplyKnockback/ApplyHitReaction/ApplyPush/IsShielded`) match the actual source. For detailed branches and safety checks, refer to the real source.

---

## B-1. TopdownMovementSystem (Update Phase)

- **Responsibilities**: apply gravity, project velocity onto slope surfaces, rotate XZ to face movement direction
- **Run condition**: `TransformComponent + PhysicsBodyComponent + CharacterComponent` + `!IsDead`
- **Asset**: `MovementPhysicsAsset` (AssetId=1500)

```csharp
public void Update(ref Frame frame)
{
    var asset = frame.AssetRegistry.Get<MovementPhysicsAsset>(1500);
    var dt = FP64.FromInt(frame.DeltaTimeMs) * MsToSeconds;

    var filter = frame.Filter<TransformComponent, PhysicsBodyComponent, CharacterComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
        if (character.IsDead) continue;

        ref var transform = ref frame.Get<TransformComponent>(entity);
        ref var physics   = ref frame.Get<PhysicsBodyComponent>(entity);
        ref var body      = ref physics.RigidBody;

        // 1. Gravity / slope projection
        if (character.IsJumping || !character.IsGrounded)
        {
            body.velocity.y -= asset.GravityAccel * dt;
        }
        else if (character.GroundNormal.y < FP64.One)
        {
            // Slope: project horizontal velocity onto the slope
            FP64 hSqr = body.velocity.x * body.velocity.x + body.velocity.z * body.velocity.z;
            if (hSqr > asset.MinMoveSqr)
            {
                FPVector3 hVel = new FPVector3(body.velocity.x, FP64.Zero, body.velocity.z);
                FPVector3 n = character.GroundNormal;
                FPVector3 projected = hVel - n * FPVector3.Dot(hVel, n);
                FP64 projSqr = projected.sqrMagnitude;
                if (projSqr > FP64.Epsilon)
                {
                    FP64 scale = FP64.Sqrt(hSqr / projSqr);
                    body.velocity = projected * scale;
                }
            }
        }

        // 2. XZ rotation (skipped while in knockback / action lock)
        if (!frame.Has<KnockbackComponent>(entity) && character.ActionLockTicks <= 0)
        {
            FP64 vx = body.velocity.x, vz = body.velocity.z;
            if (vx * vx + vz * vz > asset.MinMoveSqr)
                transform.Rotation = FP64.Atan2(vx, vz);
        }
    }
}
```

---

## B-2. KnockbackSystem (Update)

- **Responsibilities**: accumulate `KnockbackComponent.Force` into velocity with a time-decay curve, and remove the component when the duration expires
- **Decay model**: linear `Evaluate(elapsed)`

```csharp
public void Update(ref Frame frame)
{
    var dt = FP64.FromInt(frame.DeltaTimeMs) * MsToSeconds;
    var filter = frame.Filter<PhysicsBodyComponent, KnockbackComponent>();
    while (filter.Next(out var entity))
    {
        ref var kb   = ref frame.Get<KnockbackComponent>(entity);
        ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);

        FP64 elapsed = FP64.One - FP64.FromInt(kb.DurationTicks) / FP64.FromInt(kb.InitialDurationTicks);
        FP64 mult = DampingCurve.Evaluate(elapsed);   // FPAnimationCurve or linear

        phys.RigidBody.velocity.x += kb.Force.x * mult * dt;
        phys.RigidBody.velocity.z += kb.Force.y * mult * dt;

        if (--kb.DurationTicks <= 0)
            frame.Remove<KnockbackComponent>(entity);
    }
}
```

---

## B-3. BoundaryCheckSystem (Update)

- **Responsibilities**: detect XZ-boundary exit (`FPBounds2`) or fall below `FallDeathY` → death handling
- **Asset**: `BrawlerGameRulesAsset` (AssetId=1001)

```csharp
public void Update(ref Frame frame)
{
    var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
    var stageBounds = new FPBounds2(
        center: FPVector2.Zero,
        size:   new FPVector2(rules.StageBoundsSize, rules.StageBoundsSize));

    var filter = frame.Filter<CharacterComponent, TransformComponent>();
    while (filter.Next(out var entity))
    {
        ref var character = ref frame.Get<CharacterComponent>(entity);
        if (character.IsDead) continue;

        ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
        FPVector2 xzPos = new FPVector2(transform.Position.x, transform.Position.z);

        if (stageBounds.Contains(xzPos) && transform.Position.y > rules.FallDeathY) continue;

        // Death handling
        character.StockCount--;
        character.KnockbackPower = 0;
        character.IsDead = true;
        character.RespawnTimer = character.StockCount > 0 ? rules.RespawnTicks : 0;

        if (frame.Has<PhysicsBodyComponent>(entity))
        {
            ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);
            physics.RigidBody.velocity = FPVector3.Zero;
            physics.RigidBody.isStatic = true;
        }

        var evt = EventPool.Get<CharacterKilledEvent>();
        evt.Character = entity;
        evt.PlayerId = character.PlayerId;
        evt.StockRemaining = character.StockCount;
        _events.Enqueue(evt);
    }
}
```

---

## B-4. GroundClampSystem (PostUpdate)

- **Responsibilities**: compute ground Y via downward `FPStaticBVH` raycast and clamp the capsule's bottom
- **Formula**: `groundY = hitPt.y + halfHeight + radius - ColliderOffset.y`

```csharp
public void Update(ref Frame frame)
{
    var asset = frame.AssetRegistry.Get<MovementPhysicsAsset>(1500);
    var filter = frame.Filter<TransformComponent, PhysicsBodyComponent, CharacterComponent>();

    while (filter.Next(out var entity))
    {
        ref var character = ref frame.Get<CharacterComponent>(entity);
        if (character.IsDead) continue;

        ref var transform = ref frame.Get<TransformComponent>(entity);
        ref var physics   = ref frame.Get<PhysicsBodyComponent>(entity);
        if (physics.Collider.type != ShapeType.Capsule) continue;

        FP64 halfH = physics.Collider.capsule.halfHeight;
        FP64 r     = physics.Collider.capsule.radius;

        if (character.IsJumping && physics.RigidBody.velocity.y > FP64.Zero)
        {
            character.IsGrounded = false; continue;
        }

        // Raycast: from below the capsule center down to MaxFallProbe
        FPVector3 capsuleCenter = transform.Position + physics.ColliderOffset;
        FPVector3 rayOrigin = capsuleCenter - FPVector3.Up * (halfH + r) + FPVector3.Up * asset.SkinOffset;
        var downRay = new FPRay3(rayOrigin, -FPVector3.Up);

        if (!_rayCaster.RayCastStatic(downRay, asset.MaxFallProbe, out var groundPt, out var groundNormal, out _))
        {
            character.IsGrounded = false;
            character.GroundNormal = FPVector3.Up;
            continue;
        }

        FP64 groundY = groundPt.y + halfH + r - physics.ColliderOffset.y;

        if (transform.Position.y < groundY)
        {
            // Penetrating the ground → clamp
            transform.Position.y = groundY;
            physics.RigidBody.velocity.y = FP64.Zero;
            character.IsGrounded = true;
            character.IsJumping = false;
            character.GroundNormal = groundNormal;
        }
        else
        {
            // Near the ground → snap when within range
            FP64 surfaceDist = transform.Position.y - groundY;
            if (character.IsGrounded && surfaceDist < asset.GroundSnapDepth)
            {
                transform.Position.y = groundY;
                physics.RigidBody.velocity.y = FP64.Zero;
                character.IsJumping = false;
                character.GroundNormal = groundNormal;
            }
            else
            {
                character.IsGrounded = surfaceDist < asset.GroundEnterDepth;
                if (character.IsGrounded)
                    physics.RigidBody.velocity.y = FP64.Zero;
                character.IsJumping = character.IsJumping && !character.IsGrounded;
                character.GroundNormal = character.IsGrounded ? groundNormal : FPVector3.Up;
            }
        }
    }
}
```

**Key parameters**:
- `SkinOffset` (default 0.3) — lifts the ray origin slightly above the capsule's bottom to avoid self-intersection
- `GroundEnterDepth` (default 0.19) — within this distance, treat as grounded
- `GroundSnapDepth` (default 0.05) — when already grounded and within this distance, force-snap to the ground

---

## B-5. CombatHelper.ApplyKnockback

```csharp
public static void ApplyKnockback(ref Frame frame, EntityRef target, FPVector2 direction, int basePower)
{
    var asset = frame.AssetRegistry.Get<CombatPhysicsAsset>(1300);

    ref var targetChar = ref frame.Get<CharacterComponent>(target);
    targetChar.KnockbackPower += basePower;

    // Amplify force proportional to accumulated KnockbackPower (Smash-Bros style)
    FP64 forceMag = FP64.FromInt(basePower)
                  + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01);
    FPVector2 force = direction * forceMag;

    if (frame.Has<KnockbackComponent>(target))
    {
        // Compare with existing knockback — only overwrite if stronger
        ref var kb = ref frame.Get<KnockbackComponent>(target);
        if (force.sqrMagnitude > kb.Force.sqrMagnitude) kb.Force = force;
        kb.InitialDurationTicks = asset.DefaultKnockbackDurationTicks;
        kb.DurationTicks        = asset.DefaultKnockbackDurationTicks;
        kb.BlockInput = true;
    }
    else
    {
        frame.Add(target, new KnockbackComponent {
            Force = force,
            InitialDurationTicks = asset.DefaultKnockbackDurationTicks,
            DurationTicks = asset.DefaultKnockbackDurationTicks,
            BlockInput = true,
        });
    }
}
```

`CombatHelper` exposes 4 static methods:

| Method | Duration | Notes |
|---|---|---|
| `ApplyKnockback` | `DefaultKnockbackDurationTicks` (20) | Maximum strength — sets `BlockInput = true` |
| `ApplyHitReaction` | `HitReactionDurationTicks` (10) | `forceMag × 0.64` multiplier + sets `HitReactionTicks` (for Hit animation) |
| `ApplyPush` | `PushDurationTicks` (10) | `forceMag × 0.64` multiplier — light push |
| `IsShielded(ref frame, entity)` | — | Returns `SkillCooldownComponent.ShieldTicks > 0` |

When Shield is active, the reflection logic is **not a separate method** — instead, after `IsShielded` succeeds, call `ApplyPush(attacker, -direction, basePower)` to push the attacker in the **reverse direction**. See `ApplyHit` in [Brawler.A.Skills.md](Brawler.A.Skills.md) §A-4.

---

## B-6. Other Systems — Summaries

### PhysicsSystem (Update)

A sample-owned class. `FPPhysicsWorld` constructor signature is `(maxBodies, gravity)` — Brawler uses `new PhysicsSystem(256, FPVector3.Zero)` (gravity is applied directly in `TopdownMovementSystem`).

1. `SyncEcsToPhysics`: `PhysicsBodyComponent.RigidBody` → `FPPhysicsWorld.Bodies`
2. `_world.Step(dt)` — broad-/narrowphase + collision response
3. `SyncPhysicsToEcs`: `FPPhysicsWorld.Bodies` → `TransformComponent.Position`, `RigidBody.velocity`

### ActionLockSystem (Update)

```csharp
var filter = frame.Filter<CharacterComponent>();
while (filter.Next(out var entity)) {
    ref var character = ref frame.Get<CharacterComponent>(entity);
    if (character.ActionLockTicks <= 0) continue;
    if (--character.ActionLockTicks == 0) {
        _events.Enqueue(EventPool.Get<ActionCompletedEvent>().Set(entity));
    }
}
```

### SkillCooldownSystem (Update)

```csharp
var filter = frame.Filter<SkillCooldownComponent>();
while (filter.Next(out var entity)) {
    ref var cd = ref frame.Get<SkillCooldownComponent>(entity);
    if (cd.Skill0Cooldown > 0) cd.Skill0Cooldown--;
    if (cd.Skill1Cooldown > 0) cd.Skill1Cooldown--;
    if (cd.ShieldTicks    > 0) cd.ShieldTicks--;
}
```

### RespawnSystem (Update)

```csharp
var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
var filter = frame.Filter<CharacterComponent, TransformComponent>();
while (filter.Next(out var entity)) {
    ref var c = ref frame.Get<CharacterComponent>(entity);
    if (!c.IsDead || c.StockCount <= 0) continue;
    if (--c.RespawnTimer > 0) continue;

    // Respawn: return to spawn position, re-enable physics
    ref var t = ref frame.Get<TransformComponent>(entity);
    t.Position = rules.SpawnPositions[c.PlayerId % rules.SpawnPositions.Length];
    c.IsDead = false;
    ref var p = ref frame.Get<PhysicsBodyComponent>(entity);
    p.RigidBody.isStatic = false;
    _events.Enqueue(EventPool.Get<CharacterSpawnedEvent>()
        .Set(c.PlayerId, c.CharacterClass));
}
```

### TimerSystem (Update)

- Compute elapsed milliseconds as `frame.Tick × frame.DeltaTimeMs`
- Emit `RoundTimerEvent (Synced)` whenever a 1-second boundary is crossed
- When `GameDurationSeconds` is reached, set `GameTimerStateComponent.TimeoutFired = true`

### GameOverSystem (PostUpdate)

Evaluates only when `GameTimerStateComponent.GameOverFired == false`:
- If players with 0 stocks ≥ (totalPlayers - 1) → determine the winner
- On timeout → pick the winner by minimum stocks/knockback, with `reason = "timeout"`
- Emit `GameOverEvent (Synced)` once, then set `GameOverFired = true`

### ObstacleMovementSystem (Update)

For entities with `PlatformComponent`:
- Cycles through 4 waypoints `Waypoint0..3`
- Linearly interpolates `MoveProgress` 0→1; `WaypointIndex` selects the next target
- Kinematic — set `TransformComponent` directly after `PhysicsSystem`

### ItemSpawnSystem (Update)

- Decide whether to spawn an item when `frame.Tick % SpawnIntervalTicks == 0`
- Use `frame.Random` (DeterministicRandom) to pick the XZ position and item type
- Create with `frame.CreateEntity(ItemPickupPrototype.Id)`
- Skip if the current item count is ≥ `MaxItems`

### CombatSystem (Update)

Dedicated to item-pickup detection:
- Character–item proximity check (`PickupRadiusSqr`)
- Apply effects per `ItemType`:
  - `Shield` → `SkillCooldownComponent.ShieldTicks = ShieldDurationTicks`
  - `Boost`  → temporary speed boost (separate Buff component or flag)
  - `Bomb`   → explosion within `BombRadiusSqr` + `BombImpulse` knockback
- Emit `ItemPickedUpEvent (Synced)`, then destroy the item entity

### TrapTriggerSystem (Update)

Detects proximity to `SpawnMarkerComponent` (trap tag):
- On character entry, call `ApplyKnockback` and emit `TrapTriggeredEvent (Synced)`
- Add a cooldown field to `SpawnMarkerComponent` if needed

### BotFSMSystem (PreUpdate)

Tick the HFSM for every entity holding `BotComponent`. For state transitions and Decisions, see [Brawler.D.BotHFSM.md](Brawler.D.BotHFSM.md).

### SavePreviousTransformSystem (PreUpdate)

Runs first in each tick. Backs up `TransformComponent.Position/Rotation` to dedicated `PreviousPosition/PreviousRotation` (used for view interpolation; stored in Frame so it participates in snapshots / rollback).

### PlatformerCommandSystem (PreUpdate)

Implements both `ICommandSystem + ISyncEventSystem`. `OnCommand` handles the 4 command kinds; for per-class skill branching, see [Brawler.A.Skills.md](Brawler.A.Skills.md) §A-1. `HandleSpawn` rejects a duplicate spawn (a character already exists for `cmd.PlayerId`) by enqueueing a `CommandRejectedSimEvent` (Mode = Synced, Reason = `Duplicate`) — the server's `ServerNetworkService` subscribes via `OnSyncedEvent` and unicasts a `CommandRejectedMessage` back to the originating peer over `DeliveryMethod.Unreliable`, gated by a per-peer token bucket (`ConsumeRejectToken`); rejects without a `peerId` mapping (e.g. bot players) are skipped with a warning log.

---

## B-7. Asset-Lookup Conventions

Systems look assets up by hardcoded AssetId:

| AssetId | Class | Purpose |
|---|---|---|
| 1001 | BrawlerGameRulesAsset | Game rules (spawn / boundary / time) |
| 1100~1103 | CharacterStatsAsset | Per-class stats |
| 1200~1231 | SkillConfigAsset | Per-skill parameters |
| 1300 | CombatPhysicsAsset | Combat physics |
| 1301 | BasicAttackConfigAsset | Basic attack |
| 1400 | ItemConfigAsset | Items |
| 1500 | MovementPhysicsAsset | Movement physics |
| 1600 | BotBehaviorAsset | Bot behavior |
| 1700~1702 | BotDifficultyAsset | Difficulty (Easy/Normal/Hard) |

For the full AssetId catalog, see [Brawler.C.DataAssets.md](Brawler.C.DataAssets.md).
