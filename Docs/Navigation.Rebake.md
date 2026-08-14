# Runtime NavMesh Rebake

Place and remove buildings **during a match** and have the NavMesh change to match — deterministically, on every peer, with no authored variants.

Supported footprints: **AABB** (axis-aligned box), **OBB** (oriented box, quantized to `M`
directions) and **circle**, approximated by a hexagon so that it can still be packed.

> **Prerequisite**: [Navigation.md](Navigation.md) covers the static NavMesh runtime — query,
> pathfinding, agents, ORCA. This document is about changing that NavMesh mid-match.
>
> **Units**: one world unit is **one metre**. Sizes on the shape API are in **snap units**, 1024 to
> a metre — so a snap unit is just under a millimetre.

---

## 1. What problem this solves

A baked NavMesh is fixed at build time. When a player puts a building down, agents have to stop
routing through it — and every peer has to agree, exactly.

The rebaker cuts the building's footprint out of the walkable region and re-triangulates what is
left, so the space around the building stays walkable:

```text
before                          after placing a building
┌───────────────────────┐       ┌───────────────────────┐
│                       │       │        ┌────┐         │
│      one big          │  ──▸  │  ╱─────┤bldg├─────╲   │
│   corridor triangle   │       │ ╱      └────┘      ╲  │
└───────────────────────┘       └───────────────────────┘
                                 the rest stays walkable
```

Everything runs on integer arithmetic, so every peer computes the **same** mesh from the same
command — bit for bit, on Unity, Godot and the .NET server alike.

Two simpler-looking approaches do not work, and it is worth knowing why before you try them:

- **Marking the covered triangles unwalkable.** NavMesh triangles are large — one can span a whole
  corridor. Blocking the triangle a small building sits on closes a path that is still mostly clear.
- **Re-running Unity's or Godot's baker.** Those use floating-point maths, which can give slightly
  different answers on different machines. Two players would end up with different NavMeshes, and
  the match would desync.

---

## 2. Quick start

Three steps: build a context once at load, rebake when the building set changes, swap at a tick boundary.

```csharp
// 1) ONCE per match, at load time (not on the tick thread — see the performance table).
//    The context holds the stage's immutable snapshot plus this room's work buffers.
FPNavMeshRebakeContext rebake = FPNavMeshRebaker.CreateContext(baseNavMesh, logger);

// 2) When the building set changes — from the deterministic command stream, never from input.
//    The rebake IS the validation: no separate "can I place here?" call to keep in sync.
var buildings = new[]
{
    new FPBuildingRect(FP64.FromInt(2), FP64.FromInt(3),      // min x, z
                       FP64.FromInt(4), FP64.FromInt(5),      // max x, z
                       FP64.Zero),                            // y
};

//    A refused placement is a return value — the player caused it and the player should see it.
//    A malformed request still throws, because that one is your bug and must not reach them.
if (!FPNavMeshRebaker.TryRebake(
        rebake, buildings, out FPNavMesh newMesh, out FPBuildingRejectionInfo rejection, logger))
{
    ShowToPlayer(rejection.Reason);   // every peer reaches the same verdict
    return;
}

// 3) Swap it in, reseed the agents, then tell the context the swap happened.
agentSystem.SwapNavMesh(newMesh);    // rebinds the query/pathfinder/funnel it already holds
agentSystem.ReseedAgents(ref frame, agentEntities, agentCount);
rebake.CommitSwap(newMesh);          // recycles the retired mesh's storage
```

**Always pass the full current building list, not just the new one.** The rebaker starts from the
original stage mesh every time and carves the whole list out of it. Removing a building therefore
means rebaking with a shorter list.

This matters because the result never depends on how you got there: a carved mesh is never carved
again, so small errors cannot pile up over a long match.

