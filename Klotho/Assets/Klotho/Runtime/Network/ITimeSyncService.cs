using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Frame-advantage-based clock synchronization.
    /// </summary>
    public interface ITimeSyncService
    {
        /// <summary>
        /// Called every tick — updates the frame advantage history
        /// </summary>
        /// <param name="localAdvantage">Local frame advantage (localTick - remoteTick)</param>
        /// <param name="remoteAdvantage">Remote frame advantage (value reported by the peer)</param>
        void AdvanceFrame(float localAdvantage, float remoteAdvantage);

        /// <summary>
        /// Query the number of frames to wait before advancing the tick
        /// </summary>
        /// <param name="requireIdleInput">If true, only wait when input is idle</param>
        /// <returns>Number of frames to wait (0 = advance immediately)</returns>
        int RecommendWaitFrames(bool requireIdleInput);

        /// <summary>
        /// Record whether the most recent input is idle
        /// </summary>
        void RecordInput(bool isIdle);

        /// <summary>
        /// Current local frame advantage mean
        /// </summary>
        float LocalAdvantageMean { get; }

        /// <summary>
        /// Current remote frame advantage mean
        /// </summary>
        float RemoteAdvantageMean { get; }

        /// <summary>
        /// Reset
        /// </summary>
        void Reset();
    }
}
