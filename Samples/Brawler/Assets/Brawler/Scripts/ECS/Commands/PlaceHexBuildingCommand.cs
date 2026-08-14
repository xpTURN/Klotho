using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Deterministic hexagon placement. A sibling of
    /// <see cref="PlaceBuildingCommand"/>, separate because the INPUT differs: a hexagon has no
    /// orientation to send.
    ///
    /// <para>Not a shortcut — no integer hexagon is symmetric under 60 degrees (a vertex at (2a, 0)
    /// turned 60 degrees needs the irrational b = a*sqrt(3)), so the orientations a rotate button
    /// would offer simply do not exist in the catalog. Folding this into the box command as
    /// "orientation ignored for hexagons" would put a field on the wire that must be ignored, and
    /// a field that must be ignored eventually is not.</para>
    ///
    /// <para>The STORED state is the same either way — <see cref="BuildingComponent"/> keeps a
    /// catalog entry and a centre, and does not care which command produced them. So this splits
    /// the input surface, not the state.</para>
    /// </summary>
    [KlothoSerializable(117)]
    public partial class PlaceHexBuildingCommand : CommandBase, IReliableCommand
    {
        /// <summary>World position of the hexagon's centre.</summary>
        [KlothoOrder(0)] public FPVector3 Centre;
        [KlothoOrder(1)] public int SequenceNumber { get; set; }

        // Same ordering class as the box: after Spawn (0), so units spawned this tick are
        // included in validation and reseed.
        public int OrderKey => 1;
    }
}
