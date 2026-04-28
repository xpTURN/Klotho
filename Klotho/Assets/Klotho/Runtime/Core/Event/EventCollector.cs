using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Collects events raised during a single Tick() execution.
    /// ISimulationEventRaiser implementation. Reusable (GC-free).
    /// </summary>
    public class EventCollector : ISimulationEventRaiser
    {
        private readonly List<SimulationEvent> _collected = new List<SimulationEvent>();
        private int _currentTick;

        public IReadOnlyList<SimulationEvent> Collected => _collected;
        public int Count => _collected.Count;

        public void BeginTick(int tick)
        {
            _currentTick = tick;
            _collected.Clear();
        }

        public virtual void RaiseEvent(SimulationEvent evt)
        {
            evt.Tick = _currentTick;
            _collected.Add(evt);
        }

        public void Clear()
        {
            _collected.Clear();
        }
    }
}
