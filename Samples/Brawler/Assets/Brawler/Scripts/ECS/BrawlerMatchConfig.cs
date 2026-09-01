using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;           // FixedString64
using xpTURN.Klotho.Serialization; // [KlothoSerializableStruct], SpanWriter, SpanReader

namespace Brawler
{
    /// <summary>
    /// Per-match dynamic config payload for the Brawler sample, carried opaquely in
    /// <c>SimulationConfig.MatchConfigData</c> (byte[]) and propagated to every peer at match start. The
    /// source generator emits <c>Serialize</c>/<c>Deserialize</c>/<c>GetSerializedSize</c> (no hand-written
    /// codec), so the authority (SD server / P2P host) and the consumers share one format. Distinct from
    /// <see cref="BrawlerPlayerConfig"/> (per-player, message-embedded): this is per-match, (de)serialized at
    /// the byte[] boundary via <see cref="BrawlerMatchConfig"/> — mirroring the DemoEntitlement pattern.
    /// <para>
    /// <c>[KlothoSerializableStruct]</c> requires the type be <c>partial</c>, <c>unmanaged</c>, and laid out
    /// <c>[StructLayout(Sequential, Pack = 4)]</c>. Add fixed-width fields here as more match knobs are needed.
    /// </para>
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BrawlerMatchConfigData
    {
        public int BotCount;

        /// <summary>
        /// Room capacity the AUTHORITY built the tick-0 world with. Bot ids are numbered past it
        /// (<c>maxPlayers + 1 + i</c>) and their spawn slots follow from that id, so it is a tick-0 state
        /// input — not a UI hint. It travels here rather than being read from each peer's own SessionConfig
        /// for the same reason SeedLoadouts keys off the participant slots: a non-authority peer's local
        /// value can be a guess, and a replay verifier has no SessionConfig at all.
        /// <para><c>0</c> means "not stamped" (an older blob, or an issuer that does not know the capacity —
        /// the dev lobby): consumers fall back to their local value, which is the pre-existing behaviour.
        /// A replay whose blob says 0 cannot have its tick 0 rebuilt.</para>
        /// </summary>
        public int MaxPlayers;

        /// <summary>
        /// The lobby's key for THIS match instance (<c>{matchId}#{token}</c>), or empty when no lobby issued
        /// one (a P2P host or a solo session authors its own config).
        ///
        /// <para><b>Why it is in the match config and not in the replay format.</b> A replay's own
        /// <c>SessionId</c> is a local <c>Guid.NewGuid()</c> — every peer's recording gets a different one, so
        /// the service has never seen it. This is the only value that joins an uploaded replay to the lobby's
        /// record of the match it came from, and riding the config payload is what gets it onto EVERY peer
        /// (and into every peer's replay metadata) with no format change.</para>
        ///
        /// <para><b>62 bytes, and truncation is the issuer's problem.</b> <see cref="FixedString64"/> silently
        /// cuts at 62 UTF-8 bytes, and two ids cut to the same value would merge two different matches. The
        /// lobby refuses to issue an id that does not fit rather than stamping a truncated one.</para>
        /// </summary>
        public FixedString64 MatchInstanceId;
    }

    /// <summary>
    /// byte[]↔<see cref="BrawlerMatchConfigData"/> boundary (mirrors the DemoEntitlement facade). null / empty
    /// / malformed → default (BotCount = 0 = no bots = the pre-existing default), so an unset MatchConfigData
    /// is a no-op — a client with no propagated config falls back cleanly.
    /// </summary>
    public static class BrawlerMatchConfig
    {
        /// <summary>
        /// Byte length <see cref="Encode"/> always produces. The struct is fixed-width — two int32 plus an
        /// inline <see cref="FixedString64"/> — so this does not vary with what the payload carries, and a
        /// shorter blob is therefore an OLDER layout rather than a smaller record of this one.
        ///
        /// <para>Read it that way and nothing else: it is the only signal that separates "recorded before a
        /// field existed" from "this layout, values left at their defaults". Decoding cannot tell them apart,
        /// because <see cref="Decode"/> is lenient and an older blob comes back as <c>default</c> — exactly
        /// what a current payload with no bots, no stamped capacity and no identity also decodes to.</para>
        ///
        /// <para>Computed from the struct rather than written as a literal so a new field carries it along.
        /// The suite keeps its own literal on purpose: two independent statements of the size is what makes
        /// a layout change visible instead of silently agreeing with itself.</para>
        /// </summary>
        public static readonly int EncodedSize = default(BrawlerMatchConfigData).GetSerializedSize();

        /// <summary>Serialize the payload struct to the opaque byte[] the core carries (codegen, no hand-rolling).</summary>
        public static byte[] Encode(in BrawlerMatchConfigData d)
        {
            var buf = new byte[d.GetSerializedSize()];
            var w = new SpanWriter(buf);
            d.Serialize(ref w);
            return buf;
        }

        /// <summary>Deserialize the opaque byte[] to the payload struct; null / empty / malformed → default.</summary>
        public static BrawlerMatchConfigData Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return default;
            try
            {
                var d = new BrawlerMatchConfigData();
                var r = new SpanReader(data, 0, data.Length);
                d.Deserialize(ref r);
                return d;
            }
            catch
            {
                return default; // lenient: a corrupt blob → default (no bots)
            }
        }
    }
}
