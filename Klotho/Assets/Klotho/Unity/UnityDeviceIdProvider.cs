using UnityEngine;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Unity
{
    public class UnityDeviceIdProvider : IDeviceIdProvider
    {
        public string GetDeviceId() => SystemInfo.deviceUniqueIdentifier;
    }
}
