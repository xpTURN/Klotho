using System;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS.Diagnostics
{
    // Per-type high-water (peak) live-count sampler for maxCount sizing. Off by default: constructing
    // it is what enables it. Subscribes to the engine's verified-frame event so each confirmed tick is
    // sampled exactly once and predicted / re-simulated frames are never counted. Read-only: writes only
    // to its own int[] (no simulation state is touched, so it is determinism-neutral).
    //
    // Lifecycle: construct after the simulation exists (so MaxTypeId is final), Reset() at match start,
    // Dispose() at teardown to unsubscribe.
    public sealed class ComponentMemoryPeakSampler : IDisposable
    {
        private readonly int[] _peakCount;
        private readonly IKlothoEngine _engine;
        private readonly EcsSimulation _simulation;
        private int _observedTicks;
        private bool _subscribed;

        // Manual-drive mode: caller invokes Sample(frame) itself (tests, offline, custom wiring).
        public ComponentMemoryPeakSampler(int maxTypeId)
        {
            _peakCount = new int[maxTypeId + 1];
        }

        // Auto-subscribe mode: samples on the engine's verified-frame event.
        public ComponentMemoryPeakSampler(IKlothoEngine engine, EcsSimulation simulation)
            : this(ComponentStorageRegistry.MaxTypeId)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            _engine.OnFrameVerified += HandleFrameVerified;
            _subscribed = true;
        }

        // Indexed by typeId (length MaxTypeId+1). Pass to ComponentMemoryAnalyzer.Capture.
        public int[] PeakCounts => _peakCount;
        public int ObservedTicks => _observedTicks;

        // Fold one frame's live counts into the running per-type maxima. Read-only w.r.t. the frame.
        public void Sample(Frame frame)
        {
            if (frame == null) return;
            ReadOnlySpan<int> typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                int live = frame.GetLiveCount(typeId);
                if (live > _peakCount[typeId]) _peakCount[typeId] = live;
            }
            _observedTicks++;
        }

        // Clear accumulated peaks + tick count. Call at match start, otherwise peaks accumulate across
        // matches and the per-match high-water reading is lost.
        public void Reset()
        {
            Array.Clear(_peakCount, 0, _peakCount.Length);
            _observedTicks = 0;
        }

        private void HandleFrameVerified(int tick) => Sample(_simulation.Frame);

        public void Dispose()
        {
            if (_subscribed)
            {
                _engine.OnFrameVerified -= HandleFrameVerified;
                _subscribed = false;
            }
        }
    }
}
