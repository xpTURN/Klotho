using System;
using System.Collections.Generic;
using ZLogger;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Reusable buffers for event diff computation (avoids GC allocation).
        private readonly List<SimulationEvent> _rollbackOldEventsCache = new List<SimulationEvent>();
        private readonly List<SimulationEvent> _rollbackNewEventsCache = new List<SimulationEvent>();

        // Last tick at which OnSyncedEvent has been dispatched. Guards against re-fire across
        // rollback/resim cycles where _lastVerifiedTick rewinds and chain re-advances past
        // already-dispatched ticks. Reset by ApplyFullState (event buffer ClearAll cascade)
        // and by Spectator.ResetToTick (per-tick re-emit).
        private int _syncedDispatchHighWaterMark = -1;

        #region Event System Helpers

        private void DispatchTickEvents(int tick, FrameState state)
        {
            var events = _eventBuffer.GetEvents(tick);
            if (state == FrameState.Verified)
            {
                DispatchSyncedEventsForTick(tick, events);
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var evt = events[ei];
                    if (evt.Mode != EventMode.Synced)
                        _dispatcher.Dispatch(OnEventConfirmed, tick, evt, nameof(OnEventConfirmed));
                }
            }
            else
            {
                // On Predicted ticks, only fire Regular events; Synced events are kept in the buffer only.
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var evt = events[ei];
                    if (evt.Mode == EventMode.Regular)
                        _dispatcher.Dispatch(OnEventPredicted, tick, evt, nameof(OnEventPredicted));
                }
            }
        }

        /// <summary>
        /// Dispatches all Synced events at <paramref name="tick"/> exactly once across the engine
        /// lifetime. Idempotent across rollback/resim: re-entry with the same tick (i.e.
        /// <paramref name="tick"/> &lt;= <c>_syncedDispatchHighWaterMark</c>) skips the entire batch.
        /// </summary>
        private void DispatchSyncedEventsForTick(int tick, IReadOnlyList<SimulationEvent> events)
        {
            if (tick <= _syncedDispatchHighWaterMark) return;
            bool anyFired = false;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.Mode != EventMode.Synced) continue;
                _dispatcher.Dispatch(OnSyncedEvent, tick, evt, nameof(OnSyncedEvent));
                anyFired = true;
            }
            if (anyFired) _syncedDispatchHighWaterMark = tick;
        }

        private void DiffRollbackEvents(int fromTick)
        {
            // Collection of events newly gathered after re-simulation.
            _rollbackNewEventsCache.Clear();
            for (int t = fromTick; t < CurrentTick; t++)
            {
                var newEvents = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < newEvents.Count; ei++)
                    _rollbackNewEventsCache.Add(newEvents[ei]);
            }

            // Regular events that occurred before but disappeared after re-simulation are dispatched as canceled.
            for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
            {
                var oldEvt = _rollbackOldEventsCache[oi];
                if (oldEvt.Mode != EventMode.Regular)
                    continue;

                bool found = false;
                long oldHash = oldEvt.GetContentHash();
                for (int ni = 0; ni < _rollbackNewEventsCache.Count; ni++)
                {
                    var newEvt = _rollbackNewEventsCache[ni];
                    if (newEvt.Tick == oldEvt.Tick &&
                        newEvt.EventTypeId == oldEvt.EventTypeId &&
                        newEvt.GetContentHash() == oldHash)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    _dispatcher.Dispatch(OnEventCanceled, oldEvt.Tick, oldEvt, nameof(OnEventCanceled));
            }

            // Newly appeared events are dispatched as Confirmed/Predicted depending on whether they are verified.
            // Synced events are dispatched separately after hash verification on the verified chain/batch path, so they are skipped here.
            for (int ni = 0; ni < _rollbackNewEventsCache.Count; ni++)
            {
                var newEvt = _rollbackNewEventsCache[ni];

                if (newEvt.Mode == EventMode.Synced) continue;

                bool found = false;
                long newHash = newEvt.GetContentHash();
                for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
                {
                    var oldEvt = _rollbackOldEventsCache[oi];
                    if (oldEvt.Tick == newEvt.Tick &&
                        oldEvt.EventTypeId == newEvt.EventTypeId &&
                        oldEvt.GetContentHash() == newHash)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    FrameState evtState = newEvt.Tick <= _lastVerifiedTick
                        ? FrameState.Verified : FrameState.Predicted;
                    if (evtState == FrameState.Verified)
                        _dispatcher.Dispatch(OnEventConfirmed, newEvt.Tick, newEvt, nameof(OnEventConfirmed));
                    else
                        _dispatcher.Dispatch(OnEventPredicted, newEvt.Tick, newEvt, nameof(OnEventPredicted));
                }
            }
        }

        #endregion
    }
}
