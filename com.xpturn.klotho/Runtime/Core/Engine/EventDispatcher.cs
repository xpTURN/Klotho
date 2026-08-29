using System;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Engine-internal event dispatcher. Monitors callback execution time.
    /// </summary>
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
    internal struct EventDispatcher
    {
        private readonly IKLogger _logger;
        private readonly int _warnMs;

        public EventDispatcher(IKLogger logger, int warnMs)
        {
            _logger = logger;
            _warnMs = warnMs;
        }

        public void Dispatch<T>(Action<int, T> handler, int tick, T evt, string label)
            where T : SimulationEvent
        {
            if (handler == null) return;
            if (_warnMs <= 0) { handler.Invoke(tick, evt); return; }
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            handler.Invoke(tick, evt);
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - before;
            if (elapsed >= _warnMs)
                _logger?.KWarning($"[KlothoEngine] {label} slow: {elapsed}ms, tick={tick}, type={evt.EventTypeId}");
        }
    }
#else
    internal struct EventDispatcher
    {
        public EventDispatcher(IKLogger logger, int warnMs) { }

        public void Dispatch<T>(Action<int, T> handler, int tick, T evt, string label)
            where T : SimulationEvent => handler?.Invoke(tick, evt);
    }
#endif
}
