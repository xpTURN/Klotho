// Production Ed25519 backend over the Atropos package (xpTURN.Atropos — dependency-free, Zero-GC pure C#
// Ed25519, RFC 8032, plain PureEdDSA). Atropos is byte-identical to BouncyCastle (verified by Atropos's own
// BC differential tests), so this interoperates byte-identically with any conformant implementation and with
// previously issued tickets — the LobbyTicketCodec wire format is unchanged.
//
// The IEd25519Backend seam is byte[]-based; Atropos exposes Span<byte>, so this adapter is a byte[]<->Span
// wrapper. Atropos's zero-allocation guarantee applies to its direct Span API; through this byte[] seam the
// only per-call heap allocation is the returned signature (Sign) or public-key (DerivePublicKey) array.
using System;
using xpTURN.Atropos;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>Real Ed25519 sign/verify via the Atropos package — the production runtime backend
    /// (dependency-free, Zero-GC, non-malleable, RFC 8032 byte-identical across implementations).</summary>
    public sealed class PureEd25519Backend : IEd25519Backend
    {
        public byte[] Sign(byte[] privateKey, byte[] message)
        {
            if (privateKey == null || privateKey.Length != Ed25519.SeedSize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateKey));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var sig = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(privateKey, message, sig);
            return sig;
        }

        public bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != Ed25519.PublicKeySize) return false;
            if (signature == null || signature.Length != Ed25519.SignatureSize) return false;
            if (message == null) return false;
            return Ed25519.Verify(publicKey, message, signature);
        }

        /// <summary>Derives the 32-byte public key from a 32-byte private seed (used for dev/lobby key setup).</summary>
        public static byte[] DerivePublicKey(byte[] privateSeed)
        {
            if (privateSeed == null || privateSeed.Length != Ed25519.SeedSize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateSeed));
            var pk = new byte[Ed25519.PublicKeySize];
            Ed25519.DerivePublicKey(privateSeed, pk);
            return pk;
        }
    }
}
