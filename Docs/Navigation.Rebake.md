# Runtime NavMesh Rebake

Place and remove buildings **during a match** and have the NavMesh change to match — deterministically, on every peer, with no authored variants.

Supported footprints: **AABB** (axis-aligned box), **OBB** (oriented box, quantized to `M`
directions) and **circle** (approximated by a hexagon, so it can still be packed).

> **Prerequisite**: [Navigation.md](Navigation.md) covers the static NavMesh runtime — query,
> pathfinding, agents, ORCA. This document is about changing that NavMesh mid-match.
>
> **Where to start**: [2. Quick start](#2-quick-start) is the whole wiring, and
> [6. Placing from a command stream](#6-placing-from-a-command-stream) is what a placement command
> has to do. Those two are enough to ship. [4. Concepts](#4-concepts) answers "what shape can I
> place, and why was this one refused". Sections 7 to 9 cover determinism, measured costs and
> limits. [11](#11-doing-it-by-hand) is an appendix for tooling and non-rollback games.
>
> **Units**: one world unit is **one metre**. Sizes on the shape API are in **snap units**, 1024 to
> a metre — so a snap unit is just under a millimetre.

---

## 1. What problem this solves

A baked NavMesh is fixed at build time. When a player puts a building down, agents have to stop
routing through it — and every peer has to agree exactly.

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

Two simpler-looking approaches do not work:

- **Marking the covered triangles unwalkable.** NavMesh triangles are large — one can span a whole
  corridor. Blocking the triangle a small building sits on closes a path that is still mostly clear.
- **Re-running Unity's or Godot's baker.** Those use floating-point maths, which can give slightly
  different answers on different machines. Two players would end up with different NavMeshes and the
  match would desync.

---

## 2. Quick start

**If placements come from a deterministic command stream — which in a rollback game they do — this is
the whole of it:**

```csharp
// 1) ONCE per match, at load. The context holds the stage's immutable snapshot plus this
//    room's work buffers. Not on the tick thread — see the performance table.
FPNavMeshRebakeContext rebake = FPNavMeshRebaker.CreateContext(baseNavMesh, logger);

// 2) Implement two small interfaces over YOUR components: where the placement table comes
//    from, and how a mesh gets in front of your agents. Roughly 60 lines, all of it about
//    your own types (see 6. Placing from a command stream).
var seam = new MyPlacementSeam(rebake);

// 3) Register the DRIVER. That is the whole wiring.
simulation.AddSystem(seam.Driver, SystemPhase.PreUpdate);   // ahead of whatever moves agents
```

The engine finds that driver at `Initialize` and takes over from there: it paces the sliced rebake
every frame, establishes the invariant at world init, and re-establishes it after a full state
applies. **Registering it is the opt-in** — a game that registers no driver pays nothing, and once
registered there is no way to opt out of the pacing.

On the command side, a validator answers "may this placement land?" against the set that will be
live when it does. That is the same derivation the driver installs from, so what you accept is what
gets baked:

```csharp
int count = validator.Survey(ref frame, atTick: frame.Tick + BuildDelayTicks);
if (count >= MyPolicyCap) { Reject("cap reached"); return; }
if (!validator.TryPreviewWith(rebake, candidate, out FPBuildingRejectionInfo why))
{
    Reject(why.Reason);        // every peer reaches the same verdict
    return;
}
frame.Add(entity, new MyBuilding { /* … */ Sequence = validator.NextSequence, … });
```

Nothing swaps here. The component *is* the outcome, and the driver installs the mesh on the tick
that component names — which is what lets a rollback across that tick reproduce the swap instead of
having to undo it. [6. Placing from a command stream](#6-placing-from-a-command-stream) has the full
story, including four traps worth knowing about.

---

## 3. How it works

You only ever supply two things: the stage's original NavMesh, and the current list of buildings.

```text
   base NavMesh  ─┐
                  ├──▸  rebake  ──▸  new NavMesh  ──▸  swap it in
   building list ─┘
```

Three things happen inside that affect what you pass:

- **Buildings are checked, never adjusted.** A placement that will not work is refused and the
  original mesh is left alone — see [4.5](#45-what-gets-rejected).
- **The agent radius is added for you.** You give the building's real size; the rebaker widens the
  carved hole so agents do not clip the wall. **Every check runs on that widened ring**, so the
  distances that matter — to a neighbour, to a wall — are a full agent radius larger per side than
  the size you authored. That is why a flush offset has to be asked for rather than derived
  ([4.3](#43-placing-shapes-flush)), and why a building that looks clear of the boundary can still be
  refused ([4.5](#45-what-gets-rejected)).
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

Quantize late and you end up with two slightly different positions: the one your game stored, and
the one the NavMesh was carved from. Nothing warns you. It shows up much later as buildings that no
longer line up flush, because your offsets came from the wrong one.

Two things that catch people out:

- **It is 1/1024 m, not 1 m.** Whole metres are on the grid, so rounding to them works — but then
  buildings can no longer sit flush against each other, because the gaps between them are almost
  never a whole number of metres.
- **Only binary fractions are on it.** `0.5`, `0.25`, `0.125` yes; `0.1`, `0.3`, `2.7` no.
- **Rounding to a coarse grid costs you placements.** Under `ClipOverlap`, a ghost that snaps to
  quarter or half metres gets refused on a lot of positions. Moving your grid two snaps off the
  round numbers fixes it — one snap makes it worse. [4.4](#44-building-wall-to-wall) says why.

> **Why 1/1024 rather than a millimetre?** The engine's numbers are binary, and 1/1024 is an exact
> binary fraction while 1/1000 is not. An exact one converts back and forth with no rounding, which
> is what keeps every machine in agreement. It is also slightly finer than a millimetre.

### 4.2 Supported footprint types

| Type | How you describe it | Can be packed flush? |
| --- | --- | --- |
| **AABB** | `FPBuildingRect` — no catalog needed | Yes |
| **OBB** | Catalog shape from `AddObb`, in one of `M` directions | Yes — within one orientation |
| **Circle** | Catalog shape from `AddHexagon` | Yes |

Packing works **within** an orientation: two boxes turned the same way meet exactly, two boxes
turned differently do not. "Circle" means a hexagon — the roundest footprint that can still be
packed ([4.3](#43-placing-shapes-flush) has the numbers).

**A rectangle needs no catalog.** Give the footprint in metres; the rebaker adds the agent radius
itself.

```csharp
new FPBuildingRect(minX, minZ, maxX, maxZ, y)
//                 └── one corner ──┘  └── the opposite one ──┘
```

The four coordinates pair by **corner**, not by axis. The other common convention is
`minX, maxX, minZ, maxZ`, and the same four numbers read that way give a different rectangle with
nothing to complain about — so it is worth a glance the first time.

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

The `new[]` is fine for a one-off. In a game you do not write this call at all — the driver bakes and
the validator previews, both from buffers they own. If you do call it yourself, reuse one buffer and
pass a live count: [Reuse the buffer](#reuse-the-buffer--do-not-build-an-array-per-placement).

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
grid stays on it however far the pattern is extended. For free-hand placement that still snaps to
the pattern, use `TrySnapToLattice` ([4.3](#43-placing-shapes-flush)).

#### Things to know before you ship a rotate button

- **`directions` is the whole circle, not a half.** `16` means 22.5° per step, not 11.25°.
- **A box looks the same after a half turn**, so orientation `k` and `k + M/2` are the same
  footprint. 16 directions give a player **8 distinguishable shapes**, each reachable two ways —
  fine for a rotate button, surprising if you were building a palette of 16 previews.
- **Multiple of 4 is required** so that four quarter-turns land back on the exact starting integers.
  A box that drifted a snap unit per turn would lose flush contact with its neighbours, and you
  would only see it as a placement rejection long after the turn that caused it.
- **The order of the `Add…` calls decides the ids, so it must never change by accident.** A stored
  placement refers to a shape by id. Reorder the calls and every stored placement now points at a
  different shape, with nothing to warn you. Write the table as plain straight-line code — never
  from a loop over a dictionary, a scanned folder, or a config file, because those can come back in
  a different order on another machine. (`catalog.Hash` is your safety net: see
  [7. Determinism](#7-determinism).)
- **Use `AddHexagon`; do not build a hexagon yourself.** A regular hexagon has no integer form, and
  the obvious route (compute the vertices, pass them through `Snap`) loses the ability to pack —
  `Snap` is floor, which is not symmetric about the centre. It still carves perfectly, so the loss
  only shows up the first time someone builds wall to wall.

Raw offsets are available as `ObbOffsets` / `HexagonOffsets` if you are assembling a table without
the builder ([10. API reference](#10-api-reference)).

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

Rounder shapes — an octagon, a 16-gon — cannot. They give you one flush neighbour and look fine, but
keep building outward in two directions and the copies start to overlap. Such a shape is still
perfectly placeable and carves correctly; it just is not something a player can pack wall to wall.

### 4.4 Building wall to wall

By default two buildings may not even graze each other. Wall-to-wall packing is a **game rule**, so
turn it on explicitly:

```csharp
var rules = new FPBuildingPlacementRules(allowBuildingTouch: true);
FPNavMeshRebaker.Rebake(rebake, buildings, logger, rules);
```

With the flag on, a shared edge, a shared corner and a collinear overlap are accepted and carved as
one merged obstacle.

Touching the **walkable boundary** is a second, independent policy:

```csharp
var rules = new FPBuildingPlacementRules(
    allowBuildingTouch: true, FPBoundaryPlacementPolicy.Touch);
```

There are three policies:

| Policy | What a footprint may do at the walkable boundary |
| --- | --- |
| `Reject` (default) | Nothing — any contact with the boundary is refused |
| `Touch` | Sit flush against a wall: shared edges, shared corners and T-contacts all carve |
| `ClipOverlap` | Hang past a wall at any angle; the part outside is clipped away and the rest is carved |

Under `Touch`, a chain of flush buildings can **seal a corridor completely**: the walkable region
splits in two and paths across it fail — the RTS "wall off the map" case. Flush contact with a baked
hole (a pillar) works the same way.

`ClipOverlap` adds the case `Touch` cannot serve: a wall with no lattice point to sit flush against,
so one placement can seal a corridor at an arbitrary angle. Neighbouring placements whose wall runs
overlap merge into one obstacle, and the strip carved between them hugs the wall within a few
millimetres, so the passage away from the wall stays open. Everything `Touch` accepts is accepted
bit-identically.

> **`ClipOverlap` exists to seal a corridor, not to fit a building against the art.** It answers one
> question — may the walkable region be closed here — and it answers it about the *navigation* mesh
> only. The overhanging part is removed from the walkable region; nothing is removed from your
> building's rendered mesh, and the rebaker never looks at it. So a footprint that hangs over the
> cliff edge or into the rock face will pass, and on screen the building will clip into the wall art.
>
> **That is yours to manage, and the placement UI is where it belongs.** Nothing in the rebaker can
> help: it knows the walkable boundary, which is where an agent may stand — not where the visual
> wall begins, how it is chamfered, or how far it overhangs. Give the ghost its own check against
> your art — a collider sweep against the wall/cliff meshes is the usual one — and require both that
> and the rebake verdict before the placement is offered. Two independent gates, two different
> questions.

`ClipOverlap` has four refusals of its own, all deterministic and all explainable to a player:
*clips to nothing* (entirely outside the region), *exact boundary contact while crossing* (move a
snap), *no candidate near the crossing* (the sliver is thinner than ~4 mm), and *clips into more than
one region*. Both previews answer under this policy — the single-ghost `TryValidateOne` and the
full-list `TryPreviewPlacements`.

**Do not round the ghost to the same grid your stage art uses.** A build UI that snaps to quarter or
half metres gets *exact boundary contact* refusals on a lot of positions — on a dense stage, about
one in five. Move your grid a couple of snaps off the round numbers and they nearly all go away.

```csharp
// A quarter-metre ghost grid, stepped two units off the round numbers.
const int  CellShift = 8;   // 2^8 grid units = 1/4 m — there are 1024 to a metre
const long Bias      = 2;

static FP64 SnapGhost(FP64 cursor)
{
    long cell = (FPGeoPredicates.Snap(cursor) - Bias) >> CellShift;   // a shift is floor
    return FPGeoPredicates.Unsnap((cell << CellShift) + Bias);
}
```

**Two units rather than one.** One unit still parks the footprint as close to the wall as the grid
allows, so it trades the exact contacts for the thinnest slivers the clip can be handed — on a dense
stage that costs a couple of percent of positions back. Two behaves the same as three, four or
eight, so there is nothing to tune here.

Neither refusal is something `ClipOverlap` took away: `Touch` and the default refuse those same
placements too. They are just the ones clipping could not rescue.

> **Why the round grid is the bad one.** The expanded footprint is an axis-aligned rectangle, so each
> of its sides is a line of constant x or constant z. A wall vertex sits exactly on a side as soon as
> it shares *one* coordinate with it — and on real stages roughly half the vertices have their x or
> their z on a quarter-metre boundary, even though hardly any sit on one in both. Round the ghost to
> quarter metres and your footprint's sides line up with all of them at once.

Things that stay rejected whatever the policies say:

- **Overlapping interiors**, including one building nested inside another — nesting would turn the
  inner shape back into walkable and hand you a building with a courtyard in it.
- **Transversally crossing the boundary.** Flush is allowed under `Touch`; poking through is not.
- **A boundary chord running corner-to-corner of the footprint** (for a rect: a diagonal wall
  through both corners). The placement probe lands exactly on the ring edge, where inside/outside is
  undefined. Move the building a snap.

One thing `Touch` does **not** give you: a sub-snap gap is still a corridor. A building placed one
snap short of the wall leaves a 1/1024 m walkable strip and agents route through it — the engine
cannot tell a 1 mm gap from a doorway, so closing near-misses is a game rule (snap placements to a
lattice, or place flush).

> Whichever way you set the flag, it must be the same on every peer **and in every replay** — a
> build that flips it diverges from recordings made before. Source it from inside the determinism
> envelope, not from a local config file.

### 4.5 What gets rejected

**The rebake is the validation**, and every peer reaches the same verdict on the same input. There is
no second set of rules anywhere — but you can ask the question early, for a placement ghost. See
[4.6 Showing the player before they click](#46-showing-the-player-before-they-click).

**A refused placement comes back as a value.** It is what a player causes by pointing somewhere they
cannot build, so it is a return value, not an exception:

```csharp
if (!FPNavMeshRebaker.TryRebakePlacements(
        rebake, placements, out FPNavMesh mesh, out FPBuildingRejectionInfo rejection,
        logger, rules, placementCount: count))
{
    ShowToPlayer(rejection.Reason);   // branch on the value, do not match the message
    return;
}
```

**Every geometric refusal below is judged on the *expanded* footprint, not the one you authored.**
The rebaker adds the bake agent radius (plus two snap units of padding to absorb the vertex snap), so
the ring the checks see is **a full agent radius larger on every side** — at radius 0.5 that is 0.5 m
per side, and the padding is a rounding term at 2/1024 m. A building drawn half a metre clear of a
wall is therefore *touching* it as far as the boundary tests are concerned, and one drawn half a metre
from its neighbour already overlaps. That is the same arithmetic as the flush spacing in
[4.3](#43-placing-shapes-flush) seen from the other side: you cannot read either distance off the size
you authored. `FPBuildingShapeExpansion.ExpandedX/ExpandedZ` is where the realised extent lives, and a
placement ghost that wants to warn before the click should measure with it.

| `rejection.Reason` | Meaning | Indices |
| --- | --- | --- |
| `BuildingsOverlap` | Two buildings are too close once the agent radius is added. This also covers buildings that merely touch, when your rules forbid touching — the check cannot tell the two cases apart, so tell the player "too close" rather than "overlapping". | `IndexA`, `IndexB` |
| `TouchesWalkableBoundary` | The **expanded** footprint reaches the edge of the walkable region. Under `Touch` only a transversal crossing reports this — flush contact carves. Under `ClipOverlap` a carve is clipped instead, but a **retained** footprint is refused here ([4.7](#47-retaining-a-footprint-instead-of-carving-it)). | `IndexA` |
| `OutsideWalkableRegion` | The footprint is not on the mesh at all. Under `ClipOverlap` it means **clips to nothing** — the footprint never reaches the walkable region. | `IndexA` |
| `SwallowsBakedHole` | It swallowed a pillar or other baked hole, which would flip that hole back to walkable. | `IndexA`, and `Site` — *which* hole, which a building index alone cannot say |
| `EmptyWalkableRegion` | Nothing walkable was left. No placement can cause this under the default policy; under `Touch`/`ClipOverlap` a footprint flush on every side can cover the whole region. | — |
| `ProbeOnBoundaryRing` | A boundary chord runs corner-to-corner of the footprint, so the placement probe sits exactly on a ring edge. Only reachable under `Touch`/`ClipOverlap`; tell the player to nudge the building. | `IndexA` |
| `ExactBoundaryContact` | `ClipOverlap`: the footprint touches the boundary exactly at a lattice point while also crossing it. Nudge a snap. Common if your ghost snaps to a coarse grid — see [4.4](#44-building-wall-to-wall). | `IndexA` |
| `ClipCandidateMissing` | `ClipOverlap`: the placement barely grazes the walkable region (sliver under ~4 mm). Move it. | `IndexA` |
| `ClipSplitsWalkableRegion` | `ClipOverlap`: the clipped shape would split into several regions (a spur through the footprint, or a chain enclosing a whole boundary ring). | `IndexA` |
| `ClipRunsInterleave` | `ClipOverlap`: degenerate interleaved wall contact the clip cannot represent. | `IndexA` |
| `BaseMeshUnsupported` | Not about this placement at all: the **stage** cannot be rebaked (see [The stage itself can be refused](#the-stage-itself-can-be-refused)). Returned by `FPNavMeshPlacementProbe` so an unsupported base is a value like every other refusal; ask `TryPrepare` at load and you never see it. | `IndexA` is `-1` — no building is at fault |

`Rebake` / `RebakePlacements` do exactly the same thing but throw an `InvalidOperationException`
instead of returning `false`. Use those for one-off work such as tests or tooling, and the `Try…`
pair anywhere a player is placing buildings.

**A malformed request still throws, and that is the point.** These are `ArgumentException`, and they
mean the call should never have been built:

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
`ArgumentException` is a bug in your code. Handle both in the same branch and your bug reaches the
player as "you cannot build there" — on every machine, identically, with nothing left to notice it
by.

Two rows above deserve a second look: **the degenerate rectangle and the off-map one are your bugs,
yet an ordinary player gesture produces them** — a click with no drag, or a drag past the edge of the
map. Filter those in your placement UI rather than catching the exception.

If you do catch, catch `ArgumentException` specifically. Do **not** catch `Exception`: a null context
or a corrupt catalog throws something else entirely, and a blanket catch hides it.

A rejection is a normal outcome, not an error: reject the command, raise a UI event, change nothing.

#### The stage itself can be refused

Two more, and these are not placement rejections. They come from `CreateContext` / `CreateSnapshot`
and mean the asset cannot be rebaked at all:

| Refusal | Type | Meaning |
| --- | --- | --- |
| `base mesh is not predicate-grid snapped` | `NotSupportedException` | The asset's vertices are not on the placement grid — re-export it. |
| `base mesh has XZ-duplicate vertices (multi-level)` | `NotSupportedException` | Stacked floors that share a vertex — see [Limits](#9-limits). |
| `base mesh boundary rings cross in XZ (stacked floors?)` | `NotSupportedException` | Stacked levels whose boundaries cross when projected. The message names the two segments in snapped grid coordinates, and the triangulator's own refusal rides along as the inner `FPConstraintCrossingException` (which derives from `InvalidOperationException`, so existing `catch` blocks are unaffected) with the crossing coordinates as fields. |
| `zero-building rebake did not reproduce the base walkable region` | `NotSupportedException` | Stacked levels that overlap without crossing — a platform nested inside a floor. Caught by rebaking nothing and comparing the walkable area exactly; the message names the area lost. |

Handle these at load time, where you can still fall back to running that stage without runtime
building at all. If one shows up in a command handler instead, it means you built the context lazily
on the first placement — build it at load.

The area comparison in the last row is one number — total walkable area — so it catches a level that
went missing but not damage that happens to preserve area (a hole that moved, two equal regions
swapped). It is a guard against the silent case, not a proof of correctness.

### 4.6 Showing the player before they click

A placement UI needs an answer as the cursor moves, and the rebake cannot give it: it costs
milliseconds and must run from the command stream, not from input. So ask the same question without
the carving:

```csharp
// Each frame the ghost moves. Nothing to set up and nothing to keep —
// `rebake` is the context you already hold, and it knows what it carved.
bool canBuild = FPNavMeshRebaker.TryValidateOne(
    rebake, ghost, out FPBuildingRejectionInfo why);

ShowGhost(canBuild ? Colour.Green : Colour.Red);
if (!canBuild) ShowHint(why.Reason);
```

**It is the same verdict, not a guess.** Almost every reason a player can be refused is decided
before any triangle is cut, so this runs the rebake's own validation and stops there (the one
exception is at the end of this section). It checks the ghost against whatever the last successful
rebake through that context carved — you hand it no building list, so there is no list to get out of
step. `TryValidateOnePlacement` takes a catalog placement instead; the ghost's shape need not match
what is already on the map.

The `rules` argument is optional and defaults to **the ones that rebake ran under**, which is what
keeps the two answers together. Pass a value only when you mean something different — "what would
this look like if contact were allowed", say.

> **The one answer a preview cannot give.** *Nothing walkable was left* is only discovered while the
> ground is being cut, so a preview never sees it. Under the default policy that costs you nothing —
> a building always sat strictly inside the region, so a gap always survived. Under `Touch` and
> `ClipOverlap` it is reachable: a footprint flush on every side can cover the whole walkable area,
> and the rebake refuses it while the preview said green. If your game can build shapes that large,
> handle a refusal arriving after a green ghost.

**What it costs.** About **0.085 ms** on a 22k-triangle stage, and flat — it walks the base mesh's
edges once, so the buildings already down do not enter it. `ClipOverlap` is the exception: answering
means building the clip rings, which costs **0.231 ms with 4 buildings down and 0.622 ms with 32**.
That is still a per-frame budget (3.7% of a 60 Hz frame at 32 buildings), but unlike the other
policies the time grows with the building count. **Allocation does not** — no policy's preview
allocates anything in steady state, so previewing every frame costs the collector nothing.
[§8](#the-per-frame-preview) has the full table.

Two things to get right:

- **Preview from the same thread that rebakes, one previewer per context.** The ghost is written into
  storage the context owns, and so are the working buffers the clip stage reuses — which is what
  makes the call allocation-free. A second previewer, or a rebake running alongside, corrupts both.
  In a client that is usually true already. The snapshot-taking overloads are safe on any thread
  because each caller brings its own `FPBuildingPreviewScratch`; one scratch per caller is the same
  rule.
- **Green is optimistic, not final.** Another player may build there between the preview and your
  command reaching the tick. Keep handling the refusal — in a multiplayer build that is a normal
  outcome, not an edge case.

**Do not skip the rebake because the preview passed.** The preview runs locally, from input, on
whatever the local peer currently believes. The rebake runs from the command stream, which is the
only place every peer sees the same list — that is what makes the verdict identical everywhere.

There is a second form for callers that hold no context, and three rules come with it:
[If you rebake without a context](#if-you-rebake-without-a-context).


---

### 4.7 Retaining a footprint instead of carving it

`FPBuildingPlacement.Retain` keeps the footprint as **triangulated ground**. The ring still goes in
as exact constraint edges — no triangle straddles the boundary, the corners are still mesh vertices —
but the interior is not erased.

```csharp
// Terrain and authored obstacles keep carving; only this one retains.
var placements = new[]
{
    new FPBuildingPlacement(rockShape, cx, cz, y),                 // carves, as always
    new FPBuildingPlacement(barracksShape, bx, bz, y, retain: true),
};
```

**What the rebaker claims about a retained placement: one thing — it is a building.** It makes the
triangles exist, marks them, and leaves the decision to the caller's area mask. Concretely, and all
three matter:

- **The footprint is stamped `FPNavMeshAreas.BUILDING_MASK`, exclusively.** `FindTriangle` still
  reports it (it is ground, not `isBlocked`), but `FindPath` and `MoveAlongSurface` see it through
  the mask the caller passes. `FPNavAgentSystem.DEFAULT_AREA_MASK` is
  `FPNavMeshAreas.DEFAULT_AGENT_MASK` — every area except the building's — so an agent neither
  plans through a retained footprint nor walks into it: a destination inside is refused (and counted
  in `DebugAreaMaskRejectedCount`), and the walk slides along its edge as along a wall. A caller
  that passes `FPNavMeshAreas.ALL_AREAS` plans straight through it. Which mask you pass is the gate;
  the engine's own agents default to the wall side. The stamp is exclusive because masks
  *intersect*: `base | BUILDING` would still share the base bit with every default query and block
  nothing — so the base area of a retained triangle is gone from the mesh, by design. Area index 1
  is reserved for this (Unity's Not Walkable never reaches the triangulation, Godot writes 0) and
  `FPNavMeshBuildPipeline.Build` refuses a bake that carries it.
- **No ORCA obstacle is emitted for it.** The obstacle extractor seeds rings from edges with no
  walkable neighbour, and a retained footprint has walkable triangles on both sides of every edge,
  so local avoidance does not know the building is there.
- **Changing your own belief invalidates nothing on its own — but there is now a call for it.** A
  placement changes the mesh, so the swap and `ReseedAgents` drop stale corridors for you. Deciding
  later that a retained building should block someone changes no geometry, so no swap happens.
  `NavAgentComponent.SetAreaMask(ref nav, planMask, walkMask)` is the call: it assigns the masks and
  drops the corridor the old ones planned, **including the repath cooldown**, so the agent replans on
  the next tick. Reaching for `SetDestination` instead leaves `LastRepathTick` alone and the agent
  can sit for up to `PathRepathCooldown` ticks first.

  **A building dropped on top of a unit is the other direction, and it no longer traps it.** A unit
  standing inside a retained footprint can route and walk out under its own mask: the start triangle
  is exempt and the escape rule lets both the path and the walk cross forbidden ground *outward*.
  Entry from ordinary ground is unchanged. `FPNavMeshPathfinder.DebugMaskedStartCount` is the only
  trace that it happened, so read that if the game wants to react (a rescue, a refund, a warning).

Why it exists: a game that models knowledge — fog of war, ghost or memory mechanics, AI planning on
stale information — needs a route to be *computable* through a building the planner is not supposed
to know about. A hole cannot be planned through at all; retained ground can, and the layer above
decides who believes what.

**The agent layer expresses that split directly.** An agent carries two masks — `PlanAreaMaskOverride`
for what it may route through, `WalkAreaMaskOverride` for what it may enter — so *plan permissively,
walk restrictively* is one call rather than a game-side scheme. The unit then walks into the building
it was not told about and stops there, reporting `FPNavAgentStatus.Blocked`; widening its mask later
releases it. Zero on either field means "no override" and resolves to `DEFAULT_AREA_MASK`, so this
changed nothing for an agent that names no mask. The four pairs and what each produces are tabulated
in [Navigation.md § The area filter](Navigation.md#the-area-filter).

**Seeing it without writing a game.** Both editors' NavMesh visualizers can do the whole loop: place
a retained building, spawn an agent, hand it `plan = all areas` + `walk = agent default`, and set its
destination **inside the footprint**. The destination has to be inside — with both endpoints outside,
A\* routes around a small footprint whichever mask it was given (the corridor cost is a portal-midpoint
polyline), so the agent arrives normally and the run proves nothing. Applying a wider walk mask to the
stopped agent is the other half: it replans and walks in.

**Two limits.**

- **Under `FPBoundaryPlacementPolicy.ClipOverlap`, the building's clip transitions decide.** A
  transition-free footprint is admitted and retained exactly as under `Touch` (the identity path
  emits it verbatim). A footprint that crosses the walkable boundary is refused with
  `FPBuildingRejection.TouchesWalkableBoundary` — a rejection value, because the player caused it
  and is meant to see it — where a carve of the same footprint would have been clipped: the clip
  stage rewrites a crossing footprint into lattice-conforming rings, and a retained ring has no
  defined way to close against them, so retain has no clip to fall back on. Both ghost-only preview
  forms (`TryValidateOnePlacement`) report the same verdict and reason as the real placement.

  **Where this surprises people: the transitions are counted on the *expanded* ring** (see
  [4.5](#45-what-gets-rejected)). A retained building drawn comfortably clear of a wall — by less
  than the agent radius — already crosses, so it is refused while a carve dropped on the very same
  spot succeeds by being clipped. If you want to know how far in "in" is, place the same footprint as
  a carve and look at the hole: cut off at the wall means there were transitions. Building against a
  wall and keeping its ground is not available in v1; carve the ones that touch and retain the
  interior ones — the flag is per placement precisely so a game can mix them.
- **Acceptance is otherwise unchanged.** Retain does not widen what the rebaker accepts: under
  `Reject` and `Touch` a footprint that crosses the walkable boundary is still refused exactly as a
  carve is. That is not conservatism — a retained ring only survives because the placement rules
  keep the footprint inside the walkable region, and a crossing constraint is a triangulation error
  rather than a policy question.

> Retain is a **determinism input**, like the placement centre. It changes the geometry, so it must
> be a pure function of frame state and agree on every peer. A mode read from local config or a UI
> toggle diverges the navmesh while the state hash still matches.

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

`FPNavAgentInstaller` packages that pair — `Swap` and `Reseed`, as two calls rather than one, because
the second writes hashed state and the first does not. It also re-collects the agent set on both,
which matters: a cached set can be last tick's, and the reseed-only path has no swap ahead of it to
have refreshed anything. [6. Placing from a command stream](#6-placing-from-a-command-stream) is
about when to call them at all.

**`CommitSwap` is not optional.** It tells the context which mesh is live, and two things hang off
that: the *previous* mesh's arrays can be recycled, and the next rebake can reuse most of it instead
of rebuilding. Skip it and you pay full price for both — see
[8. Performance](#what-each-choice-costs-you).

> Do not cache an `FPNavMesh` across a swap — read the current one from `FPNavAgentSystem.CurrentMesh`
> (or your provider) each time. A retired mesh's storage becomes another mesh's; DEBUG builds detect
> a read of one.

---

## 6. Placing from a command stream

Everything above describes one rebake. This section is about the harder question a rollback game
runs into: **when** to install the result.

### Do not swap when the command arrives

A placement command is handled on a tick, and in a rollback netcode that tick may only have been
**predicted**. When the rollback comes it rewinds the frame — and nothing rewinds the NavMesh with
it. The mesh is now ahead of the state that describes it, and every re-executed tick runs against a
mesh its own frame does not agree with. Both peers hold identical components, and the state hash
covers components, so it diverges quietly.

The fix is not to undo the swap. It is to stop treating the install as an event:

> **The installed mesh is derived state. Re-derive it from the frame every tick.**
>
> `installed == Bake(snapshot, { b : b.EffectiveTick <= tick < b.RemovalEffectiveTick })`

Stated as an invariant, four situations collapse into one case — moving forward, re-executing after a
rollback, waking up on a full state, and spectating or replaying a match. A swap performed as an
event only ever handles the first.

This is why the building's **tick window has to be frame state** (see
[7. Determinism](#7-determinism)): the invariant is only checkable if "what is in the mesh at tick T"
is answerable from state that rolls back.

### Install and reseed are two separate calls

Installing the mesh and reseeding the agents look like one operation. They are not:

| | Nature | May it be skipped? |
| --- | --- | --- |
| Install the mesh | Derived state | **Yes** — when the right mesh is already in place |
| Reseed the agents | Writes hashed frame state | **No** — every execution of the tick must do it |

Fusing them puts the reseed behind a peer-local comparison that does not roll back. A boundary tick
re-executed after its mesh was already installed then skips the reseed entirely: the authority
reseeded once and kept the value, while the client's last pass left the agent with the triangle
index it had from *before* the swap.

The rule to carry away: **derived state may be skipped when it is already right; frame state may
not** — because "already right" is a question about this peer's history, and the frame does not know
it.

### `FPNavMeshRebakeDriver` does all of this

Rather than reimplement it, hand the engine a placement table and let it own the invariant:

```csharp
// Your game supplies two things.
sealed class MyPlacements : IFPNavMeshPlacementSource
{
    public int Capacity => MyStorageBound;      // STORAGE bound, tombstones included

    public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
    {
        // Every placement the frame holds — no tick filtering. The driver needs the windows.
        // `eligible` is how many were there, so a buffer that is too small gets reported.
    }

    public void DestroyDue(ref Frame frame, int tick)
    {
        // Your components, your write. Use <= tick, not == : a tombstone that slipped past one
        // tick must still go.
    }
}

sealed class MyInstaller : IFPNavMeshInstaller
{
    public void Install(ref Frame frame, FPNavMesh mesh)   // swap only, never reseed
        => FPNavAgentInstaller.Swap(ref frame, _agents, mesh, ref _agentBuffer);

    public void Reseed(ref Frame frame)                    // re-collects, always
        => FPNavAgentInstaller.Reseed(ref frame, _agents, ref _agentBuffer);
}

// Register it ahead of whatever drives your agents, so they see the corrected mesh.
var driver = new FPNavMeshRebakeDriver(new MyPlacements(), new MyInstaller(), rules);
driver.SetSnapshot(context.Snapshot);
simulation.AddSystem(driver, SystemPhase.PreUpdate);

// That is the whole wiring. The engine finds the driver at Initialize and paces it.
```

**The engine owns the two moments the per-tick invariant misses.** The invariant covers every tick
that *executes*; two moments are outside it:

- At world init the initial full state's nav fingerprint is sampled before tick 0 runs, so a peer
  that has installed nothing would be accused of a mismatch by a peer that has.
- A receiving peer's fingerprint is compared **synchronously** right after a full-state apply, so
  waiting for the next tick is too late.

`KlothoEngine` calls `CorrectNow` at both, and advances slices every frame, so registering the driver
is all a game writes.

**Why the seam is a table and not a set of predicates.** The driver derives everything else from it
— whether this tick is a boundary, which tick the next one is, the active set, a digest that detects
change. Getting the boundary predicate wrong is a desync, and a game that implements two predicates
has two chances to get it wrong.

### Spreading the cost over frames

A rebake on a large stage does not fit in one tick ([8. Performance](#8-performance)). The driver
pays for it across the frames between a placement and its effective tick, and the engine drives that
for you — there is nothing to switch on, and nothing to switch off:

- **Frames, not ticks.** A client catching up runs many ticks in one frame; a tick-paced budget would
  spend eleven frames' work in the single frame this exists to protect.
- **Optional, and safe to forget.** If the slices did not get there in time, the boundary finishes
  the remainder synchronously.
- **The output is identical either way**, so the number of slices is peer-local and harmless. That is
  what lets the budget be a guess rather than a negotiated constant.

Underneath, `FPNavMeshRebaker.TryBeginRebake` hands back a resumable `FPNavMeshRebakeTask`; the
driver owns it. Drive one yourself only if you are not using the driver:

```csharp
if (!FPNavMeshRebaker.TryBeginRebake(context, buildings, out var task, out var rejection))
    return;                              // refused at Begin, before any slice
while (!task.Step(budgetUnits)) { }       // or once per frame
FPNavMesh mesh = task.Install();          // announces to the context; then swap and CommitSwap
```

A task holds its context's pool for as long as it lives, and the pool refuses overlapping use — so a
synchronous rebake while one is in flight needs a **second context**. Abandon a task with
`task.Discard()`.

### Crossing a boundary more than once

A predicting client does not cross a placement's effective tick once. It crosses, rolls back, and
crosses again — and the mesh it wants each time is one of **two**.

`slotCount` (default 2) is how many meshes the driver keeps live for that. One context can only hold
one installed mesh — its next commit recycles the other — so the second slot exists to hold the
partner:

| Slots | Rebakes for 147 installs |
| ---: | ---: |
| 1 | 128 |
| **2** | **8** |
| 3+ | 8 |

Eight is the number of distinct sets, i.e. the theoretical minimum. More than two buys nothing. The
cost is a second work-buffer pool, which scales with the stage — about **+26.6 MB** on a
205k-triangle stage and nothing worth measuring on a small one. Pass `slotCount: 1` to disable it.

### Validating a command without installing anything

Two different questions, two different calls:

| You are asking | Use | Cost |
| --- | --- | --- |
| "Can the player put a building *here*?" — hover preview, one ghost | `TryValidateOne` ([4.6](#46-showing-the-player-before-they-click)) | No carving at all |
| "Does this whole set bake?" — accepting a command | `FPNavMeshPlacementValidator` ([10. API reference](#validating-a-command)) | A full trial rebake, discarded |

**If you registered a driver, use the validator** — it derives the set, orders it and audits it with
the driver's own code, so what you accept is what the driver will bake. The raw call below is what it
uses underneath, and what a host without a placement table calls directly.

```csharp
if (!FPNavMeshRebaker.TryPreviewPlacements(
        context, placements, out FPBuildingRejectionInfo rejection, logger, rules, count))
{
    RejectCommand(Describe(rejection));
    return;
}
// Accepted. Record the component with its tick window; the driver installs on that tick.
```

`TryPreviewPlacements` **hands back no mesh, deliberately**. A trial result must not be installed —
the tick that accepted the command may be a prediction a rollback discards. It also discards the
trial from the context for you: left in place, the next real rebake would diff against a mesh nobody
installed and quietly stop patching for the rest of the match.

---

## 7. Determinism

The geometry is exact integer arithmetic, so the mesh is bit-identical across runtimes by
construction. Three things are yours to keep aligned:

1. **Call it from the command stream only** — same tick, same order, on every peer. Sort the
   building list by a stable key too; **placement order** is the one to pick. Any deterministic key
   is equally correct, but this one decides how often the rebaker can take its shortcut — 96% of
   rebakes against 18% — see
   [8. Performance](#keep-the-building-list-in-placement-order).
2. **The placement rules value.** See [Building wall to wall](#44-building-wall-to-wall).
3. **The shape catalog.** A placement is a *reference* into the table, so two builds that disagree
   about the table carve different meshes from identical commands. Carry `catalog.Hash` in your
   match config so a mismatch fails at load instead of surfacing later.

**Cross-checking.** The NavMesh is not part of the state hash — it is large, and rebuilding it is
deterministic anyway. Instead, `FPNavMeshRebaker.ComputeFingerprint(mesh)` gives you a small value
peers can compare. Implement `INavFingerprintSource` and the engine compares it for you.

**Late join and rollback.** Store the building set as frame state (ECS components), not in a
system-local list — a joining peer restores the components and rebakes once from them. A
system-local list cannot be reproduced without replaying the whole command history. Carry the tick
window (`EffectiveTick` / `RemovalEffectiveTick`) on the same components: that is what makes the
installed mesh derivable rather than something you have to sequence by hand
([6. Placing from a command stream](#6-placing-from-a-command-stream)).

---

## 8. Performance

Measured on an Apple Silicon laptop, .NET 8, Release, on 2026-08-21. **Field** is a real shipped
asset: 22,321 triangles, 17,142 vertices. Every figure comes from one fixture, so you can reproduce
them:

```
dotnet test -c Release --filter FullyQualifiedName~FPNavMeshRebakerPerfTests
```

### The cost of placing a building

The timed event is the rebake that **adds** the Nth building. Re-running an unchanged set is much
cheaper — nothing to carve differently.

| Placing building # | min | allocation |
| ---: | ---: | ---: |
| 1 | 2.76 ms | 10.4 KB |
| 2 | 2.82 ms | 9.9 KB |
| 4 | 2.96 ms | 11.6 KB |
| 8 | 3.21 ms | 22.4 KB |
| 16 | 3.75 ms | 32.3 KB |
| 32 | 4.97 ms | 48.1 KB |

The base mesh dominates; each building already on the map adds roughly **0.07 ms**.

**Retaining a footprint costs the same on a large stage as on a small one.** The stamp that marks a
retained footprint `BUILDING_MASK` (§4.7) visits only the broadphase grid cells the footprint's
bounding box touches, so its cost tracks footprint area rather than mesh size: 32 retained buildings
cost **0.13 ms** on a 51k-triangle mesh and the same 0.13 ms on a 205k-triangle one. That matters
because the stamp runs inside a single sliced step, which no work-unit budget can split.

> **`ClipOverlap` costs the same as the other policies until a footprint actually overhangs.** Four
> interior footprints on a synthetic grid, same placements, policy the only variable:
>
> | Triangles | `Touch` | `ClipOverlap` | `ClipOverlap`, one footprint overhanging |
> | ---: | ---: | ---: | ---: |
> | 12,800 | 1.52 ms · patched 13 | 1.52 ms · patched 13 | **7.04 ms · patched 0** |
> | 51,200 | 4.07 ms · patched 13 | 3.96 ms · patched 13 | **11.92 ms · patched 0** |
>
> The interior columns match, because that set carves exactly what `Touch` carves. An overhanging
> footprint takes the full-rebuild path instead of the incremental patch, which is the whole of the
> difference. If your stage seals corridors every few seconds, budget for the full path on those
> placements.
>
> Allocation for the clip stage is about **3.7 KB per placement** above what the default policy
> allocates, whatever the boundary looks like. Under `Reject` and `Touch` the stage never runs.
> A rebake allocates those buffers per call by design: the per-frame preview brings its own and
> reaches zero ([§4.6](#46-showing-the-player-before-they-click)), and the two must not share a pool.

**Installing the result is free**, so the numbers above are the whole cost of a placement. Swapping
the mesh in allocates nothing: the query, pathfinder and funnel are rebound rather than rebuilt, and
the ORCA obstacle re-extraction reuses its buffers across swaps.

| Asset | Triangles | Rebake | `SwapNavMesh(mesh)` | Per placement |
| --- | ---: | ---: | ---: | ---: |
| **Field** | 22,321 | 3 KB | **0 KB** | **3 KB** |
| Stage01 | 116 | 3 KB | 0 KB | 3 KB |
| Stage02 | 60 | 3 KB | 0 KB | 3 KB |

Even on a 22k-triangle stage, placing a building is kilobytes.

> The four-argument `SwapNavMesh(mesh, query, pathfinder, funnel)` is the exception: it has you build
> those three objects, and each is sized from the triangle count — about **1.4 MB per placement** on
> Field. Use it only if you own the instances for some other reason.

### The per-frame preview

`TryValidateOne` answers without carving anything, so it is the call a placement UI makes as the
cursor moves. Same asset, one ghost, against N buildings already down.

| Buildings already down | `Reject` / `Touch` | `ClipOverlap` |
| ---: | --- | --- |
| 1 | 0.087 ms · **0 B** | 0.214 ms · **0 B** |
| 4 | 0.084 ms · **0 B** | 0.231 ms · **0 B** |
| 16 | 0.084 ms · **0 B** | 0.362 ms · **0 B** |
| 32 | 0.086 ms · **0 B** | **0.622 ms** · **0 B** |

**Nothing here allocates, under any policy** — zero in steady state, and a test asserts exactly that
for every configuration above. A preview is a per-frame call, so there is no amount of garbage per
frame that is fine.

Under the strict policies the **time** is flat too: the work is one walk of the base mesh's edges,
and what is already built does not enter it.

Under `ClipOverlap` answering means actually building the clip rings, so it starts about 2.5× higher
*and grows*: each existing building's contribution is cached on the accepted set and the ring checks
are narrowed to the ghost's own group, so what is left is the pair layer between that group and
everything else. At 32 buildings that is 3.7% of a 60 Hz frame — budget for it if your stage ends a
match with hundreds of buildings.

Previewing only when the ghost's snapped cell changes is still free performance and worth taking, but
it is no longer necessary: a cursor that previews every frame allocates nothing.

> **Why two fixtures.** A footprint that clips a wall cannot be placed under `Reject` at all, and one
> `Reject` accepts is by definition not touching the boundary — so under `ClipOverlap` it carves its
> plain footprint and never enters the clip stage. No single geometry measures both paths. The strict
> columns use footprints strictly inside the region; the `ClipOverlap` column uses footprints that
> clip.

### How many you add at once barely matters

Both rows below end at the same 11-building mesh. They differ only in how much of it the previous
rebake had already produced.

| Reaching an 11-building mesh | min |
| --- | ---: |
| From 10 buildings — one placement | 3.45 ms |
| From none — eleven at once | 3.62 ms |
| With no previous mesh to work from | 8.17 ms |

Placing eleven buildings costs about what placing one costs. Most of the work is proportional to the
**size of the map**, not to how much changed, and only the small area around each new building is
recomputed. Eleven small areas are still small.

So read the earlier table as *how many buildings are on the map*, not *how many arrived this time*.
Loading a saved base, or applying a whole queued build order at once, is no more expensive than a
single placement.

### Keep the building list in placement order

The single biggest thing under your control. The shortcut works by matching what did not change
between two rebakes, and that matching holds only while existing geometry keeps its identity — which
it does when the list grows at the end.

| Building list order | Rebakes that took the shortcut |
| --- | ---: |
| **Placement order** | **96%** (27 of 28) |
| Sorted by position | 18% (5 of 28) |

Both are equally deterministic, so the choice is free. Sorted by position, a building placed at a low
`x` lands in the middle of the list and everything after it shifts, so the rebake builds the whole
mesh instead. Give each building a placement sequence and sort by that.

**Check it in your own game** with `context.PatchOutcome`. Compare its `Incremental` count against
the `Fallback…` counters: if `FallbackVertexShift` climbs with every rebake, your list has lost
placement order.

One counter there is not about you. `FallbackDuplicateBoundaryEdge` should always be zero — a
non-zero value means a bug in the rebaker itself and is worth reporting.

### What each choice costs you

Four ways to call the rebaker, same asset, one building. The first three are what you get by skipping
something.

| Configuration | Time |
| --- | ---: |
| `Rebake(baseMesh, …)` — snapshot rebuilt every call | 45.9 ms |
| `Rebake(snapshot, …)` — snapshot cached | 7.29 ms |
| `Rebake(context, …)` — cached + pooled, full rebuild | 7.28 ms |
| `Rebake(context, …)` + `CommitSwap` — previous mesh reused | **2.50 ms** |

| Configuration | Allocation (0 buildings) |
| --- | ---: |
| `Rebake(snapshot, …)` | 8,229 KB |
| `Rebake(context, …)` — work buffers pooled | 3,110 KB |
| `Rebake(context, …)` + `CommitSwap` — output recycled too | **3 KB** |

Three independent mechanisms, and they compose — but they do not all pay in time. Caching the
snapshot is worth about **6×** on time and carrying the previous mesh another **2.9×**; **pooling the
work buffers buys no time at all** (7.29 vs 7.28 ms is noise) and shows up entirely in the allocation
table, where it is 8,229 KB → 3,110 KB. Output recycling takes the last 3,110 KB down to 3.
**`CommitSwap` enables the last two** — skip it and you get neither, which is why the quick start
calls it.

### Smaller stages

Uncached `Rebake(baseMesh, …)`, so directly comparable to Field's 45.9 ms above.

| Asset | Triangles | Rebake |
| --- | ---: | ---: |
| Stage01 | 116 | 0.14 ms |
| Stage02 | 60 | 0.07 ms |

A small stage rebakes inside a tick with room to spare. A Field-sized stage does not sit comfortably
in a 16.7 ms frame even at 4.6 ms, once the agent reseed is added.

### Slicing it across frames

A rebake can be advanced a budget at a time instead of run whole
([6. Placing from a command stream](#6-placing-from-a-command-stream)). What that buys is not a lower
total — it is a lower **worst single step**, which is what a frame actually feels:

| Triangles | Whole | Sliced (worst step) |
| ---: | ---: | ---: |
| 12,800 | 0.73 ms | 0.31 ms |
| 51,200 | 2.56 ms | 0.67 ms |
| 115,200 | 5.70 ms | 1.37 ms |
| 204,800 | 10.09 ms | **2.41 ms** |

**The worst step is set by the largest unit that cannot be cut, not by the budget you pass.**
Everything proportional to the triangle count is cut mid-pass and resumed on the next frame — the
incremental patch included, which is the bulk of a rebake and is stepped through its own phases
rather than run as one call.

What stays whole are the patch's two tail steps — **Patch/Grid** (the spatial-grid rebuild) at
2.29 ms and **Patch/Assemble** at 1.95 ms, at 205k triangles; together 65% of the sliced total. That
is the floor; no budget goes under it.

**Release does not scan this path for degenerate triangles**, so the fixture measures that scan at
**0.00 ms** — the triangulation the patch starts from is non-degenerate by construction. DEBUG keeps
the scan as an assertion (worth about 2.5 ms per rebake at that size), which makes a DEBUG step
noticeably slower than the shipped one.

**Budget.** 20000 work units per frame is at or within noise of the best at every size above and does
the fewest steps to get there; 100000 starts chaining phases back together, and 1000 spends steps for
nothing (at 51k: 0.67 ms worst at 20000, 1.37 at 100000, 0.55 at 1000 for 472 steps instead of 24). A
game on a different stage size should measure its own — `FPNavMeshRebakerPerfTests.P0F_SliceBudgetCalibration`
is the fixture.

The budget is a **constructor argument and peer-local**, so a PC build and a phone may pass different
values; the mesh is byte-identical either way. Set it per platform rather than per engine —
[SimulationConfigGuide §4.4](SimulationConfigGuide.md#44-per-device-knobs-that-are-not-in-simulationconfig)
has the per-device guidance, the lower bound that a too-small budget violates, and the counter that
tells you when you have crossed it.

### One-off costs

| Operation | Cost | When |
| --- | ---: | --- |
| `CreateContext` on Field, `prewarm: false` | 38.6 ms | Once per match, at load — **not on the tick thread** |
| First rebake in a cold process | 67.4 ms | What `prewarm` exists to absorb |
| `CreateContext` on Field, default (`prewarm: true`) | 77.1 ms | The two above, folded into load |

That 67 ms is not real work — it is the .NET runtime compiling the code the first time it runs.
Prewarming does one throwaway rebake at load, which is why the default costs about the sum of the
other two rows: it moves the JIT cost off the first real placement. It does nothing under IL2CPP/AOT,
where there is no such warm-up to pay.

### Shape cost at the agent layer

Carved buildings become ORCA obstacles, and a building contributes as many obstacle segments as it
has edges. 25 buildings, 64 agents, 200 ticks, seven interleaved box/hexagon repeats:

| Footprint | Obstacles | Tick (min) | Tick (median) | Own spread |
| --- | ---: | ---: | ---: | ---: |
| Box (4 edges) | 260 | 0.858 ms | 0.889 ms | 11.7% |
| Hexagon (6 edges) | 310 | 0.845 ms | 0.867 ms | 3.8% |
| | **+19.2%** | −1.6% | −2.5% | |

**19% more obstacles does not show up in the tick at all.** The difference is well inside the box
row's own 11.7% spread, in either direction. Agents only consider obstacles near them, so the total
count on the map does not translate into cost.

**Pick footprint shapes for how the game should feel, not for performance.**

### Memory

`FPNavMeshTriangle` is **88 bytes**. Three meshes are resident during a swap (base, installed,
retired), so a 22k-triangle stage holds roughly 5.9 MB of triangle data.

---

## 9. Limits

| Limit | Detail |
| --- | --- |
| **Single level** | The base mesh must not overlap itself in XZ — one sheet, seen from above. Stacked floors and bridges are rejected at `CreateContext`, by three checks: XZ-duplicate vertices, boundary rings that cross in projection, and a zero-building rebake that fails to reproduce the base walkable area exactly (the case where nothing crosses because a platform nests wholly inside a floor). The third one runs whether or not you ask for the prewarm — a stacked base that is *accepted* produces a mesh that is wrong identically on every peer, which no fingerprint comparison can report. |
| **Convex footprints** | Each footprint must be convex. An L-shape is expressible as touching convex pieces, but they are separate placements. |
| **No crossing constraints** | Two building edges may not cross transversally; overlap is rejected before it can happen. |
| **Shape size ceiling** | Around a kilometre of extent, the miter arithmetic runs out of Int64 and refuses by name. Real footprints are a few metres. |
| **Uniform area/cost only** | A base with non-uniform `areaMask`/`costMultiplier` loses per-triangle values on re-triangulation; the rebaked mesh keeps pipeline defaults and logs an error. The one exception is the rebaker's own stamp: retained footprints get `FPNavMeshAreas.BUILDING_MASK` after the inherit, on the full and the patch path alike (§4.7). |
| **A rebake is not per-tick** | It is a discrete event; do not call it every frame. The driver's own per-tick work is separate and cheap — one pass over your placement table plus an integer compare, and zero allocation. |
| **A preview cannot report `EmptyWalkableRegion`** | *Nothing walkable was left* is only discovered while the ground is being cut. Unreachable under the default policy; under `Touch`/`ClipOverlap` a footprint that covers the whole region shows green and is then refused ([4.6](#46-showing-the-player-before-they-click)). |
| **`ClipOverlap`: the preview's time grows with the building count** | Flat under the other policies. Under `ClipOverlap` answering means building the clip rings, so it goes 0.231 ms → 0.622 ms between 4 and 32 buildings ([8](#the-per-frame-preview)). Allocation does not grow — no preview allocates in steady state. |
| **`ClipOverlap`: no incremental patch on a clip ring** | A placement that actually overhangs a wall takes the full-rebuild path; one that does not is unaffected — it carves what `Touch` carves and patches the same way (see [8. Performance](#8-performance)). |

---

## 10. API reference

### Entry points

| Member | Purpose |
| --- | --- |
| `FPNavMeshRebaker.CreateContext(baseMesh, logger, prewarm, shapeCatalog)` | Per-room context — the normal entry point |
| `FPNavMeshRebaker.CreateSnapshot(baseMesh, logger, prewarm, shapeCatalog)` | Per-stage snapshot, when sharing across rooms |
| `FPNavMeshRebaker.TryRebake(context, FPBuildingRect[], out mesh, out rejection, …)` | **The one to use in a game.** Rebake from axis-aligned rectangles; a refused placement is `false` + a reason |
| `FPNavMeshRebaker.TryRebakePlacements(context, FPBuildingPlacement[], out mesh, out rejection, …)` | Same, from catalog placements |
| `FPNavMeshRebaker.Rebake(context, FPBuildingRect[], logger, rules, buildingCount)` | The throwing form. `buildingCount` is how much of the array is live |
| `FPNavMeshRebaker.RebakePlacements(context, FPBuildingPlacement[], logger, rules, placementCount)` | The throwing form, from catalog placements |
| `FPNavMeshRebaker.TryPreviewPlacements(context, placements, out rejection, …)` | **Command validation.** Asks whether the whole set bakes and throws the answer away — no mesh comes back, and the trial is discarded from the context for you |
| `FPNavMeshRebaker.TryBeginRebake(context, FPBuildingRect[], out task, out rejection, …)` | **Sliced rebake.** Refuses at Begin, then returns a resumable task |
| `FPNavMeshRebaker.TryBeginRebakePlacements(context, FPBuildingPlacement[], out task, out rejection, …)` | Same, from catalog placements |
| `FPNavMeshRebaker.TryValidateOne(context, ghost, out rejection, rules)` | **Placement preview.** Same verdict as a rebake, no carving, no building list ([4.6](#46-showing-the-player-before-they-click)) |
| `FPNavMeshRebaker.TryValidateOnePlacement(context, ghost, out rejection, rules)` | Same, from a catalog placement |
| `FPNavMeshRebaker.TryValidateOne(snapshot, existing, count, ghost, out rejection, scratch, rules)` | Preview without a context — you supply the accepted list |
| `FPNavMeshRebaker.TryValidateOnePlacement(snapshot, …)` | Same, from catalog placements |
| `FPBuildingPreviewScratch` | Working buffers for the snapshot-form preview calls — **keep one per caller**, and do not share it between two previewers. It holds the clip stage's reused buffers too, which is why a preview allocates nothing |
| `context.Snapshot` | The stage snapshot, for the snapshot-form calls |
| `FPNavMeshRebaker.ComputeFingerprint(mesh)` | Cross-peer check value |
| `context.CommitSwap(mesh)` | Marks the mesh live; retires the previous one |
| `context.DiscardProduced()` | Forget the last output instead of installing it. `TryPreviewPlacements` does this for you |
| `context.ShapeExpansion` | The stage's expanded shape table |
| `context.PatchOutcome` | Running tally of how often this context patched instead of rebuilt |
| `FPNavMeshPlacementProbe` | **Tooling.** A base mesh plus an editable, ordered placement list, rebaked from the base on demand — see [11](#an-editable-placement-list-for-tooling) |

**Use the context form.** `Rebake` / `RebakePlacements` also accept a snapshot or a bare `FPNavMesh`
instead of a context. Those exist for tests and one-off tooling and are much slower — they skip the
buffer reuse and, in the bare-mesh case, redo the whole setup on every call. The `Try…` pair is
offered on the context form only, for the same reason.

| Member | Purpose |
| --- | --- |
| `FPBuildingRejection` | Why the map refused — `BuildingsOverlap` · `TouchesWalkableBoundary` · `OutsideWalkableRegion` · `SwallowsBakedHole` · `EmptyWalkableRegion` · `ProbeOnBoundaryRing` · `ExactBoundaryContact` · `ClipCandidateMissing` · `ClipSplitsWalkableRegion` · `ClipRunsInterleave`. Values are wire-stable by convention: add at the end, never reorder |
| `FPBuildingRejectionInfo` | `Reason` plus `IndexA` / `IndexB` (which buildings) and `Site` (which swallowed hole) |

### Delayed install

The layer that owns *when* a rebake is installed ([6. Placing from a command stream](#6-placing-from-a-command-stream)).

| Member | Purpose |
| --- | --- |
| `FPNavMeshRebakeDriver(source, installer, rules, sliceBudgetUnits, slotCount)` | Register as an `ISystem` ahead of whatever drives your agents. **Registering it is the whole wiring** — see below |
| `driver.SetSnapshot(snapshot)` | The stage it bakes against. Builds `slotCount` contexts of its own; `null` makes `Update` a no-op |
| `driver.Update(ref frame)` | Re-derives the installed mesh, reseeds on a boundary, destroys due entries, keeps a task in flight |
| `driver.SliceFaults` | Slices that ended in an exception. **Must be 0.** Non-zero does not break consistency — the boundary rebuilds synchronously and installs the same mesh — but it means a core defect, and the driver logs one error on the next tick |

**The engine wires itself to a registered driver.** It resolves one at `Initialize` and from then on
owns three things:

| What the engine does | When |
| --- | --- |
| `AdvanceSlice` on every frame | `Update`, ahead of every per-mode early return — so it reaches a server-driven client too, which never runs world init |
| `CorrectNow` to establish the invariant | right after `OnInitializeWorld`, before the static fingerprint is sampled and before the initial full state goes out |
| `CorrectNow` to re-establish it | on every full-state apply, before your `OnFullStateApplied` hook, sharing that hook's `DerivativeRebuildFailed` outcome |

**It also tells you what you have not folded.** A rebaking game changes its navmesh from a table no
state hash covers, so two hooks decide whether a disagreement about it can ever be reported —
`INavFingerprintSource` (the installed mesh) and `IGameFingerprintSource` (the shape catalog, and the
delay that picks a placement's tick). Both are optional, and a missing one folds 0, which is
indistinguishable from a game that has nothing to fold. So when a driver is registered and either
hook is absent, the engine says so once, as a warning:

```text
[KlothoEngine] a nav rebake driver is registered but no IGameFingerprintSource is — …
```

`FPNavAgentSystem` implements the nav one, so registering your agent system usually covers it. The
game one is yours to write. If you check those inputs at load through the match config instead, the
line is a known false positive — nothing has diverged either way; what the warning reports is the
absence of the net that would tell you if it did.

**Opt-in is the registration itself, and there is no opt-out.** A game that registers no driver pays
a null check at each of those points; a game that registers one cannot pace slices itself.

| Member | Purpose |
| --- | --- |
| `driver.CorrectNow(ref frame)` | Re-derive right now. Still public: a game with its own reason to correct may call it |
| `driver.AdvanceSlice(deltaTime)` | Advance an in-flight rebake by one frame's budget. The engine calls this; a host without the engine can call it directly |
| `driver.TryClaimSliceHeartbeat()` | **You do not need this.** The engine claims it at `Initialize`, so a later caller is told "someone else is pacing" — which is how hand-written wiring steps aside on its own. Pacing happens either way |
| `IFPNavMeshPlacementSource` | **Yours.** `Capacity` · `Collect(ref frame, buffer, out eligible)` · `DestroyDue(ref frame, tick)` |
| `IFPNavMeshInstaller` | **Yours.** `Install(ref frame, mesh)` and `Reseed(ref frame)` — two calls, never one |
| `FPNavMeshTimedPlacement` | `Sequence` · `Placement` · `EffectiveTick` · `RemovalEffectiveTick` |
| `driver.CacheHits` / `CacheMisses` / `SlicedFrames` / `BoundaryFinishes` / `TaskInstalls` / `RebuildInstalls` / `Corrections` / `Reseeds` / `TaskId` | Peer-local counters. Slicing and caching are invisible from the state, so these are the only way to tell a working driver from one that never runs |

`Sequence` must be unique among the entries of one collect, and `Capacity` must be the **storage**
bound rather than the number of buildings you let stand — a demolished one keeps its slot until its
removal tick. The driver reports a violation of either loudly; both are otherwise silent desyncs.

### Validating a command

The command half of the same table. Use this instead of deriving the active set yourself — the
derivation, the canonical order and both audits above are shared with the driver, so what you accept
is by construction what the driver will bake.

| Member | Purpose |
| --- | --- |
| `FPNavMeshPlacementValidator(source, rules)` | One per command system, over the SAME `IFPNavMeshPlacementSource` the driver reads |
| `validator.Survey(ref frame, atTick, skipSequence)` | Derives the set live at `atTick` and returns its size. `atTick` is when the command TAKES EFFECT, not now — validating against the present set lets two placements queued inside one delay window pass without seeing each other. **Returns -1** when a requested `skipSequence` is ambiguous (see below) |
| `validator.TryPreviewWith(context, candidate, out rejection, logger)` | Does the surveyed set PLUS the candidate bake? For a placement |
| `validator.TryPreview(context, out rejection, logger)` | Does the surveyed set bake? For a removal, where the set only shrinks |
| `validator.NextSequence` | One past the highest `Sequence` in the WHOLE table — the number to give a new entry |

**Two calls, not one**, because your policy cap belongs between them: refusing "you already have 32"
must not pay for a trial rebake first.

**The context is yours, never the driver's.** The driver holds contexts whose buffer pools an
in-flight sliced rebake occupies across frames, and the pool refuses overlapping use — which is why
the driver does not expose them.

**A removal excludes its target by `Sequence`**, because at the moment the command is handled the
target is still inside its own tick window and nothing else drops it. If two live entries share that
number, `Survey` returns **-1** and you must refuse the command: with a duplicate, "the one to
exclude" is not a well-defined entry, and removing whichever the comparison reached first deletes a
building that is still standing while its hole stays carved in the mesh.

**Not reentrant.** One command is one `Survey` followed by its preview; a nested `Survey` replaces the
first, and previewing without one throws.

### Serving several rooms from one stage

A host running many simulations off one stage builds the snapshot **once** and gives each room its own
context — the snapshot is immutable and safe to share, the context is not (it owns work buffers).

```csharp
// once per stage, at boot
FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, logger, prewarm: true);

// once per room, when it is created
var rebake = new FPNavMeshRebakeContext(snapshot);
```

Where this runs matters. A snapshot costs a full base insertion, and the first rebake in a process
additionally pays the JIT (`prewarm: true` absorbs it at boot). A dedicated server creates rooms on
its main loop, so doing it per room puts that cost between the network poll and the room dispatch —
every OTHER room's tick budget shrinks by exactly that much.

There is no helper for the pattern: the stage keying, the missing-stage fallback and how many hosting
modes you have are yours, and a helper shaped around one server's answers would be wrong for the
next one.

### Sliced rebake

| Member | Purpose |
| --- | --- |
| `FPNavMeshRebakeTask.Step(units)` | Advance; `true` when finished |
| `task.Result` | The mesh, or `null` if it was refused mid-flight |
| `task.Install()` | Announce it to the context — only then may the next rebake patch from it |
| `task.Discard()` | Abandon it and release the pool |
| `FPNavAgentInstaller.Swap(ref frame, navSystem, mesh, ref agents)` | Install and re-collect. Returns the agent count; does **not** reseed |
| `FPNavAgentInstaller.Reseed(ref frame, navSystem, ref agents)` | Re-collect and reseed |
| `FPNavAgentInstaller.Collect(ref frame, ref agents)` | Fill the agent buffer, growing it and never truncating |

### Shapes

| Member | Purpose |
| --- | --- |
| `FPBuildingRect(minX, minZ, maxX, maxZ, y)` | Axis-aligned footprint, unexpanded |
| `FPBuildingPlacement(shapeId, orientation, centreX, centreZ, y)` | Catalog reference plus a centre |
| `FPBuildingPlacement(shapeId, centreX, centreZ, y)` | The same, for a shape that does not turn |
| `FPBuildingPlacementRules(allowBuildingTouch)` | Whether building-pair contact is allowed |
| `FPBuildingPlacementRules(allowBuildingTouch, boundaryPolicy)` | Plus the walkable-boundary policy |
| `FPBoundaryPlacementPolicy` | `Reject` (default) / `Touch` (flush placement, corridor sealing) / `ClipOverlap` (overhang clipping — arbitrary-angle sealing) |
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

---

## 11. Doing it by hand

Everything in this appendix is for **tooling, editors and games without rollback**. In a rollback
game the driver ([2. Quick start](#2-quick-start)) does all of it, and doing it by hand reintroduces
the failure it exists to remove — a swap performed where the command was handled survives a rewind.

Nothing here is required reading to ship a rebaking game.

### Rebaking by hand

Three steps: build a context once at load, rebake when the building set changes, swap at a tick
boundary.

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

That is also why the result never depends on how you got there: a carved mesh is never carved again,
so small errors cannot pile up over a long match.

Internally the rebaker reuses what the previous rebake already worked out, which makes it much
faster. The mesh you get is identical either way, so this is not something you manage —
[8. Performance](#8-performance) covers the one thing you can do to help it.

Step 3 is the one this API cannot make safe on its own: it installs the mesh where the command was
handled. [6. Placing from a command stream](#6-placing-from-a-command-stream) is why that is a desync
in a rollback game.

#### Reuse the buffer — do not build an array per placement

The `new[]` in step 2 is fine for a one-off. In a handler that runs per placement it is not: it puts a
fresh array on the heap every time, directly in front of a rebaker built to allocate nothing
([8. Performance](#what-each-choice-costs-you)). Keep one buffer for the life of the room, sized to
your building cap, and tell the rebaker how much of it is live:

```csharp
readonly FPBuildingPlacement[] _scratch = new FPBuildingPlacement[MaxBuildings + 1];

// Your own: writes the buildings currently in frame state into the buffer, in
// placement order, and returns how many it wrote. (A rollback game does not write
// this — FPNavMeshPlacementValidator derives it, and the driver bakes from the same
// derivation. See "Validating a command" in the API reference.)
int count = CollectLivePlacements(ref frame, _scratch);
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

### An editable placement list for tooling

A tool wants something the rebaker deliberately does not have: a list you can add to, remove from and
throw away, with a base to return to. `FPNavMeshPlacementProbe` is that bookkeeping — a base mesh, an
ordered list of `FPBuildingPlacement`, and a rebake from the base on demand. It is what both NavMesh
visualizers drive when you place buildings in the editor.

```csharp
var probe = new FPNavMeshPlacementProbe(baseMesh, shapeCatalog, logger)
{
    Rules = new FPBuildingPlacementRules(allowBuildingTouch: true),
    SnapToTilingLattice = true,   // centres land where footprints pack flush
};

// Ask ONCE, right after loading: a base the rebaker cannot take at all (see 4.5) is a load-time
// refusal, and this is what keeps it from arriving as an exception inside the first click. Gate
// the placement UI on it and show the reason; the rest of the tool still works on that mesh.
if (!probe.TryPrepare(out string why))
    DisablePlacementAndSay(why);

// A refusal is a value, not an exception: pointing at ground you cannot build on is normal use,
// and a refused placement is not added to the list.
if (!probe.TryPlace(shapeId, orientation, cx, cz, y, retain: true,
                    out FPNavMesh mesh, out FPBuildingRejectionInfo rejection))
    ShowThePlayer(rejection.Reason);

probe.TryRemoveAt(probe.Count - 1, out mesh, out rejection);   // undo the last one
FPNavMesh baseAgain = probe.Revert();                          // back to the base mesh instance
```

Every call that changes the list hands back the mesh it now bakes to, so the caller installs one
mesh per edit and never has to ask what the current one is. `Revert` returns the **same** base
instance it was constructed with, not a zero-placement rebake.

**A refusal never changes the list**, in either direction. `TryPlace` undoes its append, and
`TryRemoveAt` puts the building back — which matters more than it sounds, because a removal *can* be
refused: `Rules` is a public setter, so tightening the boundary policy and then removing one of two
buildings has the survivor judged under the new rules. Without the undo the list would stop holding a
building the scene still shows, and the next successful rebake would ship a mesh missing it. An
out-of-range index is a caller bug and throws `ArgumentOutOfRangeException` rather than being read
off the end.

**An unsupported stage is a value here too.** `TryPrepare` remembers its verdict, so a base the
rebaker cannot take is decided once instead of rebuilding the triangulation on every click, and
`TryPlace` / `TryRemoveAt` / `TryRebake` return false with `FPBuildingRejection.BaseMeshUnsupported`
instead of throwing out of an API whose contract is to report a refusal as a value.

It lives in the **runtime**, not in editor code, for two reasons that are worth repeating if you are
tempted to copy it into a tool: an editor assembly cannot be reached by the test suites, and the
Godot adapters compile as source into the consuming project, where nothing in this package can reach
an `internal` type. A game does not need it — it builds placements from frame state and drives
`FPNavMeshRebakeDriver`.

### Running the triangulation yourself

`FPConstrainedDelaunay.BuildSnapshot` and `TriangulateFromSnapshot` are public, with `CdtSnapshot` as
an opaque handle: build a base snapshot from your own vertices and constraint edges **once**, then
resume it per set of holes. The expensive half is the build, and it is shared across resumes. The
triangle indices go into `FPNavMeshBuildPipeline.BuildFromConformingTriangulation`, which takes a
`previous` mesh — so a caller-authored pipeline keeps the incremental patch path. What it does not
get is **slicing**: this entry point runs to completion.

Three rules come with it.

- **A resume does not weld duplicate coordinates**, the way `Triangulate` does. Your constraint
  indices are positional, so welding would renumber your pairs. A hole that duplicates a base vertex
  or an earlier hole throws `InvalidOperationException` naming the coordinate — in release builds
  too, not only under `DEBUG`.
- **The same constraint segment inserted twice stays geometrically present but parity-neutral.**
  Marking a constraint ORs the wall bit and *toggles* the parity bit, so an even count keeps the wall
  and contributes nothing to the erase pass. That is how a seam blocks movement without carving the
  region beside it — and it is exactly how retain mode works ([4.7](#47-retaining-a-footprint-instead-of-carving-it)):
  the footprint ring is emitted twice.
- **An odd-multiplicity OPEN chain is undefined**, and a constraint pair whose two endpoints weld to
  the same vertex is dropped entirely rather than becoming a parity-neutral wall. Welding happens
  before constraint insertion, so coincident input coordinates reach this.

### If you rebake without a context

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
