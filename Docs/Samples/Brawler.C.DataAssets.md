# Brawler Appendix C — DataAsset Field Catalog

> Related: [Brawler.md](Brawler.md) §4 (Phase 1 — DataAsset definitions)
> Target: full field listings for the 9 asset classes + recommended defaults + AssetId allocation rules
>
> ⚠️ **Note**: Brawler DataAssets are not `ScriptableObject`s but **plain POCOs** (implementing only `IDataAsset`, with `AssetId` injected via the constructor). Values are authored in **JSON files**, then converted via `Tools > Klotho > Convert > DataAsset JsonToBytes` to `.bytes` and loaded at runtime. There is no `[CreateAssetMenu]`; the assets cannot be created via the `Assets > Create > ...` menu. The per-class differences in §C-3 are the **recommended values authored in JSON**; the C# field initializers are the common defaults applied to every instance.

---

## C-1. AssetId Allocation Rules

| AssetId Range | Class | Description |
|---|---|---|
| 1001 | BrawlerGameRulesAsset | Global game rules (singleton) |
| 1100 ~ 1103 | CharacterStatsAsset | 4 character classes (Warrior=1100, Mage=1101, Rogue=1102, Knight=1103) |
| 1200 ~ 1231 | SkillConfigAsset | Per-class skills × 2 slots (1200/1201=Warrior, 1210/1211=Mage, 1220/1221=Rogue, 1230/1231=Knight) |
| 1300 | CombatPhysicsAsset | Combat physics (singleton) |
| 1301 | BasicAttackConfigAsset | Basic attack (singleton) |
| 1400 | ItemConfigAsset | Items (singleton) |
| 1500 | MovementPhysicsAsset | Movement physics (singleton) |
| 1600 | BotBehaviorAsset | Common bot-behavior parameters (singleton) |
| 1700 ~ 1702 | BotDifficultyAsset | Easy(1700) / Normal(1701) / Hard(1702) |

> **Convention**: For 1000-range IDs, the leading two digits encode category (11=Character, 12=Skill, 13=Combat, 14=Item, 15=Move, 16=BotBehavior, 17=BotDifficulty); the trailing two digits encode the instance index.

---

## C-2. Asset Definition Pattern

Brawler DataAssets are **plain POCOs, not Unity `ScriptableObject`s** — they only implement `IDataAsset` and use `partial class` + `[KlothoDataAsset(typeId)]`. `AssetId` is injected via the constructor and distinguishes instances. The source generator emits `Serialize / Deserialize / GetHash` automatically.

```csharp
[KlothoDataAsset(100)]
public partial class CharacterStatsAsset : IDataAsset
{
    public int AssetId { get; }

    [KlothoOrder(0)] public int  PrototypeId;
    [KlothoOrder(1)] public FP64 MoveSpeed;
    [KlothoOrder(2)] public FP64 Mass               = FP64.One;
    [KlothoOrder(3)] public FP64 Friction           = FP64.FromDouble(0.5);
    // ... (defaults specified via C# field initializers)

    public CharacterStatsAsset(int assetId)
    {
        AssetId = assetId;
    }
}
```

**Key differences**:
- Does not inherit `ScriptableObject` → **cannot create `.asset` files in the Unity Project window**
- No `[CreateAssetMenu]` → no `Assets > Create > ...` menu entry
- No `_assetId` SerializeField → **the `assetId` field in the JSON is passed to the constructor on deserialization**
- No Inspector editing → values are edited in the JSON text file

---

## C-3. Full Field Listings (9 assets)

### C-3-1. CharacterStatsAsset (typeId=100)

**AssetId: 1100(Warrior) / 1101(Mage) / 1102(Rogue) / 1103(Knight)**

| Order | Field | Type | Warrior | Mage | Rogue | Knight |
|---|---|---|---|---|---|---|
| 0 | PrototypeId | int | 100 | 101 | 102 | 103 |
| 1 | MoveSpeed | FP64 | 5.0 | 4.0 | 6.0 | 3.5 |
| 2 | Mass | FP64 | 1.0 | 0.8 | 0.9 | 1.3 |
| 3 | Friction | FP64 | 0.5 | 0.5 | 0.3 | 0.6 |
| 4 | ColliderRadius | FP64 | 0.5 | 0.5 | 0.5 | 0.5 |
| 5 | ColliderHalfHeight | FP64 | 0.5 | 0.5 | 0.5 | 0.5 |
| 6 | ColliderOffsetY | FP64 | 1.0 | 1.0 | 1.0 | 1.0 |
| 7 | Skill0Id | int | 1200 | 1210 | 1220 | 1230 |
| 8 | Skill1Id | int | 1201 | 1211 | 1221 | 1231 |

