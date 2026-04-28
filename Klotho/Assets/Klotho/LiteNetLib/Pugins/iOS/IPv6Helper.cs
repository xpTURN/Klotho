using System.Net;
using System.Runtime.InteropServices;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// IPv4→IPv6 address conversion utility for iOS NAT64 environments.
    /// </summary>
    public static class IPv6Helper
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern string _ResolveIPv6Address(string ipv4);
#endif

        /// <summary>
        /// In a NAT64 environment, convert an IPv4 address to IPv6.
        /// In normal environments, return the original IPv4 unchanged.
        /// </summary>
        public static string Resolve(string ipv4)
        {
#if UNITY_IOS && !UNITY_EDITOR
            return _ResolveIPv6Address(ipv4);
#else
            return ipv4;
#endif
        }

        public static bool IsIPv6(string ip)
        {
            return IPAddress.TryParse(ip, out var addr) &&
                addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }
    }
}