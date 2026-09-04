using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;

namespace Brawler
{
    /// <summary>
    /// Brawler's half of the delayed-rebake pipeline: where the placement table comes from, who
    /// destroys the due tombstones, and who installs a mesh. Everything else —
    /// the invariant, the two-slot cache, the slicing, the ordering, the audits — is
    /// <see cref="FPNavMeshRebakeDriver"/>.
    ///
    /// <para><b>Not an <c>ISystem</c>.</b> The driver is registered directly (see
    /// <see cref="Driver"/>); this type is the seam it calls back into. It used to wrap the driver
    /// and forward fifteen members so callers could reach it through a game type — the wrapper is
    /// gone, so the forwarders are too. Anything that needs the driver asks the simulation for it
    /// by its own type, which is what lets the ENGINE find it as well.</para>
    ///
    /// <para><b>What used to be here.</b> This file was the pump itself, 806 lines of it, and all but
    /// about sixty of them never mentioned <c>BuildingComponent</c>. The mechanism moved to the core
    /// engine; what stayed is what is genuinely this game's: the component that carries the tick
    /// window, the destroy that writes the frame, and the swap that goes through the bot system's
    /// agent buffer.</para>
    ///
    /// <para><b>Why the component's tick window is frame state.</b> That is the precondition for the
    /// whole invariant. The driver derives the installed mesh from the frame on every tick rather
    /// than swapping on an event, which is what makes a rollback across a boundary reproduce the swap
    /// instead of needing it undone — and it can only do that if "what is in the mesh at tick T" is
    /// answerable from state that rolls back. <c>EffectiveTick</c> and <c>RemovalEffectiveTick</c>
    /// live on <c>BuildingComponent</c> for that reason, not for convenience.</para>
    /// </summary>
    public class NavMeshEffectiveTickSystem
        : IFPNavMeshPlacementSource, IFPNavMeshInstaller
    {
        private BotFSMSystem _botFSM;
        private readonly EntityRef[] _due;

        /// <summary>
        /// Work units per frame. Measured by
        /// <c>FPNavMeshRebakerPerfTests.P0F_SliceBudgetCalibration</c>.
        ///
        /// <para>Worst single step, min of 5, after the incremental patch became sliceable:</para>
        /// <code>
        ///   tris    1000    5000   20000  100000   whole
        ///  12800    0.51    0.33    0.30    0.65    0.65   ms
        ///  51200    0.54    0.58    0.61    1.27    2.27
        /// 115200    1.21    1.18    1.17    1.40    5.11
        /// 204800    2.12    2.08    2.13    2.14    9.33
        /// </code>
        /// <para>20000 because it is at or within noise of the best at every size and does the
        /// fewest steps to get there; 100000 starts chaining phases back together (1.27 vs 0.61 at
        /// 51k) and 1000 spends steps for nothing.</para>
        ///
        /// <para>An earlier revision said the budget barely mattered, and it was right AT THE TIME:
        /// 86% of the rebake was one indivisible unit, so every setting landed within noise of
        /// 11.4 ms. Dividing that unit is what made the budget mean something — at 205k the worst
        /// frame went 14.0 → 9.3 (unsliced, after the degeneracy scan moved to DEBUG) → 2.1 ms
        /// sliced.</para>
        ///
        /// <para>The floor now is <c>PatchSpatialGrid</c>, still whole at about 1.9 ms. Anyone who
        /// needs lower starts there, not here. And a game on a different stage size should measure
        /// its own rather than inherit this one.</para>
        /// </summary>
        private const int SliceBudgetUnits = 20000;

        /// <summary>
        /// The engine-level pump, registered directly into <c>PreUpdate</c> ahead of whatever drives
        /// the agents. Exposed rather than wrapped: the ENGINE discovers it with
        /// <c>GetSystem&lt;FPNavMeshRebakeDriver&gt;()</c>, and a wrapper of a game type would hide
        /// it from that lookup.
        /// </summary>
        public FPNavMeshRebakeDriver Driver { get; }

        /// <summary>
        /// The command path's validator, over the SAME table the driver reads. Sharing the table is
        /// the point: "what will be live when this placement lands" and "what is live now" are one
        /// derivation, so the command path cannot accept a set the driver will not bake.
        ///
        /// <para>Its context is supplied per call and must be the room's, never the driver's — a
        /// trial rebake through a context whose pool an in-flight slice is holding would be refused
        /// by the pool.</para>
        /// </summary>
        public FPNavMeshPlacementValidator Validator { get; }

        public NavMeshEffectiveTickSystem()
        {
            _due = new EntityRef[PlatformerCommandSystem.BuildingSlotCapacity];
            Driver = new FPNavMeshRebakeDriver(
                this, this, PlatformerCommandSystem.PlacementRules, SliceBudgetUnits);
            Validator = new FPNavMeshPlacementValidator(
                this, PlatformerCommandSystem.PlacementRules);
        }

        /// <summary>
        /// Hands the driver this stage's snapshot, and the bot system whose agent buffer the install
        /// goes through.
        ///
        /// <para>The driver builds contexts of its own over that snapshot — not the room's. A task
        /// holds its context's pool across frames and the pool refuses overlapping use, and the
        /// command path validates placements synchronously through the room context, so sharing one
        /// would put a trial rebake straight into a live slice.</para>
        /// </summary>
        public void SetContext(FPNavMeshRebakeContext context, BotFSMSystem botFSM)
        {
            _botFSM = botFSM;
            Driver.SetSnapshot(context?.Snapshot);
        }

        // ── IFPNavMeshPlacementSource ───────────────────────────────────────────

        /// <summary>
        /// The STORAGE bound, and that distinction is the whole point of the property.
        ///
        /// <para>A building being demolished keeps its component until its removal tick, so the
        /// number a frame can hold is larger than <c>MaxBuildings</c> — the number the game lets
        /// stand. Sizing from the policy bound would make the collect truncate, and a truncated
        /// rebake input is the quietest failure in this pipeline: the dropped buildings are missing
        /// from the mesh while the state hash still matches on every peer.</para>
        /// </summary>
        public int Capacity => PlatformerCommandSystem.BuildingSlotCapacity;

        /// <summary>
        /// Every building the frame holds, tick windows included and no tick filtering — the driver
        /// needs the windows to derive the boundaries.
        ///
        /// <para><c>eligible</c> is counted separately from what is written, so a buffer that is too
        /// small is reported rather than silently obeyed.</para>
        /// </summary>
        public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
        {
            int count = 0;
            eligible = 0;
            var filter = frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                eligible++;
                if (count >= buffer.Length)
                    continue;

                ref readonly var b = ref frame.GetReadOnly<BuildingComponent>(entity);
                buffer[count++] = new FPNavMeshTimedPlacement
                {
                    Sequence = b.Sequence,
                    Placement = new FPBuildingPlacement(
                        b.ShapeId, b.Orientation, b.Centre.x, b.Centre.z, b.Centre.y, b.Retain),
                    EffectiveTick = b.EffectiveTick,
                    RemovalEffectiveTick = b.RemovalEffectiveTick,
                };
            }
            return count;
        }