### C-3-2. SkillConfigAsset (typeId=101)

**AssetId: 1200~1231**

| Order | Field | Type | Meaning |
|---|---|---|---|
| 0 | Cooldown | int (tick) | Reuse cooldown |
| 1 | ActionLockTicks | int | Action-lock (movement-suppression) ticks |
| 2 | MoveSpeedOrRange | FP64 | Dash speed or projectile range |
| 3 | RangeSqr | FP64 | Hit-detection distance² |
| 4 | KnockbackPower | int | Knockback magnitude |
| 5 | EffectRadius | FP64 | Visual VFX radius |
| 6 | AuxDurationTicks | int | Auxiliary duration (Shield, etc.) |
| 7 | ImpactOffsetDist | FP64 | Impact-point offset distance |

For per-skill defaults, see [Brawler.A.Skills.md](Brawler.A.Skills.md) §A-3.

### C-3-3. BasicAttackConfigAsset (typeId=102)

**AssetId: 1301 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | MeleeRangeSqr | FP64 | 4.0 | Melee hit-detection distance² |
| 1 | BasePower | int | 10 | Base knockback |
| 2 | ActionLockTicks | int | 15 | Post-attack lock ticks |
| 3 | HitStunTicks | int | 6 | Victim hit-stun ticks |

### C-3-4. BotBehaviorAsset (typeId=103)

**AssetId: 1600 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | StageBoundary | FP64 | 18.0 | Boundary the bot treats as dangerous (inside the actual ±20 boundary) |
| 1 | ChaseStopDistance | FP64 | 1.5 | Stop distance during target chase |
| 2 | NavSnapMaxDist | FP64 | 3.0 | Max NavMesh snap distance |
| 3 | EvadeCooldownTicks | int | 300 | Re-evade cooldown |
| 4 | EyeHeight | FP64 | 1.5 | Height of LOS rays |
| 5 | TargetScoreKnockbackFactor | FP64 | 10.0 | Target-score multiplier for knockback |
| 6 | TargetScoreStockFactor | FP64 | 100.0 | Target-score multiplier for stocks |
| 7 | EvadePoints | FPVector3[] | 8 coords | Candidate destinations during evade |

**EvadePoints defaults**:
```
(-5, 0, -5), ( 5, 0, -5), (-5, 0,  5), ( 5, 0,  5),
( 0, 0, -5), ( 0, 0,  5), (-5, 0,  0), ( 5, 0,  0)
```

### C-3-5. BotDifficultyAsset (typeId=104)

**AssetId: 1700(Easy) / 1701(Normal) / 1702(Hard)**

| Order | Field | Type | Easy | Normal | Hard |
|---|---|---|---|---|---|
| 0 | DecisionCooldown | int (tick) | 30 | 20 | 10 |
| 1 | AttackCooldownBase | int | 60 | 40 | 20 |
| 2 | EvadeMargin | FP64 | 1.0 | 2.0 | 3.0 |
| 3 | EvadeKnockbackPct | int | 120 | 80 | 50 |
| 4 | SkillExtraDelay | int | 60 | 30 | 10 |

### C-3-6. BrawlerGameRulesAsset (typeId=105)

**AssetId: 1001 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | GameDurationSeconds | int | 120 | Time limit (seconds) |
| 1 | StageBoundsSize | FP64 | 40.0 | XZ boundary size (±20) |
| 2 | FallDeathY | FP64 | -10.0 | Fall-death Y threshold |
| 3 | RespawnTicks | int | 120 | Respawn wait (ticks) |
| 4 | CharacterSpawnY | FP64 | 0.0 | Spawn Y position |
| 5 | SpawnPositions | FPVector3[] | 4 coords | Per-player spawn |

**SpawnPositions defaults**:
```
(-4, 0, -4),   // Player 0
( 4, 0, -4),   // Player 1
(-4, 0,  4),   // Player 2
( 4, 0,  4),   // Player 3
```

### C-3-7. CombatPhysicsAsset (typeId=106)

**AssetId: 1300 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | DefaultKnockbackDurationTicks | int | 20 | Default knockback duration |
| 1 | HitReactionDurationTicks | int | 10 | Hit-reaction ticks |
| 2 | PushDurationTicks | int | 10 | Push duration |
| 3 | BodyRadiusSqr | FP64 | 1.0 | Inter-character collision radius² |
| 4 | ContactPower | int | 5 | Base contact knockback |
| 5 | ShieldDurationTicks | int | 100 | Shield-item duration |
| 6 | BoostDurationTicks | int | 100 | Boost-item duration |
| 7 | BombRadiusSqr | FP64 | 9.0 | Bomb radius² |
| 8 | BombBasePower | int | 15 | Bomb base knockback |
| 9 | BombImpulse | FP64 | 15.0 | Bomb instantaneous impulse |

