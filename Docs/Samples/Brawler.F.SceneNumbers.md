# Brawler Appendix F — Scene / Prefab Numbers

> Related: [Brawler.md](Brawler.md) §13 (Phase 10 — Scenes & Prefabs)
> Target: spawn coordinates, platform paths, physics constants, prefab-component checklist
>
> ⚠️ **Note**: Per-class numbers like `MoveSpeed` and `Mass` are **injected from JSON DataAssets**; the C# code holds only common defaults. The "recommended values" in §F-4 are examples only and may differ from the actual sample data.

---

## F-1. Stage Numbers

### Boundary (BrawlerGameRulesAsset)

| Parameter | Value | Note |
|---|---|---|
| StageBoundsSize | 40.0 | Total XZ size (range ±20) |
| FallDeathY | -10.0 | Below this Y → fall-death |
| RespawnTicks | 120 | 3 seconds (40 ticks/s) |

**FPBounds2 construction**: `new FPBounds2(center: FPVector2.Zero, size: new FPVector2(40, 40))` — `[-20, 20]` on the XZ plane.

### Player Spawn Positions

`BrawlerGameRulesAsset.SpawnPositions` holds an array of 4 coordinates:

| PlayerId | Position (x, y, z) |
|---|---|
| 0 | (-4, 0, -4) |
| 1 | ( 4, 0, -4) |
| 2 | (-4, 0,  4) |
| 3 | ( 4, 0,  4) |

---

## F-2. Moving Platform

Created by `MovingPlatformPrototype` in `BrawlerSimSetup.InitializeWorldState`, with a 4-point round-trip path:

```csharp
frame.Add(platformEntity, new PlatformComponent
{
    IsMoving = true,
    Waypoint0 = new FPVector3(-16f, 0f, -16f),
    Waypoint1 = new FPVector3(+16f, 0f, -16f),
    Waypoint2 = new FPVector3(+16f, 0f, +16f),
    Waypoint3 = new FPVector3(-16f, 0f, +16f),
    WaypointIndex = 0,
    MoveSpeed = FP64.FromDouble(0.1),  // units / tick
    MoveProgress = FP64.Zero,
});
```

`ObstacleMovementSystem` linearly interpolates from `Waypoint{i}` to `Waypoint{(i+1)%4}` each tick; when `MoveProgress` reaches 1, `WaypointIndex++`.

---

## F-3. Physics Constants (MovementPhysicsAsset)

| Parameter | Value | Meaning |
|---|---|---|
| JumpSpeed | 8.0 | On Space input, `velocity.y = 8.0` |
| GravityAccel | 20.0 | Gravity (units/sec²) |
| MinMoveSqr | 0.0001 | Stationary threshold — below this, skip rotation / slope projection |
| SkinOffset | 0.3 | Ground-ray start offset |
| MaxFallProbe | 5.0 | Max ground-probe distance while falling |
| GroundEnterDepth | 0.19 | Air → ground entry threshold |
| GroundSnapDepth | 0.05 | Force-snap threshold while already grounded |

### Jump Trajectory at 40 ticks/sec

- Jump initial speed 8 → reaches 0 after `8 / 20 = 0.4 s` (apex)
- Apex height: `8² / (2 × 20) = 1.6 units`
- Total time to landing: `0.8 s = 32 ticks`

---

## F-4. Character Move Speed (CharacterStatsAsset) — Recommended Values

The C# field default is `MoveSpeed = 0` (i.e., the value must come from JSON). Recommended tuning values:

| Class | Recommended MoveSpeed | Recommended Mass | Profile |
|---|---|---|---|
| Warrior | 5.0 | 1.0 | Balanced |
| Mage | 4.0 | 0.8 | Slow, light |
| Rogue | 6.0 | 0.9 | Fast |
| Knight | 3.5 | 1.3 | Heavy and slow |

`TopdownMovementSystem` sets `velocity.xz = MoveInputCommand.H/V × MoveSpeed`. Actual values are specified in the JSON in [Brawler.C.DataAssets.md](Brawler.C.DataAssets.md) §C-5.

---

## F-5. Prefab Component Checklist

### BrawlerGameController.prefab (root)

