// BouncyCastle Ed25519 backend — an independent-implementation differential oracle for the Atropos
// backend (PureEd25519Backend). It lives in this .NET test project, so the runtime assemblies carry no
// BouncyCastle dependency. Uses ONLY the low-level Org.BouncyCastle.Math.EC.Rfc8032.Ed25519 static API.
// Ed25519 is RFC 8032 deterministic, so it is byte-identical to any conformant implementation — the
// byte-identity that Ed25519InteropTests asserts between this backend and PureEd25519Backend.
using System;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>Ed25519 sign/verify via BouncyCastle — test differential oracle only (runtime uses
    /// <see cref="PureEd25519Backend"/>; the deterministic unit fake is <see cref="FakeEd25519Backend"/>).</summary>
    public sealed class BcEd25519Backend : IEd25519Backend
    {
        public byte[] Sign(byte[] privateKey, byte[] message)
        {
            if (privateKey == null || privateKey.Length != Ed25519.SecretKeySize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateKey));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var sig = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(privateKey, 0, message, 0, message.Length, sig, 0);
            return sig;
        }

        public bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != Ed25519.PublicKeySize) return false;
            if (signature == null || signature.Length != Ed25519.SignatureSize) return false;
            if (message == null) return false;
            return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }

        /// <summary>Derives the 32-byte public key from a 32-byte private seed (used for dev/lobby key setup).</summary>
        public static byte[] DerivePublicKey(byte[] privateSeed)
        {
            if (privateSeed == null || privateSeed.Length != Ed25519.SecretKeySize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateSeed));
            var pk = new byte[Ed25519.PublicKeySize];
            Ed25519.GeneratePublicKey(privateSeed, 0, pk, 0);
            return pk;
        }
    }
}
