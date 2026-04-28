namespace xpTURN.Klotho.Network
{
    public enum InboundEventType : byte
    {
        Data,
        Connected,
        Disconnected
    }

    public struct InboundEntry
    {
        public int PeerId;
        public InboundEventType Type;
        /// <summary>
        /// Valid only when Type is Data. Buffer rented from StreamPool.
        /// Must be returned via StreamPool.ReturnBuffer after DrainInboundQueue consumes it.
        /// </summary>
        public byte[] Buffer;
        public int Length;
    }
}
