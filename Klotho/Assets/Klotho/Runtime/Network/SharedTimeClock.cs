using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Shared clock synchronized via handshake.
    /// SharedNow = localTime - clockOffset - sharedEpoch
    /// </summary>
    public readonly struct SharedTimeClock
    {
        private readonly long _sharedEpoch;  // UTC millisecond reference recorded on handshake completion
        private readonly long _clockOffset;  // difference between local clock and Host clock (ms). Host is 0

        public long SharedEpoch => _sharedEpoch;
        public long ClockOffset => _clockOffset;
        public bool IsValid => _sharedEpoch != 0;

        /// <summary>
        /// Current shared time (milliseconds).
        /// </summary>
        public long SharedNow =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _clockOffset - _sharedEpoch;

        public SharedTimeClock(long sharedEpoch, long clockOffset)
        {
            _sharedEpoch = sharedEpoch;
            _clockOffset = clockOffset;
        }
    }
}
