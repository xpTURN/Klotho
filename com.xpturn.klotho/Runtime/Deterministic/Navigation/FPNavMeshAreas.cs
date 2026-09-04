namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// The area indices and masks the runtime reserves for itself.
    ///
    /// <para><b>Area 1 belongs to retained buildings.</b> A placement with
    /// <see cref="FPBuildingPlacement.Retain"/> keeps its footprint as triangulated ground, and the
    /// rebaker stamps every triangle inside it <see cref="BUILDING_AREA"/> — exclusively, so a
    /// query that does not carry <see cref="BUILDING_MASK"/> treats the footprint as a wall while a
    /// query that does (<see cref="ALL_AREAS"/>) plans straight through it. That asymmetry is the
    /// whole point of retaining rather than carving: a route can be <i>computed</i> through a
    /// building the mover is not allowed to <i>cross</i>, and the layer above decides which side of
    /// that line each caller is on.</para>
    ///
    /// <para><b>Why 1.</b> Unity's built-in "Not Walkable" is area 1 and never reaches the exported
    /// triangulation; Godot's exporter writes area 0 for every triangle. So no baked asset carries
    /// it today, and <c>FPNavMeshBuildPipeline.Build</c> refuses one that does — an authored
    /// triangle in this area would be invisible to every agent on <see cref="DEFAULT_AGENT_MASK"/>
    /// and nothing would say why.</para>
    ///
    /// <para><b>The stamp is exclusive, not OR-ed in.</b> Area masks intersect: a triangle is
    /// walkable to a query when the two share <i>any</i> bit. A footprint stamped
    /// <c>base | BUILDING_MASK</c> would still share the base bit with every default query and
    /// block nothing. The rebaker therefore writes <c>areaMask = BUILDING_MASK</c>, and the base
    /// area of a retained triangle is not recoverable from the mesh — by design.</para>
    /// </summary>
    public static class FPNavMeshAreas
    {
        /// <summary>Area index the rebaker stamps onto retained building footprints.</summary>
        public const int BUILDING_AREA = 1;

        /// <summary>The mask bit of <see cref="BUILDING_AREA"/>: what a retained triangle's
        /// <c>areaMask</c> equals after the rebake, exclusively.</summary>
        public const int BUILDING_MASK = 1 << BUILDING_AREA;

        /// <summary>
        /// Every area except <see cref="BUILDING_AREA"/> — the mask <c>FPNavAgentSystem</c> hands
        /// to <c>FindPath</c> and <c>MoveAlongSurface</c>, so an agent neither plans through nor
        /// walks into a retained footprint.
        /// </summary>
        public const int DEFAULT_AGENT_MASK = ~BUILDING_MASK;

        /// <summary>Every area, retained footprints included: the mask for a planner that is
        /// allowed to route through buildings (fog of war, memory, AI planning on stale
        /// information).</summary>
        public const int ALL_AREAS = ~0;
    }
}
