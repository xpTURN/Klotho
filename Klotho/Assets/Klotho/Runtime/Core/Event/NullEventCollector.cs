namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// No-op EventCollector. Fully disables event collection in server headless mode.
    /// Inherits from EventCollector for compatibility with existing code (_eventCollector.BeginTick/Count/Collected).
    /// Overrides RaiseEvent to also block calls routed through the interface.
    /// </summary>
    public sealed class NullEventCollector : EventCollector
    {
        public static readonly NullEventCollector Instance = new NullEventCollector();

        public override void RaiseEvent(SimulationEvent evt) { }
    }
}
