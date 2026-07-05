using System;

using Xunit;

using xpTURN.Klotho.Samples.Identity; // BcEd25519Backend, PureEd25519Backend, DevLobbyTicketIssuer, LobbyTicket, LobbyTicketCodec

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// Atropos (<see cref="PureEd25519Backend"/>) ↔ BouncyCastle (<see cref="BcEd25519Backend"/>) differential
    /// across the exact <c>LobbyTicketCodec</c> wire bytes. Guards that switching the runtime backend to
    /// Atropos keeps signatures byte-identical and mutually verifiable — so previously issued tickets stay
    /// valid and the wire format is unchanged. (Crypto byte-identity is also proven upstream in the Atropos
    /// repo's BC differential tests; this pins it at Klotho's wire level.)
    /// </summary>
    public sealed class Ed25519InteropTests
    {
        private static readonly BcEd25519Backend Bc = new BcEd25519Backend();
        private static readonly PureEd25519Backend Pure = new PureEd25519Backend();

        // RFC 8032 deterministic vector (0x01..0x20 seed → known public key; matches P2pDevIdentity /
        // Brawler IdentityValidationTests). Doubles as a known-answer test for DerivePublicKey.
        private static readonly byte[] Seed =
        {
            0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,0x10,
            0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f,0x20,
        };
        private static readonly byte[] ExpectedPub =
        {
            0x79,0xb5,0x56,0x2e,0x8f,0xe6,0x54,0xf9,0x40,0x78,0xb1,0x12,0xe8,0xa9,0x8b,0xa7,
            0x90,0x1f,0x85,0x3a,0xe6,0x95,0xbe,0xd7,0xe0,0xe3,0x91,0x0b,0xad,0x04,0x96,0x64,
        };

        [Fact]
        public void DerivePublicKey_Bc_And_Pure_MatchKnownVector()
        {
            byte[] pkBc = BcEd25519Backend.DerivePublicKey(Seed);
            byte[] pkPure = PureEd25519Backend.DerivePublicKey(Seed);
            Assert.Equal(ExpectedPub, pkBc);
            Assert.Equal(ExpectedPub, pkPure);
            Assert.Equal(pkBc, pkPure);
        }

        [Fact]
        public void Sign_Bc_And_Pure_AreByteIdentical_AndCrossVerify()
        {
            byte[] pub = PureEd25519Backend.DerivePublicKey(Seed);
            var rng = new Random(0x5EED);
            for (int len = 0; len <= 512; len += (len < 32 ? 1 : 37))
            {
                var msg = new byte[len];
                rng.NextBytes(msg);

                byte[] sigBc = Bc.Sign(Seed, msg);
                byte[] sigPure = Pure.Sign(Seed, msg);

                Assert.Equal(sigBc, sigPure);              // Ed25519 is deterministic → byte-identical
                Assert.True(Pure.Verify(pub, msg, sigBc)); // BC-signed verifies under Atropos
                Assert.True(Bc.Verify(pub, msg, sigPure)); // Atropos-signed verifies under BC
            }
        }

        [Fact]
        public void Verify_RejectsTamperedMessage_OnBothBackends()
        {
            byte[] pub = PureEd25519Backend.DerivePublicKey(Seed);
            var msg = new byte[] { 1, 2, 3, 4, 5 };
            byte[] sig = Bc.Sign(Seed, msg);

            var tampered = (byte[])msg.Clone();
            tampered[0] ^= 0xFF;

            Assert.False(Pure.Verify(pub, tampered, sig));
            Assert.False(Bc.Verify(pub, tampered, sig));
        }

        [Fact]
        public void IssuedTicketWire_IsByteIdentical_AcrossBackends_AndCrossVerifies()
        {
            var ticket = new LobbyTicket("acc-1", "DisplayName", "match-42",
                                         1_700_000_000_000L, 1_700_000_060_000L, "nonce-xyz");

            string wireBc = new DevLobbyTicketIssuer(Bc, Seed).Issue(ticket);
            string wirePure = new DevLobbyTicketIssuer(Pure, Seed).Issue(ticket);
            Assert.Equal(wireBc, wirePure); // identical bytes go over the network regardless of backend

            // Verify the exact wire bytes with the opposite backend (verify-over-wire, no re-serialization).
            byte[] pub = PureEd25519Backend.DerivePublicKey(Seed);
            Assert.True(LobbyTicketCodec.TrySplitWire(wireBc, out string payloadSeg, out string sigSeg));
            byte[] payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
            byte[] sig = LobbyTicketCodec.Base64UrlDecode(sigSeg);
            Assert.True(Pure.Verify(pub, payload, sig));
            Assert.True(Bc.Verify(pub, payload, sig));
        }
    }
}