Internally the rebaker reuses what the previous rebake already worked out, which makes it much
faster. The mesh you get is identical either way, so this is not something you manage —
[7. Performance](#7-performance) covers the one thing you can do to help it.

---

## 3. How it works

You only ever supply two things: the stage's original NavMesh, and the current list of buildings.

```text
   base NavMesh  ─┐
                  ├──▸  rebake  ──▸  new NavMesh  ──▸  swap it in
   building list ─┘
```

Three things happen inside that are worth knowing about, because they affect what you pass:

- **Buildings are checked, never adjusted.** A placement that will not work is refused and the
  original mesh is left alone — see [4.5](#45-what-gets-rejected).
- **The agent radius is added for you.** You give the building's real size; the rebaker widens the
  carved hole so agents do not clip the wall. That is why a flush neighbour's offset has to be asked
  for rather than derived from the size you authored ([4.3](#43-placing-shapes-flush)).
- **The result is an ordinary NavMesh.** It goes through the same final assembly the offline baker
  uses and inherits the same bake settings, so nothing downstream can tell the two apart.

---

## 4. Concepts

### 4.1 The placement grid

Building positions must sit on a grid of **1/1024 of a metre**. Anything else is refused.

```csharp
FP64 x = FPGeoPredicates.Quantize(rawCursorX);   // put it on the grid
bool ok = FPGeoPredicates.IsOnGrid(x);           // or just check
```

**Quantize early — before the position goes into the command**, not on the way into the rebaker.

If you quantize late, you end up with two slightly different positions: the one your game stored,
and the one the NavMesh was actually carved from. Nothing warns you about this. It shows up much
later as buildings that no longer line up flush, because your offsets were calculated from the
wrong one.

Two things that catch people out:

- **It is 1/1024 m, not 1 m.** Whole metres are on the grid, so rounding to them does work — but
  then buildings can no longer sit flush against each other, because the gaps between them are
  almost never a whole number of metres.
- **Only binary fractions are on it.** `0.5`, `0.25`, `0.125` yes; `0.1`, `0.3`, `2.7` no.

> **Why 1/1024 rather than a millimetre?** The engine's numbers are binary, and 1/1024 is an exact
> binary fraction while 1/1000 is not. An exact one converts back and forth with no rounding at all,
> which is what keeps every machine in agreement. It is also slightly finer than a millimetre.

### 4.2 Supported footprint types

| Type | How you describe it | Can be packed flush? |
| --- | --- | --- |
| **AABB** | `FPBuildingRect` — no catalog needed | Yes |
| **OBB** | Catalog shape from `AddObb`, in one of `M` directions | Yes — within one orientation |
| **Circle** | Catalog shape from `AddHexagon` | Yes |

Packing works **within** an orientation: two boxes turned the same way meet exactly, two boxes
turned differently do not. "Circle" means a hexagon — that is the roundest footprint that can still
be packed ([4.3](#43-placing-shapes-flush) has the numbers).

**A rectangle needs no catalog.** Give the footprint in metres; the rebaker adds the agent radius
itself.

```csharp
new FPBuildingRect(minX, minZ, maxX, maxZ, y)
//                 └── one corner ──┘  └── the opposite one ──┘
```

The four coordinates pair by **corner**, not by axis. The other common convention is
`minX, maxX, minZ, maxZ`, and the same four numbers under that reading give a different rectangle
with nothing to complain about — so it is worth a glance the first time.

**Everything else comes from a catalog** — one table per stage, holding every size and type the game
can place. A placement then names a shape in that table, a turn of it, and a centre.

#### Build the table, once at load

```csharp
// Sizes are in snap units — 1024 to a metre.
var b = new FPBuildingShapeCatalogBuilder();
int smallBox  = b.AddObb(halfWidth:  512, halfDepth:  512, directions: 16);
int largeBox  = b.AddObb(halfWidth: 2048, halfDepth: 1024, directions: 16);
int smallDisc = b.AddHexagon(circumradius: 1024);
int largeDisc = b.AddHexagon(circumradius: 3072);
FPBuildingShapeCatalog catalog = b.Build();

var rebake = FPNavMeshRebaker.CreateContext(baseMesh, logger, prewarm: true, shapeCatalog: catalog);
```

Keep those ids where the game can name them — a bare `1` in a command payload is not usable.

| Parameter | Meaning | `512` is… |
| --- | --- | --- |
| `halfWidth`, `halfDepth` | Half extents of the box, before the agent-radius expansion — `512, 512` is a 1 m × 1 m footprint | 0.5 m |
| `circumradius` | Radius of the circle the hexagon approximates; the six vertices sit on it | 0.5 m |
| `directions` | Steps around a **full circle** — `16` puts them 22.5° apart. Must be a multiple of 4 | — |

For a hexagon the rest follows from `circumradius`:

| | Formula | At `1024` (1 m) |
| --- | --- | --- |
| Across the points | `2 × circumradius` | 2.00 m |
| Across the flats | `circumradius × √3` | 1.73 m |
| Inradius (centre to a flat edge) | `circumradius × √3 / 2` | 0.87 m |

The realised circumradius is the requested one rounded down to an even number of snap units — steps
of about 2 mm.

#### Place one

```csharp
const int Directions = 16;        // the same M that was passed to AddObb

// Rotate button — step around the circle and wrap.
orientation = (orientation + 1) % Directions;

// Quantize BEFORE the value becomes the command payload (see 4.1).
FP64 cx = FPGeoPredicates.Quantize(cursorWorldX);
FP64 cz = FPGeoPredicates.Quantize(cursorWorldZ);

var placement = new FPBuildingPlacement(largeBox, orientation, cx, cz, groundY);

// A shape that does not turn — such as a hexagon — can leave the orientation out.
var disc = new FPBuildingPlacement(smallDisc, otherX, otherZ, groundY);

FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(rebake, new[] { placement, disc }, logger);
```

#### Reuse the buffer — do not build an array per placement

The `new[]` above is fine for a one-off. In a command handler it is not: it puts a fresh array on
the heap on every placement, directly in front of a rebaker built to allocate nothing
([7. Performance](#what-each-choice-costs-you)). Keep one buffer for the life of the room, sized to
your building cap, and tell the rebaker how much of it is live:

```csharp
readonly FPBuildingPlacement[] _scratch = new FPBuildingPlacement[MaxBuildings + 1];

// Your own: writes the buildings currently in frame state into the buffer, in
// placement order, and returns how many it wrote.
int count = CollectBuildings(ref frame, _scratch);
_scratch[count++] = new FPBuildingPlacement(largeBox, orientation, cx, cz, groundY);

FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(
    rebake, _scratch, logger, rules, placementCount: count);
```

Three things to know about `placementCount`:

- **It is how many entries are live.** Anything past it is ignored. The rebaker does not clear the
  rest of the buffer, so leftovers from last time are simply not read.
- **Leave it out and the array's full length is used instead.** That is the trap: a 33-slot buffer
  holding 5 buildings would rebake all 33 slots, including 28 stale ones. Always pass the count when
  you reuse a buffer.
- **Size the buffer one bigger than your cap** if you append before checking it. A cap of 32 needs
  33 slots.

`-1` means "use the whole array" and is the only negative accepted; anything else is refused. That
refusal is deliberate. A count that came out negative means your own arithmetic went wrong, and
quietly treating it as "the whole array" would rebake the stale tail — buildings the *previous*
rebake accepted. Every peer would do the same thing, so the match would not desync and nothing would
ever report it. You would just have the wrong map.

**Rectangles take the same argument**, under the name the parameter counts:

```csharp
FPNavMesh mesh = FPNavMeshRebaker.Rebake(rebake, _rectScratch, logger, rules, buildingCount: count);
```

#### Put the next one flush against it

Ask the expansion where the neighbour goes rather than guessing an offset — the gap depends on the
*expanded* shape, not the drawn one.

```csharp
// Both boxes must be the SAME orientation; two boxes turned differently do not meet.
rebake.ShapeExpansion.TryTilingDelta(
    largeBox, existing.Orientation, edge: 0, out long dx, out long dz);

var neighbour = new FPBuildingPlacement(
    largeBox, existing.Orientation,
    existing.CentreX + FPGeoPredicates.Unsnap(dx),
    existing.CentreZ + FPGeoPredicates.Unsnap(dz),
    groundY);
```

No quantizing needed here: the delta is a whole number of snap units, so a centre already on the
grid stays on it, however far the pattern is extended. For free-hand placement that still snaps to
the pattern, use `TrySnapToLattice` ([4.3](#43-placing-shapes-flush)).

#### Things to know before you ship a rotate button

- **`directions` is the whole circle, not a half.** `16` means 22.5° per step, not 11.25°.
- **A box looks the same after a half turn**, so orientation `k` and `k + M/2` are the same
  footprint. 16 directions give a player **8 distinguishable shapes**, each reachable two ways —
  fine for a rotate button, surprising if you were building a palette of 16 previews.
- **Multiple of 4 is required** so that four quarter-turns land back on the exact starting integers.
  A box that drifted a snap unit per turn would lose flush contact with its neighbours, and you
  would see it as a placement rejection long after the turn that caused it.
- **The order of the `Add…` calls decides the ids, so it must never change by accident.** A stored
  placement refers to a shape by id. Reorder the calls and every stored placement now points at a
  different shape, with nothing to warn you. Write the table as plain straight-line code — never
  from a loop over a dictionary, a scanned folder, or a config file, because those can come back in
  a different order on another machine. (`catalog.Hash` is your safety net: see
  [6. Determinism](#6-determinism).)
- **Use `AddHexagon`; do not build a hexagon yourself.** A regular hexagon has no integer form, and
  the obvious route (compute the vertices, pass them through `Snap`) loses the ability to pack —
  `Snap` is floor, which is not symmetric about the centre. It still carves perfectly, so the loss
  only shows up much later, the first time someone builds wall to wall.

Raw offsets are available as `ObbOffsets` / `HexagonOffsets` if you are assembling a table without
the builder ([9. API reference](#9-api-reference)).

### 4.3 Placing shapes flush

Two shapes meet exactly when their centres differ by a **tiling delta**. Ask for it — it is computed
from the *expanded* footprint, so you cannot derive it from the size you authored.

```csharp
FPBuildingShapeExpansion exp = rebake.ShapeExpansion;

// Where does the neighbour across edge 0 go?
exp.TryTilingDelta(shape, orientation, edge: 0, out long dx, out long dz);

// Or: snap a free-hand position onto the shape's own lattice.
exp.TrySnapToLattice(shape, orientation, cursorX, cursorZ, out FP64 x, out FP64 z);
```

**Only boxes and hexagons can be packed.** Those two are the shapes that tile a plane with no gaps.
Ask `catalog.TilesThePlane(shape, orientation)` if you are unsure.

Rounder shapes — an octagon, a 16-gon — cannot. They will give you one flush neighbour and look
fine, but keep building outward in two directions and the copies start to overlap. Such a shape is
still perfectly placeable and carves correctly; it just is not something a player can pack wall to
wall.

### 4.4 Building wall to wall

By default two buildings may not even graze each other. Wall-to-wall packing is a **game rule**, so
turn it on explicitly:

```csharp
var rules = new FPBuildingPlacementRules(allowBuildingTouch: true);
FPNavMeshRebaker.Rebake(rebake, buildings, logger, rules);
```

With the flag on, a shared edge, a shared corner and a collinear overlap are accepted and carved as
one merged obstacle.

Two things stay rejected whatever the flag says:

- **Overlapping interiors**, including one building nested inside another — nesting would turn the
  inner shape back into walkable and hand you a building with a courtyard in it.
- **Touching the walkable boundary.** A footprint that shares an edge with the outer edge of the
  mesh would be accepted while carving *nothing*, which is worse than a rejection.

> Whichever way you set the flag, it must be the same on every peer **and in every replay** — a
> build that flips it diverges from recordings made before. Source it from inside the determinism
> envelope, not from a local config file.

### 4.5 What gets rejected

**The rebake is the validation**, and every peer reaches the same verdict on the same input.

There is no *second* set of rules anywhere — but there is a way to ask the question early, for a
placement ghost. See [Showing the player before they click](#46-showing-the-player-before-they-click).

**A refused placement comes back as a value.** It is what a player causes by pointing somewhere
they cannot build, so it is a return value, not an exception:

```csharp
if (!FPNavMeshRebaker.TryRebakePlacements(
        rebake, placements, out FPNavMesh mesh, out FPBuildingRejectionInfo rejection,
        logger, rules, placementCount: count))
{
    ShowToPlayer(rejection.Reason);   // branch on the value, do not match the message
    return;
}
```

| `rejection.Reason` | Meaning | Indices |
| --- | --- | --- |
| `BuildingsOverlap` | Two buildings are too close once the agent radius is added. This also covers buildings that merely touch, when your rules forbid touching — the check cannot tell the two cases apart, so tell the player "too close" rather than "overlapping". | `IndexA`, `IndexB` |
| `TouchesWalkableBoundary` | The footprint reaches the edge of the walkable region. | `IndexA` |
| `OutsideWalkableRegion` | The footprint is not on the mesh at all. | `IndexA` |
| `SwallowsBakedHole` | It swallowed a pillar or other baked hole, which would flip that hole back to walkable. | `IndexA`, and `Site` — *which* hole, which a building index alone cannot say |
| `EmptyWalkableRegion` | Nothing walkable was left. **No placement can cause this** — every building is already checked to sit strictly inside the region, so a gap always survives. If you do see it, the base mesh itself is degenerate. | — |

`Rebake` / `RebakePlacements` do exactly the same thing but throw an `InvalidOperationException`
instead of returning `false`. Use those for one-off work such as tests or tooling, and the `Try…`
pair anywhere a player is placing buildings.

**A malformed request still throws, and that is the point.** These are `ArgumentException`, and
they mean the call should never have been built:

| Refusal | Meaning |
| --- | --- |
| `placement b centre is not on the predicate snap grid` | Quantize it — see [The placement grid](#41-the-placement-grid). |
| `placement b names shape s, and this catalog holds n` | No such shape — usually a stale id from before the table changed. |
| `placement b names orientation k of shape s, which turns n ways` | The shape exists; that turn of it does not. |
| `this stage's snapshot was built without a shape catalog` | `CreateContext` was called without `shapeCatalog`. |
| `building b is degenerate (min >= max)` | A zero-area rect. **Reachable from a real gesture** — a drag-to-size UI produces one on a click without a drag, so filter it before you call. |
| `building b (expanded) outside the snapped domain` | Past the coordinate bound. Also reachable from a gesture — dragging far off the map. |
| `placement b (expanded) outside the snapped domain` | The same, for a catalog placement — the centre plus the shape's own extent runs past the bound. |
| `building b footprint could not be offset by the bake radius` | A **catalog** shape that cannot be widened by the agent radius (degenerate or collinear). It throws on every placement that names it, so the fix is in the table, not the call. |
| `placementCount` / `buildingCount` `n exceeds the array's m` | The live count is longer than the buffer it indexes into. |
| `…Count n is negative` | Only `-1` means "the whole array" — see [Reuse the buffer](#reuse-the-buffer--do-not-build-an-array-per-placement). |

**Keep the two apart.** A refusal is the map answering the player, and the player should see it. An
`ArgumentException` is a bug in your code. If you handle both in the same branch, your bug reaches
the player as "you cannot build there" — on every machine, identically, with nothing left to notice
it by.

Two rows above deserve a second look: **the degenerate rectangle and the off-map one are your bugs,
yet an ordinary player gesture produces them** — a click with no drag, or a drag past the edge of
the map. Filter those in your placement UI rather than catching the exception.

If you do catch, catch `ArgumentException` specifically. Do **not** catch `Exception`: a null
context or a corrupt catalog throws something else entirely, and a blanket catch hides it.

A rejection is a normal outcome, not an error: reject the command, raise a UI event, change nothing.

#### The stage itself can be refused

Two more, and these are not placement rejections. They come from `CreateContext` / `CreateSnapshot`
and mean the asset cannot be rebaked at all:

| Refusal | Type | Meaning |
| --- | --- | --- |
| `base mesh is not predicate-grid snapped` | `NotSupportedException` | The asset's vertices are not on the placement grid — re-export it. |
| `base mesh has XZ-duplicate vertices (multi-level)` | `NotSupportedException` | Stacked floors — see [Limits](#8-limits). |

Handle these at load time, where you can still fall back to running that stage without runtime
building at all. If one of them shows up in a command handler instead, it means you built the
context lazily on the first placement — build it at load.

### 4.6 Showing the player before they click

A placement UI needs an answer as the cursor moves, and the rebake cannot give it: it costs
milliseconds and must run from the command stream, not from input. So ask the same question
without the carving:

```csharp
// Each frame the ghost moves. Nothing to set up and nothing to keep —
// `rebake` is the context you already hold, and it knows what it carved.
bool canBuild = FPNavMeshRebaker.TryValidateOne(
    rebake, ghost, out FPBuildingRejectionInfo why);

ShowGhost(canBuild ? Colour.Green : Colour.Red);
if (!canBuild) ShowHint(why.Reason);
```

**It is the same verdict, not a guess.** Every reason a player can be refused is decided before
any triangle is cut, so this runs the rebake's own validation and stops there. It checks the ghost
against whatever the last successful rebake through that context carved — you do not hand it a
building list, and there is no list to get out of step. `TryValidateOnePlacement` takes a catalog
placement instead; the ghost's shape need not match what is already on the map.

The `rules` argument is optional and defaults to **the ones that rebake ran under**, which is what
keeps the two answers together. Pass a value only when you mean something different — "what would
this look like if contact were allowed", say.

Two things to get right:

- **Preview from the same thread that rebakes, one previewer per context.** The ghost is written
  into storage the context owns, so a second previewer, or a rebake running alongside, corrupts it.
  In a client that is usually true already.
- **Green is optimistic, not final.** Another player may build there between the preview and your
  command reaching the tick. Keep handling the refusal — in a multiplayer build that is a normal
  outcome, not an edge case.

**Do not skip the rebake because the preview passed.** The preview runs locally, from input, on
whatever the local peer currently believes. The rebake runs from the command stream, which is the
only place every peer sees the same list — that is what makes the verdict identical everywhere.

#### If you rebake without a context

The stateless `Rebake(baseMesh, …)` overloads leave nothing to remember, so those callers pass the
building list themselves:

```csharp
// Both kept, not rebuilt per frame. `_placed` is the same array you hand the rebake.
readonly FPBuildingPreviewScratch _preview = new FPBuildingPreviewScratch();
FPBuildingRect[] _placed; int _placedCount;

bool canBuild = FPNavMeshRebaker.TryValidateOne(
    snapshot, _placed, _placedCount, ghost, out FPBuildingRejectionInfo why,
    _preview, rules);          // ← the SAME rules you pass to the rebake
```

Three extra things apply to this form, and they are why the context form exists:

- **Pass the same `rules`.** Nothing defaults them for you here. The default forbids contact, so a
  game that allows wall-to-wall building but omits the argument paints red over ground the player
  could actually build on — and nothing reports that.
- **The list must be one the rebaker already accepted.** That is what lets it check one building
  instead of all of them, and nothing checks it — a list that was never accepted gets a confident
  wrong answer rather than an error.
- **Keep the array; do not build one per frame.** The engine allocates nothing here, and the list
  costs it almost nothing — going from no buildings to 128 adds 0.003 ms, because the expensive
  part is one walk of the base mesh's edges and that happens once whatever the list length. But
  `ToArray()` on a query result every time the cursor moves is a few KB per frame on the
  *caller's* side.


---

## 5. Lifecycle and threading

| Object | Lifetime | Sharing |
| --- | --- | --- |
| `FPNavMeshRebakeSnapshot` | One per **stage**, built at load | Immutable — safe to share read-only across rooms and threads |
| `FPNavMeshRebakeContext` | One per **room** | Mutable (holds work buffers) — **never** share between rooms |
| `FPNavMesh` (rebaked) | Until the swap after next | Read-only once installed |

Two rooms running the same stage share one snapshot and build one context each.

**Swap at a tick boundary, before agents update.** `SwapNavMesh` rebinds the query, pathfinder and
funnel. **It does not reseed the agents — that call is yours,** and it is not optional: every agent
is still holding a triangle index and corridor that point into the mesh you just replaced, and both
are hashed frame state. Skipping `ReseedAgents` desyncs the peer rather than merely misplacing
somebody.

**`CommitSwap` is not optional.** It is what tells the context which mesh is live, and two things
hang off that: the *previous* mesh's arrays can be recycled, and the next rebake can reuse most of
it instead of rebuilding. Skip it and you pay full price for both — see
[7. Performance](#what-each-choice-costs-you).

> Do not cache an `FPNavMesh` across a swap — read the current one from `FPNavAgentSystem.CurrentMesh`
> (or your provider) each time. A retired mesh's storage becomes another mesh's; DEBUG builds detect
> a read of one.

---

## 6. Determinism

The geometry is exact integer arithmetic, so the mesh is bit-identical across runtimes by
construction. Three things are yours to keep aligned:

1. **Call it from the command stream only** — same tick, same order, on every peer. Sort the
   building list by a stable key too; **placement order** is the one to pick. Any deterministic key
   is equally correct, but this one decides how often the rebaker can take its shortcut — 96% of
   rebakes against 18% — see
   [7. Performance](#keep-the-building-list-in-placement-order).
2. **The placement rules value.** See [Building wall to wall](#44-building-wall-to-wall).
3. **The shape catalog.** A placement is a *reference* into the table, so two builds that disagree
   about the table carve different meshes from identical commands. Carry `catalog.Hash` in your
   match config so a mismatch fails at load instead of surfacing later.

**Cross-checking.** The NavMesh is not part of the state hash — it is large, and rebuilding it is
deterministic anyway. Instead, `FPNavMeshRebaker.ComputeFingerprint(mesh)` gives you a small value
peers can compare. Implement `INavFingerprintSource` and the engine compares it for you.

**Late join and rollback.** Store the building set as frame state (ECS components), not in a
system-local list — a joining peer restores the components and rebakes once from them. A
system-local list cannot be reproduced without replaying the whole command history.

---

## 7. Performance

Measured on an Apple Silicon laptop, .NET 8, Release. **Field** is a real shipped asset: 22,321
triangles, 17,142 vertices. Every figure below comes from one fixture, so you can reproduce them:

```
dotnet test -c Release --filter FullyQualifiedName~FPNavMeshRebakerPerfTests
```

### The cost of placing a building

The timed event is the rebake that **adds** the Nth building. Re-running an unchanged set is much
cheaper — nothing to carve differently — so it would not be a useful number here.

| Placing building # | min | allocation |
| ---: | ---: | ---: |
| 1 | 4.91 ms | 9.3 KB |
| 2 | 4.98 ms | 8.7 KB |
| 4 | 5.14 ms | 10.2 KB |
| 8 | 5.52 ms | 20.6 KB |
| 16 | 6.20 ms | 29.7 KB |
| 32 | 7.73 ms | 44.0 KB |

The base mesh dominates; each building already on the map adds roughly **0.09 ms**.

**Installing the result is free**, so the numbers above are the whole cost of a placement. Swapping
the mesh in allocates nothing: the query, pathfinder and funnel are rebound rather than rebuilt, and
the ORCA obstacle re-extraction reuses its buffers across swaps.

| Asset | Triangles | Rebake | `SwapNavMesh(mesh)` | Per placement |
| --- | ---: | ---: | ---: | ---: |
| **Field** | 22,321 | 2 KB | **0 KB** | **2 KB** |
| Stage01 | 116 | 2 KB | 0 KB | 2 KB |
| Stage02 | 60 | 2 KB | 0 KB | 2 KB |

Even on a 22k-triangle stage, placing a building is kilobytes.

> The four-argument `SwapNavMesh(mesh, query, pathfinder, funnel)` is the exception: it has you
> build those three objects, and each is sized from the triangle count — about **1.4 MB per
> placement** on Field. Use it only if you own the instances for some other reason.

### How many you add at once barely matters

Both rows below end at the same 11-building mesh. They differ only in how much of it the previous
rebake had already produced.

| Reaching an 11-building mesh | min |
| --- | ---: |
| From 10 buildings — one placement | 5.76 ms |
| From none — eleven at once | 6.02 ms |
| With no previous mesh to work from | 10.94 ms |

Placing eleven buildings costs about what placing one costs. Most of the work is proportional to the
**size of the map**, not to how much changed, and only the small area around each new building is
recomputed. Eleven small areas are still small.

So read the earlier table as *how many buildings are on the map*, not *how many arrived this time*.
Loading a saved base, or applying a whole queued build order at once, is no more expensive than a
single placement.

### Keep the building list in placement order

The single biggest thing under your control. The shortcut works by matching what did not change
between two rebakes, and that matching holds only while existing geometry keeps its identity —
which it does when the list grows at the end.

| Building list order | Rebakes that took the shortcut |
| --- | ---: |
| **Placement order** | **96%** |
| Sorted by position | 18% |

Both are equally deterministic, so the choice is free. Sorted by position, a building placed at a
low `x` lands in the middle of the list and everything after it shifts, so the rebake builds the
whole mesh instead. Give each building a placement sequence and sort by that.

**Check it in your own game** with `context.PatchOutcome`. Compare its `Incremental` count against
the `Fallback…` counters: if `FallbackVertexShift` climbs with every rebake, your list has lost
placement order.

One counter there is not about you. `FallbackDuplicateBoundaryEdge` should always be zero — a
non-zero value means a bug in the rebaker itself and is worth reporting.

### What each choice costs you

Four ways to call the rebaker, same asset, one building. The first three are what you get by
skipping something.

| Configuration | Time |
| --- | ---: |
| `Rebake(baseMesh, …)` — snapshot rebuilt every call | 49.8 ms |
| `Rebake(snapshot, …)` — snapshot cached | 9.4 ms |
| `Rebake(context, …)` — cached + pooled, full rebuild | 9.3 ms |
| `Rebake(context, …)` + `CommitSwap` — previous mesh reused | **4.6 ms** |

| Configuration | Allocation (0 buildings) |
| --- | ---: |
| `Rebake(snapshot, …)` | 7,766 KB |
| `Rebake(context, …)` — work buffers pooled | 3,110 KB |
| `Rebake(context, …)` + `CommitSwap` — output recycled too | **2 KB** |

Three independent mechanisms, and they compose: caching the snapshot is worth about 5× on time,
carrying the previous mesh across another 2×, and pooling plus output recycling is what turns
megabytes of allocation into kilobytes. **`CommitSwap` enables the last two** — skip it and you get
neither, which is why the quick start calls it.

### Smaller stages

Uncached `Rebake(baseMesh, …)`, so directly comparable to the 49.8 ms row above.

| Asset | Triangles | Rebake |
| --- | ---: | ---: |
| Stage01 | 116 | 0.14 ms |
| Stage02 | 60 | 0.07 ms |

A small stage rebakes inside a tick with room to spare. A Field-sized stage does not sit comfortably
in a 16.7 ms frame even at 4.6 ms, once the agent reseed is added — apply it at a tick boundary.

### One-off costs

| Operation | Cost | When |
| --- | ---: | --- |
| `CreateContext` on Field | 41 ms | Once per match, at load — **not on the tick thread** |
| First rebake in a cold process | 324 ms | Absorbed by `prewarm: true` (the default) |

That 324 ms is not real work — it is the .NET runtime compiling the code the first time it runs.
Prewarming does one throwaway rebake at load so the first real placement in a match pays the normal
cost instead. It does nothing under IL2CPP/AOT, where there is no such warm-up to pay.

### Shape cost at the agent layer

Carved buildings become ORCA obstacles, and a building contributes as many obstacle segments as it
has edges. 25 buildings, 64 agents, 200 ticks, seven interleaved box/hexagon repeats:

| Footprint | Obstacles | Tick (min) | Tick (median) | Own spread |
| --- | ---: | ---: | ---: | ---: |
| Box (4 edges) | 260 | 0.840 ms | 0.843 ms | 4.2% |
| Hexagon (6 edges) | 310 | 0.824 ms | 0.827 ms | 1.2% |
| | **+19.2%** | −2.0% | −1.9% | |

**19% more obstacles does not show up in the tick at all.** The difference is smaller than the
normal run-to-run variation, and the hexagon actually came out marginally faster every time. Agents
only consider obstacles near them, so the total count on the map does not translate into cost.

**Pick footprint shapes for how the game should feel, not for performance.**

### Memory

`FPNavMeshTriangle` is **88 bytes**. Three meshes are resident during a swap (base, installed,
retired), so a 22k-triangle stage holds roughly 5.9 MB of triangle data.

---

## 8. Limits

| Limit | Detail |
| --- | --- |
| **Single level** | The base mesh must have XZ-unique vertices. Stacked floors and bridges are rejected at `CreateContext`. |
| **Convex footprints** | Each footprint must be convex. An L-shape is expressible as touching convex pieces, but they are separate placements. |
| **No crossing constraints** | Two building edges may not cross transversally; overlap is rejected before it can happen. |
| **Shape size ceiling** | Around a kilometre of extent, the miter arithmetic runs out of Int64 and refuses by name. Real footprints are a few metres. |
| **Uniform area/cost only** | A base with non-uniform `areaMask`/`costMultiplier` loses per-triangle values on re-triangulation; the rebaked mesh keeps pipeline defaults and logs an error. |
| **Not per-tick** | A rebake is a discrete event. Do not call it every frame. |

---

## 9. API reference

### Entry points

| Member | Purpose |
| --- | --- |
| `FPNavMeshRebaker.CreateContext(baseMesh, logger, prewarm, shapeCatalog)` | Per-room context — the normal entry point |
| `FPNavMeshRebaker.CreateSnapshot(baseMesh, logger, prewarm, shapeCatalog)` | Per-stage snapshot, when sharing across rooms |
| `FPNavMeshRebaker.TryRebake(context, FPBuildingRect[], out mesh, out rejection, …)` | **The one to use in a game.** Rebake from axis-aligned rectangles; a refused placement is `false` + a reason |
| `FPNavMeshRebaker.TryRebakePlacements(context, FPBuildingPlacement[], out mesh, out rejection, …)` | Same, from catalog placements |
| `FPNavMeshRebaker.Rebake(context, FPBuildingRect[], logger, rules, buildingCount)` | The throwing form. `buildingCount` is how much of the array is live |
| `FPNavMeshRebaker.RebakePlacements(context, FPBuildingPlacement[], logger, rules, placementCount)` | The throwing form, from catalog placements |
| `FPNavMeshRebaker.TryValidateOne(context, ghost, out rejection, rules)` | **Placement preview.** Same verdict as a rebake, no carving, no building list ([4.6](#46-showing-the-player-before-they-click)) |
| `FPNavMeshRebaker.TryValidateOnePlacement(context, ghost, out rejection, rules)` | Same, from a catalog placement |
| `FPNavMeshRebaker.TryValidateOne(snapshot, existing, count, ghost, out rejection, scratch, rules)` | Preview without a context — you supply the accepted list |
| `FPNavMeshRebaker.TryValidateOnePlacement(snapshot, …)` | Same, from catalog placements |
| `FPBuildingPreviewScratch` | Working buffers for the snapshot-form preview calls — keep one per caller |
| `context.Snapshot` | The stage snapshot, for the snapshot-form calls |
| `FPNavMeshRebaker.ComputeFingerprint(mesh)` | Cross-peer check value |
| `context.CommitSwap(mesh)` | Marks the mesh live; retires the previous one |
| `context.ShapeExpansion` | The stage's expanded shape table |
| `context.PatchOutcome` | Running tally of how often this context patched instead of rebuilt |

**Use the context form.** `Rebake` / `RebakePlacements` also accept a snapshot or a bare
`FPNavMesh` instead of a context. Those exist for tests and one-off tooling and are much slower —
they skip the buffer reuse and, in the bare-mesh case, redo the whole setup on every call. The
`Try…` pair is offered on the context form only, for the same reason.

| Member | Purpose |
| --- | --- |
| `FPBuildingRejection` | Why the map refused — `BuildingsOverlap` · `TouchesWalkableBoundary` · `OutsideWalkableRegion` · `SwallowsBakedHole` · `EmptyWalkableRegion`. Values are wire-stable by convention: add at the end, never reorder |
| `FPBuildingRejectionInfo` | `Reason` plus `IndexA` / `IndexB` (which buildings) and `Site` (which swallowed hole) |

### Shapes

| Member | Purpose |
| --- | --- |
| `FPBuildingRect(minX, minZ, maxX, maxZ, y)` | Axis-aligned footprint, unexpanded |
| `FPBuildingPlacement(shapeId, orientation, centreX, centreZ, y)` | Catalog reference plus a centre |
| `FPBuildingPlacement(shapeId, centreX, centreZ, y)` | The same, for a shape that does not turn |
| `FPBuildingPlacementRules(allowBuildingTouch)` | Whether contact is allowed |
| `FPBuildingShapeCatalogBuilder` | Assembles a table from several sizes/types; each `Add…` returns a shape id |
| `builder.AddObb(hw, hd, directions)` | Appends an **OBB** that turns `directions` ways; `directions` divides a full circle and must be a multiple of 4 |
| `builder.AddHexagon(circumradius)` | Appends a **circle**; one orientation |
| `builder.Add(offX, offZ)` | Appends a custom convex shape; one orientation |
| `FPBuildingShapeCatalog(offX, offZ, entryStart, shapeFirstEntry)` | The table directly (CSR, integers from the centre) |
| `FPBuildingShapeCatalog.ObbOffsets(hw, hd, directions, …)` | Raw **OBB** offsets, if not using the builder |
| `FPBuildingShapeCatalog.HexagonOffsets(circumradius, …)` | Raw **circle** offsets, if not using the builder |
| `catalog.Hash` | Table identity, for the determinism envelope |
| `catalog.ShapeCount` / `catalog.DirectionCount(shape)` | How many shapes, and how many turns each has |
| `catalog.TryResolveEntry(shape, orientation)` | The pair as a table row, or `-1` if it names nothing |
| `catalog.TilesThePlane(shape, orientation)` | Can this shape be packed gap-free? |
| `expansion.TryTilingDelta(shape, orientation, edge, …)` | Centre offset to a flush neighbour |
| `expansion.TrySnapToLattice(shape, orientation, x, z, …)` | Nearest lattice point to a free-hand position |

### Grid

| Member | Purpose |
| --- | --- |
| `FPGeoPredicates.Quantize(v)` | Put a coordinate on the placement grid |
| `FPGeoPredicates.IsOnGrid(v)` | Is it already? |
| `FPGeoPredicates.SNAP_FRAC_BITS` | Grid resolution exponent (10 → 1024 snap units per metre) |
| `FPGeoPredicates.MAX_SNAPPED_COORD` | Coordinate bound (±46,340 m) |

### Agent side

| Member | Purpose |
| --- | --- |
| `FPNavAgentSystem.SwapNavMesh(mesh)` | Install a rebaked mesh, rebinding the held query/pathfinder/funnel |
| `FPNavAgentSystem.SwapNavMesh(mesh, query, pathfinder, funnel)` | Same, taking instances you built yourself |
| `FPNavAgentSystem.ReseedAgents(...)` | **Yours to call after either swap** — rebuilds every agent's triangle index and corridor |
| `FPNavAgentSystem.LoadNavMeshObstacles()` | Re-extract ORCA obstacles (called by the swap) |
| `FPNavAgentSystem.CurrentMesh` / `CurrentQuery` | The live instances — read these instead of caching |
| `INavFingerprintSource` | Implement to fold the nav fingerprint into full-state exchange |
