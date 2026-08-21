using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// 2×2 footprints on the real Field asset, in two sets, shared by the preview allocation gate
    /// and the preview perf test.
    ///
    /// <para><b>Two sets, and that is forced rather than sloppy.</b> A footprint that clips a wall
    /// cannot be placed under <see cref="FPBoundaryPlacementPolicy.Reject"/> at all, and one Reject
    /// accepts is by definition not touching the boundary — so under
    /// <see cref="FPBoundaryPlacementPolicy.ClipOverlap"/> it carves its plain footprint and never
    /// enters the clip stage. No single geometry measures both paths.</para>
    ///
    /// <para><b>Shared rather than copied.</b> Both callers quote the same numbers in their
    /// assertion messages, so a table that drifted in one file would leave the other's recorded
    /// baseline describing placements it no longer measures.</para>
    ///
    /// <para>Both sets were found by a sweep that added one footprint at a time and kept it only
    /// while the whole set still validated, then confirmed the final set with a real rebake. Field
    /// has no long clear corridor, so a hand-placed row is refused outright — the same lesson
    /// <c>FieldBuildings</c> records. If Field is ever re-baked these stop being valid and both
    /// callers fail loudly with a rejection rather than quietly measuring something else.</para>
    /// </summary>
    internal static class FieldPlacementSpots
    {
        /// <summary>Strictly inside the walkable region — placeable under every policy, and
        /// therefore never entering the clip stage. The last entry is the ghost.</summary>
        internal static readonly (double x, double z)[] Strict =
        {
            (-56,-63),(-56,-59),(-56,57),(-56,61),(-26,-83),(-26,-79),(-26,-75),(-26,-71),
            (-26,-67),(-26,-63),(-26,-59),(-26,-55),(-26,-51),(-26,-23),(-26,-19),(-26,-15),
            (-26,-11),(-26,-7),(-26,-3),(-26,1),(-26,5),(-26,9),(-26,37),(-26,41),
            (-26,45),(-26,49),(-26,53),(-26,57),(-26,61),(-26,65),(-26,69),(44,-83),
            (44,-79),
        };

        /// <summary>Overhanging the boundary — accepted only under `ClipOverlap`, and each one
        /// actually emits a clip ring. The last entry is the ghost.</summary>
        internal static readonly (double x, double z)[] Clipping =
        {
            (-60,-80),(-60,-35),(-60,-29),(-60,-23),(-60,-17),(-60,25),(-60,37),(-60,43),
            (-57,-62),(-57,-2),(-57,58),(-54,-80),(-54,-56),(-54,-50),(-54,-35),(-54,-29),
            (-54,-23),(-54,-17),(-54,4),(-54,10),(-54,25),(-54,37),(-54,43),(-54,64),
            (-54,70),(-51,-65),(-51,-5),(-51,55),(-48,-80),(-48,-59),(-48,-47),(-48,-35),
            (-48,61),
        };

        internal static FPBuildingRect At((double x, double z) p) => new FPBuildingRect(
            FP64.FromDouble(p.x), FP64.FromDouble(p.z),
            FP64.FromDouble(p.x + 2), FP64.FromDouble(p.z + 2), FP64.Zero);

        /// <summary>The first <paramref name="n"/> of a set, as footprints.</summary>
        internal static FPBuildingRect[] Take((double x, double z)[] spots, int n)
        {
            var result = new FPBuildingRect[n];
            for (int i = 0; i < n; i++) result[i] = At(spots[i]);
            return result;
        }

        /// <summary>The ghost — the last entry, never placed.</summary>
        internal static FPBuildingRect Ghost((double x, double z)[] spots)
            => At(spots[spots.Length - 1]);
    }
}