| Component | Role | Field Wiring |
|---|---|---|
| `BrawlerGameController` (MonoBehaviour) | Game controller | `_staticCollidersAsset`, `_navMeshAsset`, `_dataAsset`, `_simulationConfig`, `_uKlotho`, `_viewSync`, `_viewUpdater`, `_gameMenu`, `_brawlerSettings` |
| `UKlothoBehaviour` | Session bind / Update | (auto) |
| `BrawlerViewSync` | Holds view references | `_gameHUD`, `_resultScreen`, `_movingPlatforms` |
| `EntityViewUpdater` | Entity ↔ View reconcile | `_factory` = `BrawlerEntityViewFactory.asset`, `_pool` (optional) |
| `BrawlerCameraController` | Camera follow | `_cinemachineCamera`, `_proxy` |

### Characters/Warrior.prefab (Mage/Rogue/Knight share this structure)

```
Warrior (root)
├── CharacterView (MonoBehaviour, inherits EntityView)
│   ├── _renderers          = [child Renderers]
│   ├── _shieldFx           = Child/ShieldFx GameObject
│   ├── _boostFx            = Child/BoostFx GameObject
│   ├── _interpolationTarget = Child/Model (optional)
│   └── _errorVisual         = ErrorVisualState.Default
├── (child) Model
│   ├── SkinnedMeshRenderer (character mesh)
│   ├── Animator (RuntimeAnimatorController = BrawlerAnimator)
│   ├── CharacterAnimatorViewComponent (EntityViewComponent)
│   │   └── _animator → Animator on the same object
│   └── CharacterActionVfxViewComponent (EntityViewComponent)
│       ├── _attackVfxPrefab → Assets/Klotho/Samples/Brawler/SFX/SlashVfx.prefab
│       └── _skillVfxByClass → [Skill0, Skill1] VFX prefabs
├── (child) ShieldFx (initially inactive) → ShieldFx.prefab
└── (child) BoostFx (initially inactive) → BoostFx.prefab
```

**Animator parameters** (set inside `CharacterAnimatorViewComponent`):

| Parameter | Type | Purpose |
|---|---|---|
| `Speed` | float | Movement speed (Locomotion Blend) |
| `Jump` | bool | Airborne flag |
| `VerticalSpeed` | float | Normalized Y velocity (0=falling, 0.5=idle, 1=rising) |
| `Knockback` | bool | In knockback |
| `Hit` | bool | `HitReactionTicks > 0` |
| `Class` | int | CharacterClass (0–3) — used to branch skill animations |
| `Skill` | int | Currently active skill slot (0 or 1, -1=none) |
| `UseSkill` | Trigger | Skill / basic-attack trigger point (subscribes to ActionPredicted / ActionConfirmed events) |

### Objs/MovingPlatform.prefab

```
MovingPlatform
├── PlatformView (inherits EntityView) — works without it too
├── MeshRenderer / MeshFilter (Cube scale 3×0.5×3)
└── (optional) BoxCollider — visual only, Kinematic
```

### Objs/TrapZone.prefab

```
TrapZone (visual only)
├── MeshRenderer (plane 3×0.1×3, red material)
└── ParticleSystem (trap-threat visualization)
```

**Important**: Trap detection is handled by ECS `SpawnMarkerComponent` (trap tag) + `TrapTriggerSystem`. The prefab's collider is visual only.

### Objs/ItemShield|Boost|Bomb.prefab

```
ItemBoost
├── ItemView (inherits EntityView)
│   └── _renderers = [child meshes]
├── (child) Model — icon mesh (Boost = wings / Shield = shield / Bomb = sphere)
└── (child) PickupEffect (rotation animation, MonoBehaviour)
```

---

## F-6. BrawlerEntityViewFactory.asset Fields

```csharp
[CreateAssetMenu(menuName = "Brawler/EntityViewFactory", fileName = "BrawlerEntityViewFactory")]
public class BrawlerEntityViewFactory : EntityViewFactory
{
    [Header("Character Prefabs (indexed by CharacterClass)")]
    [SerializeField] private GameObject[] _characterPrefabs;  // [0]=Warrior [1]=Mage [2]=Rogue [3]=Knight

    [Header("Item Prefabs (indexed by ItemType)")]
    [SerializeField] private GameObject[] _itemPrefabs;       // [0]=Shield  [1]=Boost [2]=Bomb
}
```

Wire the prefabs into the two arrays in this order in the Inspector.

