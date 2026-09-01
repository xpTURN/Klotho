using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;            // FixedString32
using xpTURN.Klotho.Serialization;  // [KlothoSerializableStruct], SpanWriter, SpanReader

namespace Brawler
{
    /// <summary>
    /// What the client SAYS happened, written into the replay's <c>GameCustomData</c> when recording ends.
    ///
    /// <para>The verifier re-simulates the recorded inputs to derive the authoritative result, then compares
    /// it with this. Without a claim there is nothing to compare against — a re-simulation always agrees with
    /// itself — so the runner treats a claimless replay as UNVERIFIABLE rather than as a pass. That is the
    /// whole reason this type exists.</para>
    ///
    /// <para>This is a game type on purpose. "What counts as the result" differs per game, so the core keeps
    /// an opaque byte[] slot and stays out of it.</para>
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BrawlerReplayClaimData
    {
        /// <summary>Identifies the blob as a claim. GameCustomData is a free-form game slot, so decoding
        /// without a tag would silently accept whatever else a build might have put there.</summary>
        public uint Magic;

        /// <summary>Claim layout version. Bumped when fields change; a reader rejects anything else.</summary>
        public int Version;

        /// <summary>Winning PlayerId, -1 for draw / no winner (same shape as IMatchEndEvent).</summary>
        public int WinnerPlayerId;

        /// <summary>Tick the match ended on.</summary>
        public int EndTick;

        /// <summary>Why the MATCH ended ("stocks", "timeout") — not to be confused with
        /// <c>ReplayEndReason</c>, which says why the RECORDING ended.</summary>
        public FixedString32 MatchEndReason;
    }

    /// <summary>
    /// byte[] ↔ <see cref="BrawlerReplayClaimData"/> boundary.
    ///
    /// <para>Unlike <see cref="BrawlerMatchConfig"/>, decoding is NOT lenient. A missing or malformed claim
    /// must be distinguishable from a valid one: if "no claim" collapsed into "default claim", an attacker
    /// would only have to strip the blob to make every replay verify.</para>
    /// </summary>
    public static class BrawlerReplayClaim
    {
        /// <summary>"BRCL" — the claim tag.</summary>
        public const uint ClaimMagic = 0x4252434CU;

        /// <summary>Current claim layout.</summary>
        public const int CurrentVersion = 1;

        public static byte[] Encode(int winnerPlayerId, int endTick, FixedString32 matchEndReason)
        {
            var d = new BrawlerReplayClaimData
            {
                Magic = ClaimMagic,
                Version = CurrentVersion,
                WinnerPlayerId = winnerPlayerId,
                EndTick = endTick,
                MatchEndReason = matchEndReason,
            };
            var buf = new byte[d.GetSerializedSize()];
            var w = new SpanWriter(buf);
            d.Serialize(ref w);
            return buf;
        }

        /// <summary>
        /// Returns false for absent, foreign, or malformed data — the caller must treat that as
        /// "unverifiable", never as "verified".
        /// </summary>
        public static bool TryDecode(byte[] data, out BrawlerReplayClaimData claim)
        {
            claim = default;
            if (data == null || data.Length == 0) return false;
            try
            {
                var d = new BrawlerReplayClaimData();
                var r = new SpanReader(data, 0, data.Length);
                d.Deserialize(ref r);
                if (d.Magic != ClaimMagic || d.Version != CurrentVersion) return false;
                claim = d;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
