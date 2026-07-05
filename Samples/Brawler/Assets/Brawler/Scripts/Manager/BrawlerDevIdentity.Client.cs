// Brawler dev lobby-identity — CLIENT (public-only) side. Mirrors P2pSample's P2pDevIdentity.Client.cs.
//
// The carry-only provider a joining peer presents + the host-side validator (public key). NO private key
// here: the public key is verify-only (cannot forge), so embedding it in clients is safe. The private
// signing seed and ticket minting live in the issuer partial (BrawlerDevIdentity.Server.cs).
//
// Activation is RUNTIME, config-driven (BrawlerLobbySettings._lobbyEnabled) — NOT a compile define — so one
// build flips lobby on/off without recompiling. ⚠ Consequently the dev signing seed (Server partial)
// compiles into every build; acceptable for a SAMPLE only. A real game MUST replace BrawlerDevIdentity with
// a real lobby (client carries an out-of-band ticket; no embedded seed). See P2pSample/.../P2pDevIdentity.Client.cs
// for the full trust-model rationale.
//
// Scope: P2P only. The SD path uses SdRef's SdDevIdentity (public key + redeem) on the dedicated server.
using System;
using xpTURN.Klotho.Logging;          // IKLogger / KInformation
using xpTURN.Klotho.Network;          // IPlayerIdentityProvider / IPlayerIdentityValidator
using xpTURN.Klotho.Samples.Identity; // p2pref: DevIdentityProvider, P2pEd25519IdentityValidator, BcEd25519Backend

namespace Brawler
{
    internal static partial class BrawlerDevIdentity
    {
        // Dev lobby Ed25519 PUBLIC key (verify key) — the shared dev key (seed 0x01..0x20, RFC 8032
        // deterministic). Safe to embed (verify-only). Keep identical to P2pSample / SdDevIdentity / Godot.
        private static readonly byte[] PublicKey =
        {
            0x79, 0xb5, 0x56, 0x2e, 0x8f, 0xe6, 0x54, 0xf9, 0x40, 0x78, 0xb1, 0x12, 0xe8, 0xa9, 0x8b, 0xa7,
            0x90, 0x1f, 0x85, 0x3a, 0xe6, 0x95, 0xbe, 0xd7, 0xe0, 0xe3, 0x91, 0x0b, 0xad, 0x04, 0x96, 0x64,
        };

        // P2P session id all peers' tickets share — a build-time constant the host validator checks against.
        // Distinct from the SD _matchId config (SdDevIdentity.DevMatchId): P2P verification is offline at the
        // host, so the session binding is a fixed dev constant rather than a lobby room assignment.
        private const string DevSessionId = "brawler-dev-match";

        /// <summary>Obtains a dev ticket from the issuer stand-in and wraps it in a carry-only provider.
        /// The display name is carried so the lobby-on roster shows the same nickname the no-lobby path uses.
        /// In production the ticket arrives out-of-band from a real lobby and the issuer partial is absent.</summary>
        public static IPlayerIdentityProvider CreateProvider(string account, string displayName, IKLogger logger = null)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string acct = string.IsNullOrEmpty(account)
                ? "dev-" + Guid.NewGuid().ToString("N").Substring(0, 6) // distinct per process instance
                : account;
            string name = string.IsNullOrEmpty(displayName) ? acct : displayName;
            logger?.KInformation($"[Brawler] Join identity: account='{acct}', displayName='{name}'");
            string ticket = IssueDevTicket(acct, name, now);
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
