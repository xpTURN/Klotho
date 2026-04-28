using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Kind of frame reference. Marker that distinguishes which reference the view layer is using.
    /// </summary>
    public enum FrameKind
    {
        /// <summary>The frame at the latest verified tick.</summary>
        Verified,
        /// <summary>The live frame at the current CurrentTick.</summary>
        Predicted,
        /// <summary>The frame at CurrentTick - 1. Pair used to interpolate the period difference between the render clock and the simulation clock.</summary>
        PredictedPrevious,
        /// <summary>The head frame at the entry of the previous Update.</summary>
        PreviousUpdatePredicted,
    }

    /// <summary>
    /// Frame reference (tick + Frame + kind). Frame is null when outside the ring buffer range.
    /// </summary>
    public readonly struct FrameRef
    {
        public int Tick { get; }
        public Frame Frame { get; }
        public FrameKind Kind { get; }

        public FrameRef(int tick, Frame frame, FrameKind kind)
        {
            Tick = tick;
            Frame = frame;
            Kind = kind;
        }

        public static FrameRef None(FrameKind kind) => new FrameRef(-1, null, kind);
    }
}
