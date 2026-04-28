using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    public class TimerSystem : ISystem
    {
        readonly EventSystem _events;

        public TimerSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<GameTimerStateComponent>();
            if (!filter.Next(out var entity)) return;

            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);

            ref var state = ref frame.Get<GameTimerStateComponent>(entity);

            if (state.StartTick < 0)
            {
                state.StartTick = frame.Tick;
                state.LastReportedSeconds = rules.GameDurationSeconds;
            }

            int elapsedMs    = (frame.Tick - state.StartTick) * frame.DeltaTimeMs;
            int remainingMs  = rules.GameDurationSeconds * 1000 - elapsedMs;
            int remainingSec = remainingMs > 0 ? (remainingMs + 999) / 1000 : 0;

            if (remainingSec != state.LastReportedSeconds)
            {
                state.LastReportedSeconds = remainingSec;
                var timerEvt = EventPool.Get<RoundTimerEvent>();
                timerEvt.RemainingSeconds = remainingSec;
                _events.Enqueue(timerEvt);
            }
        }
    }
}
