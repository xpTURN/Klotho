using System;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Thrown by <see cref="FPConstrainedDelaunay"/> when a constraint segment would cross an
    /// edge that is already a wall (the T1-NotAllowed refusal). Carries the two segments in
    /// snapped grid coordinates, so a caller can name the offending geometry instead of
    /// reporting "a constraint crosses an existing constraint" to someone holding an asset.
    ///
    /// <para>Derives from <see cref="InvalidOperationException"/> — the type the triangulator
    /// has always used here — so an existing <c>catch (InvalidOperationException)</c> keeps
    /// working. What the subtype buys is the ability to tell THIS refusal apart from the two
    /// internal-corruption throws on the same path (a non-terminating channel walk, a constraint
    /// direction not found), which is what
    /// <see cref="FPNavMeshRebaker.CreateSnapshot"/> needs in order to reclassify a crossing
    /// base mesh as an unsupported stage without hiding a triangulator bug.</para>
    /// </summary>
    public sealed class FPConstraintCrossingException : InvalidOperationException
    {
        /// <summary>The constraint being inserted, snapped grid coordinates.</summary>
        public long AX { get; }
        public long AZ { get; }
        public long BX { get; }
        public long BZ { get; }

        /// <summary>The already-constrained edge it would cross, snapped grid coordinates.</summary>
        public long CrossedX0 { get; }
        public long CrossedZ0 { get; }
        public long CrossedX1 { get; }
        public long CrossedZ1 { get; }

        public FPConstraintCrossingException(
            long ax, long az, long bx, long bz,
            long crossedX0, long crossedZ0, long crossedX1, long crossedZ1)
            : base("FPConstrainedDelaunay: constraint crosses an existing constraint (T1 NotAllowed): "
                + $"({ax},{az})-({bx},{bz}) crosses ({crossedX0},{crossedZ0})-({crossedX1},{crossedZ1})")
        {
            AX = ax; AZ = az; BX = bx; BZ = bz;
            CrossedX0 = crossedX0; CrossedZ0 = crossedZ0;
            CrossedX1 = crossedX1; CrossedZ1 = crossedZ1;
        }

        /// <summary>The two segments alone, for a caller composing its own message.</summary>
        public string DescribeSegments()
            => $"({AX},{AZ})-({BX},{BZ}) crosses ({CrossedX0},{CrossedZ0})-({CrossedX1},{CrossedZ1})";
    }
}
