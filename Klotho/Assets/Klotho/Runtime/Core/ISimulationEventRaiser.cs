namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Interface for raising simulation events during Tick() execution.
    /// Set on the concrete Simulation before tick execution.
    /// </summary>
    public interface ISimulationEventRaiser
    {
        /// <summary>
        /// Raises a game event during a simulation tick.
        /// The event's Tick is set automatically by the collector.
        /// </summary>
        void RaiseEvent(SimulationEvent evt);
    }
}
