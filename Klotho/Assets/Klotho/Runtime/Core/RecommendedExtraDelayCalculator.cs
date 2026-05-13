using System;

namespace xpTURN.Klotho.Core
{
    // Pure computation for RecommendedExtraDelay — unit-testable, no instance state, no logging.
    // Shared by ServerNetworkService (SD) and KlothoNetworkService (P2P) so the formula stays single-source.
    //
    // Treat avgRtt <= 0 as invalid (no measurement) → fallback to safety only.
    // For valid samples above rttSanityMaxMs, clamp to the sanity cap rather than dropping to
    // safety-only — keeps storm-prevention monotonic in the RTT direction while bounding the
    // worst-case extraDelay against spurious measurements.
    internal static class RecommendedExtraDelayCalculator
    {
        internal static (int extraDelay, bool fallback, int rttTicks, int raw, bool clamped) Compute(
            int avgRtt, int tickIntervalMs, int safety, int rttSanityMaxMs, int maxRollbackTicks)
        {
            int safeRtt = avgRtt <= 0 ? 0 : Math.Min(avgRtt, rttSanityMaxMs);
            bool fallback = (safeRtt == 0);
            int rttTicks = fallback ? 0 : (safeRtt + tickIntervalMs - 1) / tickIntervalMs;
            int raw = fallback ? safety : rttTicks + safety;
            int clampMax = maxRollbackTicks / 2;
            int extraDelay = Math.Clamp(raw, 0, clampMax);
            bool clamped = raw > clampMax;
            return (extraDelay, fallback, rttTicks, raw, clamped);
        }
    }
}
