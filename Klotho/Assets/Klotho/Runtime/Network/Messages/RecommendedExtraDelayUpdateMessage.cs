using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server→client push of the latest recommended extra InputDelay. Sent only when the server's
    /// per-peer smoothed RTT change crosses the asymmetric threshold (UP=2 ticks, DOWN=4 ticks) and
    /// MIN_PUSH_INTERVAL_MS (=500) since the last push. Delivered ReliableOrdered — eventual
    /// consistency on transient loss.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.RecommendedExtraDelayUpdate)]
    public partial class RecommendedExtraDelayUpdateMessage : NetworkMessageBase
    {
        // Recommended extra InputDelay in ticks. Client applies via ApplyExtraDelay (absolute value).
        // ServerCurrentTick intentionally omitted — client's monotonic cmd.Tick clamp uses
        // _lastSentCmdTick alone, so the absolute server tick is not required for correctness.
        [KlothoOrder] public int RecommendedExtraDelay;

        // Diagnostic — server-side smoothed avgRtt at compute time. Echoed by the client's
        // [Metrics][DynamicDelay] emit for offline correlation analysis.
        [KlothoOrder] public int AvgRttMs;
    }
}
