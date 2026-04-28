using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// In PostUpdate, detects the game-over condition and enqueues GameOverEvent.
    ///   - When a character with StockCount == 0 appears → the player with the highest stock count among the rest wins ("stocks")
    ///   - When the time limit is exceeded → the player with the most stocks wins; on a tie the lower PlayerId wins ("timeout")
    /// The event is emitted only once.
    /// </summary>
    public class GameOverSystem : ISystem
    {
        const int GraceTicks = 10;

        static readonly FixedString32 ReasonStocks  = FixedString32.FromString("stocks");
        static readonly FixedString32 ReasonTimeout = FixedString32.FromString("timeout");

        readonly EventSystem _events;

        public GameOverSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var stateFilter = frame.Filter<GameTimerStateComponent>();
            if (!stateFilter.Next(out var stateEntity)) return;

            ref var state = ref frame.Get<GameTimerStateComponent>(stateEntity);

            if (state.GameOverFired) return;

            if (state.StartTick < 0) state.StartTick = frame.Tick;

            if (TryStocksGameOver(ref frame, ref state)) return;
            TryTimeoutGameOver(ref frame, ref state);
        }

        // ────────────────────────────────────────────
        // Stock depletion detection
        // ────────────────────────────────────────────
        bool TryStocksGameOver(ref Frame frame, ref GameTimerStateComponent state)
        {
            if (frame.Tick - state.StartTick < GraceTicks) return false;

            int aliveCount  = 0;
            int winnerPlayerId = -1;
            int winnerStocks   = -1;

            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (c.StockCount > 0)
                {
                    aliveCount++;
                    if (c.StockCount > winnerStocks ||
                       (c.StockCount == winnerStocks && c.PlayerId < winnerPlayerId))
                    {
                        winnerStocks   = c.StockCount;
                        winnerPlayerId = c.PlayerId;
                    }
                }
            }

            if (aliveCount > 1) return false;

            if (aliveCount == 0)
            {
                FireGameOver(ref state, -1, ReasonStocks);
                return true;
            }

            FireGameOver(ref state, winnerPlayerId, ReasonStocks);
            return true;
        }

        // ────────────────────────────────────────────
        // Timeout detection
        // ────────────────────────────────────────────
        void TryTimeoutGameOver(ref Frame frame, ref GameTimerStateComponent state)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
            int gameDurationMs = rules.GameDurationSeconds * 1000;

            int elapsedMs = (frame.Tick - state.StartTick) * frame.DeltaTimeMs;
            if (elapsedMs < gameDurationMs) return;

            // Find the player with the most stocks (lower PlayerId wins on a tie)
            int winnerPlayerId = -1;
            int winnerStocks   = -1;

            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (c.StockCount > winnerStocks ||
                   (c.StockCount == winnerStocks && c.PlayerId < winnerPlayerId))
                {
                    winnerStocks   = c.StockCount;
                    winnerPlayerId = c.PlayerId;
                }
            }

            FireGameOver(ref state, winnerPlayerId, ReasonTimeout);
        }

        void FireGameOver(ref GameTimerStateComponent state, int winnerId, FixedString32 reason)
        {
            state.GameOverFired = true;
            var goEvt = EventPool.Get<GameOverEvent>();
            goEvt.WinnerPlayerId = winnerId;
            goEvt.Reason         = reason;
            _events.Enqueue(goEvt);
        }
    }
}