### C-3-8. ItemConfigAsset (typeId=107)

**AssetId: 1400 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | SpawnIntervalTicks | int | 200 | Spawn period (5 s at 40 ticks/s) |
| 1 | MaxItems | int | 4 | Max simultaneous items on the map |
| 2 | ItemLifetimeTicks | int | 600 | Auto-despawn if not picked up (15 s) |
| 3 | SpawnMinRange | FP64 | 4.0 | Spawn radius (min) |
| 4 | SpawnMaxRange | FP64 | 8.0 | Spawn radius (max) |
| 5 | BoostSpeedMultiplier | FP64 | 1.5 | Speed multiplier while Boost is active |
| 6 | PickupRadiusSqr | FP64 | 0.4 | Pickup-detection distance² |

### C-3-9. MovementPhysicsAsset (typeId=108)

**AssetId: 1500 (singleton)**

| Order | Field | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | JumpSpeed | FP64 | 8.0 | Y velocity on jump |
| 1 | GravityAccel | FP64 | 20.0 | Gravitational acceleration |
| 2 | MinMoveSqr | FP64 | 0.0001 | Stationary-threshold velocity² |
| 3 | SkinOffset | FP64 | 0.3 | Ground-ray start offset |
| 4 | MaxFallProbe | FP64 | 5.0 | Max ground-probe distance while falling |
| 5 | GroundEnterDepth | FP64 | 0.19 | Air → ground entry threshold |
| 6 | GroundSnapDepth | FP64 | 0.05 | Force-snap-to-ground threshold |

---

## C-4. Registry Build & Injection

`BrawlerGameController.Start()` loads the assets and builds the registry:

```csharp
_dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

IDataAssetRegistryBuilder builder = new DataAssetRegistry();
builder.RegisterRange(_dataAssets);
_assetRegistry = builder.Build();

_session = KlothoSession.Create(new KlothoSessionSetup {
    AssetRegistry = _assetRegistry,   // ← injected as Frame.AssetRegistry
    // ...
});
```

Or register individually:
```csharp
builder.Register(warriorStats);    // AssetId=1100
builder.Register(warriorSkill0);   // AssetId=1200
// ...
```

---

## C-5. Asset Build Pipeline

Brawler DataAssets use a **JSON → .bytes conversion pipeline**, not Unity `.asset` files.

1. **Author the JSON file** — describe the asset instances as a JSON collection: type tag (`$type`) + `assetId` + field values.

    ```json
    [
      {
        "$type": "Brawler.CharacterStatsAsset",
        "assetId": 1100,
        "PrototypeId": 100,
        "MoveSpeed": 5.0,
        "Mass": 1.0,
        "Friction": 0.5,
        "Skill0Id": 1200,
        "Skill1Id": 1201
      },
      { "$type": "Brawler.SkillConfigAsset", "assetId": 1200, "Cooldown": 60, "ActionLockTicks": 20, "MoveSpeedOrRange": 0, "RangeSqr": 4.0, "KnockbackPower": 20 },
      // ... remaining instances
    ]
    ```

    For the JSON serialization conventions, see the DataAsset section of [Docs/FEATURES.md](../FEATURES.md) and the converters in the `xpTURN.Klotho.DataAsset.Json` assembly (FP64JsonConverter, FPVector2/3JsonConverter, etc.).

2. **JSON → .bytes conversion** — Select the `.json` `TextAsset` in the Project window and run `Tools > Klotho > Convert > DataAsset JsonToBytes`. A `.bytes` file is created in the same folder. Internally it runs `DataAssetJsonConverter.ConvertMixedJsonToBytes(json)` → `DataAssetWriter.SaveToFile(path, bytes)`.

3. **Wire up the TextAsset** — Assign the generated `.bytes` to the `BrawlerGameController._dataAsset` (TextAsset field) in the Inspector.

4. **Runtime load** — In `BrawlerGameController.Start()`:
    ```csharp
    var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);
    IDataAssetRegistryBuilder builder = new DataAssetRegistry();
    builder.RegisterRange(dataAssets);
    _assetRegistry = builder.Build();
    ```

For determinism, assets are immutable at runtime. The `.bytes` payload must be identical across all peers (the JSON source is for editing only and is never sent over the network — every peer is assumed to have received the same `.bytes` ahead of time).
