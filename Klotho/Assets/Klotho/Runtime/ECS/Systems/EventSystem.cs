using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS.Systems
{
    /// <summary>
    /// ECS event system: events enqueued by other systems are batch-published via ISimulationEventRaiser in the LateUpdate Phase.
    /// Usage: var evt = EventPool.Get&lt;DamageEvent&gt;(); evt.DamageAmount = 10; system.Enqueue(evt);
    /// </summary>
    public class EventSystem : ISystem
    {
        private readonly List<SimulationEvent> _queue = new List<SimulationEvent>();

        /// <summary>
        /// Called from other systems. Enqueues events within the Frame Tick.
        /// </summary>
        public void Enqueue(SimulationEvent evt)
        {
            _queue.Add(evt);
        }

        /// <summary>
        /// Called in the LateUpdate Phase. Batch-publishes queued events via frame.EventRaiser and clears the queue.
        /// frame.EventRaiser is set after KlothoEngine.Initialize(), so it is referenced directly each tick.
        /// </summary>
        public void Update(ref Frame frame)
        {
            if (frame.EventRaiser == null)
            {
                _queue.Clear();
                return;
            }
            for (int i = 0; i < _queue.Count; i++)
                frame.EventRaiser.RaiseEvent(_queue[i]);
            _queue.Clear();
        }
    }
}