        /// <summary>
        /// Destroys the components whose removal has come due. Deferred to here rather than done in
        /// the command: the mesh keeps the hole until this tick, and a joiner arriving in between
        /// could not reproduce that hole from a component that no longer exists.
        ///
        /// <para>Collected first, destroyed after. Destroying inside the filter loop is what makes
        /// the storage move entries under the iteration; collecting is also what lets ALL of the due
        /// ones go in one pass, and more than one comes due together as soon as two players demolish
        /// in the same tick.</para>
        ///
        /// <para><c>&lt;=</c>, not <c>==</c>. Equality holds only on the exact tick, so a tombstone
        /// that slipped past — a tick where the destroy could not run, a state restored from further
        /// ahead — would sit in the array forever, invisible because the active-set filter already
        /// excludes it and permanent because nothing else looks at it. The predicate stays a pure
        /// function of frame state either way, so a re-execution destroys the same set.</para>
        /// </summary>
        public void DestroyDue(ref Frame frame, int tick)
        {
            int due = 0;
            var filter = frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                if (frame.GetReadOnly<BuildingComponent>(entity).RemovalEffectiveTick <= tick
                    && due < _due.Length)
                    _due[due++] = entity;
            }
            for (int i = 0; i < due; i++)
                frame.DestroyEntity(_due[i]);
        }

        // ── IFPNavMeshInstaller ─────────────────────────────────────────────────

        /// <summary>Swaps only. The reseed is <see cref="Reseed"/>, and the two being separate is
        /// what keeps a hashed frame write from riding along on a skippable install.</summary>
        public void Install(ref Frame frame, FPNavMesh mesh)
            => _botFSM.SwapForRestoredState(ref frame, mesh);

        /// <summary>Reseeds against the mesh already installed. Re-collects the agent set, which the
        /// driver relies on: on a boundary whose mesh was already right there is no install ahead of
        /// this to have refreshed anything.</summary>
        public void Reseed(ref Frame frame) => _botFSM.ReseedOnly(ref frame);
    }
}
