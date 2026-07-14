using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// SD client → server (ReliableOrdered): the client's desync diagnosis, so the conclusion reaches
    /// the server's logs instead of dying in a client-side log the operator never sees.
    ///
    /// <para>Diagnostic-only, and shaped so it cannot be anything else: integers only (no strings —
    /// type and system NAMES are resolved from the server's own registry, so a client cannot inject
    /// text into server logs), fixed length, and the server consumes it into a log line without
    /// storing it in any recovery-ladder state. Same idiom as
    /// <see cref="ResyncFailureReportMessage"/>, minus the ladder side effects.</para>
    ///
    /// <para>P2P does not send this: peers are symmetric and mutually untrusted there, and there is no
    /// authority-side log for the verdict to be useful in.</para>
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.DesyncVerdictReport)]
    public partial class DesyncVerdictReportMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int DivergedTick;

        /// <summary>Diagnostics.DesyncClass. Unknown never travels here — an SD client always has both digests.</summary>
        [KlothoOrder]
        public byte Class;

        /// <summary>Diagnostics.DesyncLayer. None when the layer probe failed — class and tick still ship.</summary>
        [KlothoOrder]
        public byte Layer;

        /// <summary>Component typeId (Layer=Component) / snapshot-participant index (Layer=System) / -1 (Layer=None).</summary>
        [KlothoOrder]
        public int TypeIdOrParticipantIdx;

        [KlothoOrder]
        public long LocalHash;

        [KlothoOrder]
        public long RemoteHash;
    }
}
