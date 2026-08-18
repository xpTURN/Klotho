using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// The call protocol for putting a rebaked mesh in front of the agents, as two named halves.
    ///
    /// <para><b>Why the halves are named separately.</b> Installing a mesh is DERIVED state — it can
    /// be skipped when the right one is already in place. Reseeding WRITES HASHED FRAME STATE, so it
    /// has to run every time the tick that owns it executes, including every re-execution after a
    /// rollback. Fusing them makes the write conditional on a peer-local comparison that does not
    /// roll back, and a boundary tick re-executed after its mesh was already installed then skips
    /// the reseed entirely — the authority reseeded once and kept the result, the client's last pass
    /// left the agent with the triangle index it had before the swap. That divergence took four live
    /// matches to find, and having one name for each half is what makes it hard to
    /// reintroduce.</para>
    ///
    /// <para><b>The agent buffer belongs to the caller.</b> These take it by <c>ref</c> and grow it;
    /// they do not own it. That is deliberate: a game's own per-tick pipeline usually walks the same
    /// set (Brawler's bot FSM appends newly created agents to it, drives the agent system with it,
    /// and writes bot state through it), and the two consumers have to agree on the COUNT — a peer
    /// reseeding fewer agents than another is a desync, not a glitch. Keeping one buffer on the
    /// caller's side is what makes them unable to disagree.</para>
    ///
    /// <para><b>Grow, never truncate</b>, for the same reason. An agent past a cut keeps a
    /// <c>CurrentTriangleIndex</c> and corridor that index the OLD mesh, which is precisely what the
    /// reseed exists to prevent. The asymmetry bites hardest right after a FullState apply: that
    /// peer may not have run a single update yet, so its array is still at its initial size while
    /// the authority's has already grown.</para>
    ///
    /// <para><b>Precondition.</b> <see cref="Swap"/> uses the one-argument
    /// <see cref="FPNavAgentSystem.SwapNavMesh(FPNavMesh)"/>, which rebinds the query, pathfinder
    /// and funnel the system already holds rather than building new ones (~1.4 MB a placement on a
    /// Field-sized stage). It therefore requires that trio to be installed already — through the
    /// constructor or the four-argument overload — and says so by throwing if it is not.</para>
    ///
    /// <para>Logging is left to the caller. The lines these paths emit are how a live match is read
    /// afterwards — installs and reseeds are counted by grepping them — so the prefix stays the
    /// game's rather than becoming this type's.</para>
    /// </summary>
    public static class FPNavAgentInstaller
    {
        /// <summary>
        /// Installs <paramref name="mesh"/> and refreshes <paramref name="agents"/> from the frame.
        /// Returns the agent count, which the caller must store — the collection is what makes the
        /// following passes run on the post-swap set.
        ///
        /// <para>Does NOT reseed. A caller that needs the reseed too calls <see cref="Reseed"/>
        /// after this, and the fact that it is a second call is the point (see the type's remarks).</para>
        /// </summary>
        public static int Swap(
            ref Frame frame, FPNavAgentSystem navSystem, FPNavMesh mesh, ref EntityRef[] agents)
        {
            if (navSystem == null)
                throw new System.ArgumentException("FPNavAgentInstaller.Swap: navSystem is null");

            navSystem.SwapNavMesh(mesh);
            return Collect(ref frame, ref agents);
        }

        /// <summary>
        /// Reseeds every agent against the mesh ALREADY installed, without swapping. Returns the
        /// agent count.
        ///
        /// <para><b>Re-collects, always.</b> The caller's buffer may hold last tick's set — a
        /// command phase runs before the update that maintains it — and this is the path a boundary
        /// tick takes when the mesh it wants is already installed, so there is no
        /// <see cref="Swap"/> ahead of it to have refreshed anything. Reseeding a stale set writes
        /// hashed state for the wrong entities.</para>
        /// </summary>
        public static int Reseed(
            ref Frame frame, FPNavAgentSystem navSystem, ref EntityRef[] agents)
        {
            if (navSystem == null)
                throw new System.ArgumentException("FPNavAgentInstaller.Reseed: navSystem is null");

            int count = Collect(ref frame, ref agents);
            navSystem.ReseedAgents(ref frame, agents, count);
            return count;
        }

        /// <summary>
        /// Fills <paramref name="agents"/> with every entity carrying a
        /// <see cref="NavAgentComponent"/> and returns how many. Grows the array as needed and
        /// never shrinks it.
        /// </summary>
        public static int Collect(ref Frame frame, ref EntityRef[] agents)
        {
            if (agents == null)
                agents = new EntityRef[16];

            int count = 0;
            var filter = frame.Filter<NavAgentComponent>();
            while (filter.Next(out var entity))
            {
                if (count + 1 > agents.Length)
                {
                    int size = agents.Length;
                    while (size < count + 1)
                        size *= 2;
                    System.Array.Resize(ref agents, size);
                }
                agents[count++] = entity;
            }
            return count;
        }
    }
}
