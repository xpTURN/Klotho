using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Deterministic placement of an oriented box. A placement is a CATALOG
    /// REFERENCE plus a centre, not a footprint: which shape of
    /// <see cref="BrawlerBuildingShapes"/> to build, which turn of it, and where its centre goes.
    ///
    /// <para>The centre rather than a corner, because the catalog stores offsets about the centre —
    /// that keeps the shape's symmetry invariants independent of where the game chose to anchor, and
    /// a centre also stays put when the shape turns (a bounding-box corner moves, which would make
    /// "rotate in place" a position change as well).</para>
    ///
    /// <para>Same-tick ordering across peers is engine-guaranteed (CommandOrdering:
    /// OrderKey -> CommandTypeId -> PlayerId -> SequenceNumber).</para>
    /// </summary>
    [KlothoSerializable(115)]
    public partial class PlaceBuildingCommand : CommandBase, IReliableCommand
    {
        /// <summary>Which shape of <see cref="BrawlerBuildingShapes.Catalog"/> to build. Spelled
        /// out rather than assumed to be the box, so the payload says what it places instead of
        /// leaving it to a convention the handler happens to share.</summary>
        [KlothoOrder(0)] public int ShapeId;
        /// <summary>Which turn of that shape, in <c>[0, DirectionCount(ShapeId))</c>. 0 for a shape
        /// that does not turn.</summary>
        [KlothoOrder(1)] public int Orientation;
        /// <summary>World position of the building's centre.</summary>
        [KlothoOrder(2)] public FPVector3 Centre;
        [KlothoOrder(3)] public int SequenceNumber { get; set; }
        /// <summary>Retain the footprint as ground (stamped <c>FPNavMeshAreas.BUILDING_AREA</c>)
        /// instead of carving a hole. On the wire because it is a determinism input: a mode read from
        /// local state at the handler would diverge the navmesh while the state hash still matched.
        /// <see cref="PlaceHexBuildingCommand.Retain"/> is the hexagon's counterpart.</summary>
        [KlothoOrder(4)] public bool Retain;

        // Both indices arrive from the network, so both are untrusted, and the pair is validated
        // together in the handler — a shape that exists says nothing about whether it turns that
        // many ways. Validated there rather than inside the trial rebake because the rebake would
        // report it as a placement rejection, sending whoever reads the log to look at the
        // position.

        // After Spawn (0): units spawned this tick are included in validation/reseed.
        public int OrderKey => 1;
    }
}
