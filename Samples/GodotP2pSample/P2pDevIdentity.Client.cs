// GodotP2pSample dev lobby-identity — CLIENT (public-only) side. Godot counterpart of the Unity
// P2pSample helper.
//
// The carry-only provider a joining peer presents + the host-side validator (public key). NO private key
// here: the public key is verify-only (cannot forge), so embedding it in clients is safe. The private
// signing seed and ticket minting live in the issuer partial (P2pDevIdentity.Server.cs), which must NEVER
// ship in a real client. Gated by DEBUG (Godot/.NET) so it drops out of release/export builds (then the
// sample runs in no-lobby mode). Crypto via BouncyCastle (NuGet on Godot); identity impl is the shared
// package source (Compile-Included by the .csproj).
//
// ── Identity trust model (why this is dev-only, and what production must do instead) ──
// The signed-ticket scheme presupposes an ISSUER that holds the private signing key. P2P removes the
// authoritative *game session* server — NOT the *identity issuance* authority. Therefore:
//   • Public key in the client: OK and REQUIRED. Any peer may become the host/validator and must verify
//     incoming tickets offline; a public key can only verify, never forge — so embedding it is safe.
//   • Private key in the client: FORBIDDEN. Whoever holds it can mint a ticket for any account → full
//     impersonation. The seed in the issuer partial exists ONLY to fake the absent issuer in dev.
// When "P2P has no backend", who holds the key? — the signed-ticket model needs a trusted issuer, so:
//   • Delegate to a platform identity provider (Steam/Epic/Game Center/PSN) — it IS the issuer; the client
//     carries only the platform-signed ticket + verify material. Usual first choice for serverless P2P.
//   • Or run a thin own lobby that ISSUES only — P2P verification stays offline at the host, so the lobby is
//     off the gameplay path and far thinner than the SD redeem lobby (issue-time, once per join).
//   • If there is genuinely no trusted issuer at all, the signed-ticket model does not apply: fall back to
//     self-asserted identity (no cryptographic guarantee) or this no-lobby mode. Embedding a signing key in
//     the client is never an acceptable substitute for an issuer.
#if DEBUG
using System;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Samples.Identity;

namespace xpTURN.Samples.P2pSample
{
    internal static partial class P2pDevIdentity
    {
        // Dev lobby Ed25519 PUBLIC key (verify key) — derived from the issuer's private seed. Safe to embed
        // in clients (verify-only). seed 0x01..0x20 → this key (RFC 8032 deterministic).
        private static readonly byte[] PublicKey =
        {
            0x79, 0xb5, 0x56, 0x2e, 0x8f, 0xe6, 0x54, 0xf9, 0x40, 0x78, 0xb1, 0x12, 0xe8, 0xa9, 0x8b, 0xa7,
            0x90, 0x1f, 0x85, 0x3a, 0xe6, 0x95, 0xbe, 0xd7, 0xe0, 0xe3, 0x91, 0x0b, 0xad, 0x04, 0x96, 0x64,
        };

        // Lobby match id all peers' tickets share — build-time constant. Shared with the issuer partial.
        private const string DevSessionId = "p2psample-dev-match";

        /// <summary>Obtains a dev ticket from the issuer stand-in and wraps it in a carry-only provider.
        /// In production the ticket arrives out-of-band from a real lobby and the issuer partial is absent.</summary>
        public static IPlayerIdentityProvider CreateProvider()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 6); // distinct per process instance
            string ticket = IssueDevTicket("dev-" + suffix, "Player-" + suffix, now);
            return new DevIdentityProvider(ticket);
        }

        /// <summary>Host-side validator with the dev public key + expected session id.</summary>
        public static IPlayerIdentityValidator CreateValidator()
            => new P2pEd25519IdentityValidator(
                PublicKey, DevSessionId,
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Ed25519Backends.Default);
    }
}
#endif