---

## F-7. BrawlerScene Layout

```
BrawlerScene
├── Main Camera + Cinemachine Brain
│   └── VirtualCamera (Body: 3rd Person Follow, Aim: Composer)
├── Directional Light
├── Global Volume (URP Post-processing)
│
├── [Stage]
│   ├── Ground
│   │   ├── MeshFilter/Renderer (Plane, Scale 40×1×40)
│   │   └── (loaded at runtime — included in StaticColliders.bytes; no scene Collider needed)
│   ├── Wall_North   — Visual only (scale 42×2×2, pos (0,1,20))
│   ├── Wall_South   — Visual only (scale 42×2×2, pos (0,1,-20))
│   ├── Wall_East    — Visual only (scale 2×2×42, pos (20,1,0))
│   ├── Wall_West    — Visual only (scale 2×2×42, pos (-20,1,0))
│   ├── CenterObstacle (3×2×3, pos (0,1,0))
│   ├── TrapZone_West (TrapZone.prefab, pos (-6,0,0))
│   └── TrapZone_East (TrapZone.prefab, pos ( 6,0,0))
│
├── [MovingPlatforms]
│   └── MovingPlatform (MovingPlatform.prefab)
│
├── BrawlerGameController (BrawlerGameController.prefab)
│   ├── UKlothoBehaviour
│   ├── BrawlerGameController
│   ├── BrawlerViewSync
│   ├── EntityViewUpdater
│   └── BrawlerCameraController
│
└── [UI]
    ├── GameMenu (Canvas — Screen Space Overlay)
    ├── GameHUD  (Canvas — Screen Space Overlay)
    └── ResultScreen (Canvas — Screen Space Overlay, initially inactive)
```

---

## F-8. Asset Export Pipeline

### NavMesh
1. Unity `Window > AI > Navigation`
2. Mark Ground / Wall / Obstacle objects as "Navigation Static"
3. Run `Bake` → Unity NavMesh generated
4. `Tools > Klotho > Export NavMesh` → `Samples/Brawler/Data/NavMeshData.bytes`
5. Wire to the `BrawlerGameController._navMeshAsset` TextAsset field

### StaticColliders
1. Tag fixed-geometry objects with `FPStatic`
2. `Tools > Klotho > Export Static Colliders` → `Samples/Brawler/Data/StaticColliders.bytes`
3. Wire to `BrawlerGameController._staticCollidersAsset`

### DataAssets
1. Use `Assets > Create > Brawler > DataAsset > ...` to create the 9 assets ([Brawler.C.DataAssets.md](Brawler.C.DataAssets.md))
2. Fill in field values via the Inspector
3. `Tools > Klotho > Convert > DataAsset JsonToBytes` → `Samples/Brawler/Data/DataAssets.bytes`
4. Wire to `BrawlerGameController._dataAsset`

---

## F-9. Build Settings

Add `Samples/Brawler/Scenes/BrawlerScene` in `File > Build Settings`. Verify the scene index doesn't collide with other test scenes.

---

## F-10. Inspector-Wiring Checklist

| Field | Assigned Target |
|---|---|
| `_staticCollidersAsset` | `Data/StaticColliders.bytes` |
| `_navMeshAsset` | `Data/NavMeshData.bytes` |
| `_dataAsset` | `Data/DataAssets.bytes` |
| `_simulationConfig` | `Config/SimulationConfig.asset` |
| `_uKlotho` | `UKlothoBehaviour` on the same object |
| `_viewSync` | `BrawlerViewSync` on the same object |
| `_viewUpdater` | `EntityViewUpdater` on the same object |
| `_gameMenu` | `GameMenu` in the scene |
| `EntityViewUpdater._factory` | `Config/BrawlerEntityViewFactory.asset` |
| `BrawlerEntityViewFactory._characterPrefabs` | `Prefabs/Characters/Warrior/Mage/Rogue/Knight` |
| `BrawlerEntityViewFactory._itemPrefabs` | `Prefabs/Objs/ItemShield/ItemBoost/ItemBomb` |
| `BrawlerViewSync._gameHUD` | `GameHUD` in the scene |
| `BrawlerViewSync._resultScreen` | `ResultScreen` in the scene |
| `BrawlerViewSync._movingPlatforms` | List of `MovingPlatform`s in the scene |
