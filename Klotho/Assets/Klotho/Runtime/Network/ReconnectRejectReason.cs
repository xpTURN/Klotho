namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Symbolic names for ReconnectRejectMessage.Reason byte values.
    /// Keep in sync with the reason switch in
    /// KlothoNetworkService.HandleReconnectReject / ServerDrivenClientService.HandleReconnectReject.
    /// </summary>
    public static class ReconnectRejectReason
    {
        public const byte InvalidMagic     = 1;
        public const byte InvalidPlayer    = 2;
        public const byte TimedOut         = 3;
        public const byte AlreadyConnected = 4;

        /// <summary>
        /// Returns the symbolic name for a reason byte (e.g. "InvalidMagic"). Game/UI layers should
        /// localize these via their own string table; the values here are stable identifiers.
        /// </summary>
        public static string ToName(byte reason)
        {
            switch (reason)
            {
                case InvalidMagic:     return "InvalidMagic";
                case InvalidPlayer:    return "InvalidPlayer";
                case TimedOut:         return "TimedOut";
                case AlreadyConnected: return "AlreadyConnected";
                default:               return "Unknown";
            }
        }

        /// <summary>
        /// Whether this reason indicates the same PlayerId is already held by another peer/device
        /// — game layer should offer a user choice (fall back to fresh join, or quit).
        /// </summary>
        public static bool RequiresUserChoice(byte reason)
        {
            return reason == AlreadyConnected;
        }
    }
}
