namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Transport-neutral disconnect reason exposed via <see cref="INetworkTransport.OnDisconnected"/>.
    /// Concrete transport implementations map their underlying library reasons onto these categories.
    /// </summary>
    public enum DisconnectReason
    {
        /// <summary>Locally requested disconnect (Disconnect/DisconnectPeer called).</summary>
        LocalDisconnect,

        /// <summary>Remote host closed the connection gracefully.</summary>
        RemoteDisconnect,

        /// <summary>Connection attempt rejected by remote (e.g. wrong key, protocol mismatch).</summary>
        ConnectionRejected,

        /// <summary>Network-layer failure: timeout, host unreachable, or socket error.</summary>
        NetworkFailure,

        /// <summary>Reconnect requested by remote.</summary>
        ReconnectRequested,

        /// <summary>Other / unknown reason.</summary>
        Unknown,
    }
}
