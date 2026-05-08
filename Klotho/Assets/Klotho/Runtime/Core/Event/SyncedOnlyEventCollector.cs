namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server-mode EventCollector. Stores only EventMode.Synced events, drops Regular ones.
    /// Regular events have no server-side subscribers (they are visualization hooks consumed on
    /// clients via deterministic re-simulation). Synced events are server-only and must reach
    /// the network layer for unicast feedback (e.g. CommandRejectedSimEvent → CommandRejectedMessage).
    /// </summary>
    public sealed class SyncedOnlyEventCollector : EventCollector
    {
        public override void RaiseEvent(SimulationEvent evt)
        {
            if (evt == null || evt.Mode != EventMode.Synced)
                return;
            base.RaiseEvent(evt);
        }
    }
}
