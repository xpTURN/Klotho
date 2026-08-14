using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// A building placed at runtime. Frame state so the building set
    /// rides FullState — a joiner rebakes once from these components, which a system-local list
    /// could never reproduce without command history.
    ///
    /// Stores the CATALOG REFERENCE and the centre, not a footprint. Two things follow from that:
    /// the +BakeAgentRadius expansion stays the snapshot's business (its radius remains the single
    /// source of truth), and the shape table becomes part of the determinism envelope — see
    /// <see cref="BrawlerBuildingShapes"/>.
    /// </summary>
    [KlothoComponent(108, MaxCount = 32)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BuildingComponent : IComponent
    {
        /// <summary>World position of the shape's centre. One FPVector3 rather than three scalars
        /// because that is what it is — and the rebaker's placement wants x/z/y anyway, so the
        /// grouping costs nothing at the only site that reads it.</summary>
        public FPVector3 Centre;
        /// <summary>
        /// Order this building entered the set. The rebake input is sorted by it, and that choice
        /// is worth more than it looks.
        ///
        /// <para>Any deterministic key satisfies the correctness requirement — the rebake must be
        /// a function of the frame, not of entity iteration order — so sorting by CENTRE would do
        /// just as well. It costs a great deal though: the rebaker appends each building's hole
        /// vertices in list order, so a list ordered by placement only ever grows at the end and
        /// every existing vertex index keeps its meaning, which is what lets a rebake patch the
        /// previous navmesh instead of rebuilding it. Sorted by centre, a building placed at a low
        /// x lands in the middle and shifts every later building's vertices, and the rebaker falls
        /// back to a full rebuild. Measured on a 60-placement run: 96% of rebakes patched in
        /// placement order, 18% sorted by centre.</para>
        ///
        /// <para>Assigned as one past the highest in the current set, which is a function of frame
        /// state and therefore survives rollback. A number freed by a removal can be handed out
        /// again later, and that is fine.</para>
        ///
        /// <para><b>It must be unique among the buildings alive at any one moment</b>, and
        /// "one past the highest" is what guarantees that rather than a nicety of it. The sort
        /// that orders the rebake input is unstable, so two buildings sharing a number would be
        /// ordered by whichever the entity iteration happened to reach first — and the rebake
        /// would stop being a function of the frame. Peers would carve different navmeshes from
        /// identical components, with the state hash still matching. Numbering per owner, or
        /// reusing a freed number while its neighbours are still standing, breaks this;
        /// CollectBuildings reports a duplicate loudly for that reason.</para>
        /// </summary>
        public int Sequence;
        /// <summary>Which shape, as the catalog builder handed it out.</summary>
        public int ShapeId;
        /// <summary>Which turn of it. 0 for a shape that does not turn, such as the hexagon.</summary>
        public int Orientation;
        public int OwnerSlot;
    }
}
