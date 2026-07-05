// P2pSample dev lobby-identity — ISSUER (lobby/server) side. MUST NOT SHIP in a real client.
//
// Holds the PRIVATE Ed25519 signing seed and mints signed tickets — the lobby's job. In production this
// whole file is replaced by a real lobby service that issues tickets out-of-band; the client then carries
// only the issued ticket + the public verify key. See P2pDevIdentity.Client.cs for the full trust-model
// rationale. Kept on the same build gate as the client partial here for sample simplicity; a stricter
// build can narrow this file's gate further (or exclude it outright) without touching the client side.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using xpTURN.Klotho.Samples.Identity;

namespace xpTURN.Samples.P2pSample
{
    internal static partial class P2pDevIdentity
    {
        // Dev lobby Ed25519 PRIVATE seed (issuer only) — NEVER ship this. seed 0x01..0x20 → the public key
        // in P2pDevIdentity.Client.cs (RFC 8032 deterministic).
        // Key generation/rotation guide: Docs/Samples/DevIdentityKeys.md (changing the seed requires updating Client.cs:PublicKey too; keep identical to the Godot side).
        private static readonly byte[] Seed =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20,
        };

        private const long TicketValidityMs = 5 * 60 * 1000; // short expiry; a fresh ticket is minted each run

        /// <summary>Mints a signed dev ticket (lobby stand-in). In production this is the lobby's signing
        /// endpoint, reached over the network; the client never holds the seed or runs this.</summary>
        private static string IssueDevTicket(string account, string displayName, long nowMs)
        {
            var issuer = new DevLobbyTicketIssuer(Ed25519Backends.Default, Seed);
            var ticket = new LobbyTicket(
                account: account,
                displayName: displayName,
                sessionId: DevSessionId,
                issuedAt: nowMs,
                expiresAt: nowMs + TicketValidityMs,
                nonce: Guid.NewGuid().ToString("N"));
            return issuer.Issue(ticket);
        }
    }
}
#endif
