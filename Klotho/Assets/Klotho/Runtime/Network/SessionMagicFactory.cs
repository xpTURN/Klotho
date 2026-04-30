using System;
using System.Security.Cryptography;

namespace xpTURN.Klotho.Network
{
    internal static class SessionMagicFactory
    {
        public static long Generate()
        {
            Span<byte> buffer = stackalloc byte[8];
            long magic;
            do
            {
                RandomNumberGenerator.Fill(buffer);
                magic = BitConverter.ToInt64(buffer);
            }
            while (magic == 0L);
            return magic;
        }
    }
}
