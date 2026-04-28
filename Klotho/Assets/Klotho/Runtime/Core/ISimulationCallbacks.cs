namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation-side callbacks — common to all peers (server / client / replay).
    /// Only deterministic code is allowed.
    /// </summary>
    public interface ISimulationCallbacks
    {
        /// <summary>
        /// Register simulation systems.
        /// Called immediately after EcsSimulation is created and before Engine.Initialize().
        /// </summary>
        void RegisterSystems(ECS.EcsSimulation simulation);

        /// <summary>
        /// Create world-initialization entities.
        /// Called inside Engine.Start(), before SaveSnapshot(0).
        /// Invoked identically on every peer, so only deterministic code is allowed.
        /// </summary>
        void OnInitializeWorld(IKlothoEngine engine);

        /// <summary>
        /// Input polling immediately before a tick.
        /// The game sends as many commands as desired via sender.
        /// If no command is sent, an EmptyCommand is automatically injected.
        /// </summary>
        void OnPollInput(int playerId, int tick, ICommandSender sender);
    }
}
