namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Why an agent sits at <see cref="FPNavAgentStatus.PathFailed"/>.
    ///
    /// <para>The order of the members is the order <see cref="FPNavMeshPathfinder.FindPath"/>
    /// refuses in, and <see cref="FPNavPathFailure.Diagnose"/> walks it in that order. Renumbering
    /// them would silently reorder the diagnosis.</para>
    /// </summary>
    public enum FPNavPathFailureReason
    {
        None,
        AgentOffMesh,
        AgentOnBlockedGround,
        DestinationOffMesh,
        DestinationBlocked,
        DestinationAreaMasked,
        /// <summary>The failure predates the mesh now installed: its cause is gone.</summary>
        StaleFailure,
        NoRouteOrBudget,
    }

    /// <summary>
    /// Names the cause of a <see cref="FPNavAgentStatus.PathFailed"/> without a counter, by
    /// re-walking the refusals <see cref="FPNavMeshPathfinder.FindPath"/> makes.
    ///
    /// <para><b>Public, and in the runtime, for the same reason
    /// <see cref="FPNavMeshQuery.FindTriangleForEndpoint"/> is.</b> Both editor tools need this,
    /// and they are not one assembly: Unity's is fixed and could have been named in
    /// <c>InternalsVisibleTo</c>, but the Godot adapter ships as SOURCE inside
    /// <c>addons/klotho/Adapters/</c> and is compiled into whatever assembly the consuming Godot
    /// project happens to be — including projects outside this repository. There is no list to
    /// write, so <c>internal</c> cannot reach it. Writing the diagnosis twice instead is what this
    /// type replaced: the two copies had already drifted in their comments.</para>
    ///
    /// <para><b>Not a simulation input.</b> Nothing here is called from the tick; the members read
    /// state and never write it. <see cref="Diagnose"/> resolves the endpoint with
    /// <see cref="FPNavMeshQuery.FindTriangleForEndpoint"/> — the same member the engine uses — so
    /// a tool cannot report a mask refusal the engine never made.</para>
    /// </summary>
    public static class FPNavPathFailure
    {
        /// <summary>
        /// The cause, or <see cref="FPNavPathFailureReason.None"/> when the agent is not failed or
        /// the caller has no mesh yet (a tool asks before a load).
        /// </summary>
        /// <param name="nav">The agent to explain.</param>
        /// <param name="query">Query over <paramref name="mesh"/>; null answers None.</param>
        /// <param name="mesh">The mesh the agent is on; null answers None.</param>
        /// <param name="failurePredatesSwap">
        /// Whether this failure was already standing when the current mesh was installed. The
        /// caller owns that bookkeeping — it is the only way to tell
        /// <see cref="FPNavPathFailureReason.StaleFailure"/> from
        /// <see cref="FPNavPathFailureReason.NoRouteOrBudget"/>, which look identical from here.
        /// </param>
        public static FPNavPathFailureReason Diagnose(
            in NavAgentComponent nav, FPNavMeshQuery query, FPNavMesh mesh, bool failurePredatesSwap)
        {
            if (nav.Status != (byte)FPNavAgentStatus.PathFailed)
                return FPNavPathFailureReason.None;
            if (query == null || mesh == null)
                return FPNavPathFailureReason.None;

            // The agent's own footing first: the reseed-lost case is the one no counter explains.
            if (nav.CurrentTriangleIndex < 0)
                return FPNavPathFailureReason.AgentOffMesh;
            if (mesh.Triangles[nav.CurrentTriangleIndex].isBlocked)
                return FPNavPathFailureReason.AgentOnBlockedGround;

            // Then the endpoint, in FindPath's order: lookup, isBlocked, mask. The START's mask is
            // deliberately absent — the engine exempts it and only reports it, so naming it as a
            // cause would be a lie.
            int mask = FPNavAgentSystem.ResolvePlanMask(nav);
            int endTri = query.FindTriangleForEndpoint(
                nav.Destination.ToXZ(), nav.Destination.y, mask);
            if (endTri < 0)
                return FPNavPathFailureReason.DestinationOffMesh;

            ref readonly var endTriangle = ref mesh.Triangles[endTri];
            if (endTriangle.isBlocked)
                return FPNavPathFailureReason.DestinationBlocked;
            if ((mask & endTriangle.areaMask) == 0)
                return FPNavPathFailureReason.DestinationAreaMasked;

            // Every endpoint check passes. Either the cause is gone (the mesh changed under a
            // failure that was never re-planned) or there genuinely is no route. Only the first is
            // knowable from here — hence the swap marker.
            return failurePredatesSwap
                ? FPNavPathFailureReason.StaleFailure
                : FPNavPathFailureReason.NoRouteOrBudget;
        }

        /// <summary>
        /// The reason as a suffix a tool can append to an agent row. Empty for
        /// <see cref="FPNavPathFailureReason.None"/> — a caller appends unconditionally.
        ///
        /// <para>These strings live here rather than in each tool so the two engines cannot
        /// describe the same state differently. That makes the wording a contract: changing it is
        /// a CHANGELOG entry.</para>
        /// </summary>
        public static string Describe(FPNavPathFailureReason reason)
        {
            switch (reason)
            {
                case FPNavPathFailureReason.AgentOffMesh:
                    return " ← agent is off the mesh (a rebake left it there; nothing re-acquires it)";
                case FPNavPathFailureReason.AgentOnBlockedGround:
                    return " ← the agent stands on blocked ground";
                case FPNavPathFailureReason.DestinationOffMesh:
                    return " ← the destination is off the mesh";
                case FPNavPathFailureReason.DestinationBlocked:
                    return " ← the destination is blocked (closed in code, not by a mask)";
                case FPNavPathFailureReason.DestinationAreaMasked:
                    return " ← the destination's area is outside this agent's plan mask";
                case FPNavPathFailureReason.StaleFailure:
                    return " ← stale: nothing blocks it now (the mesh changed) — set the destination again";
                case FPNavPathFailureReason.NoRouteOrBudget:
                    return " ← no route (or the search budget ran out)";
                default:
                    return "";
            }
        }
    }
}
