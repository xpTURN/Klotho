using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Static registry of message types.
    /// Each message's generated code calls Register via [ModuleInitializer] to support
    /// automatic cross-assembly registration.
    /// </summary>
    public static class MessageRegistry
    {
        private static readonly List<Action<MessageSerializer>> _registrations
            = new List<Action<MessageSerializer>>();

        public static bool Register<T>(NetworkMessageType type) where T : INetworkMessage, new()
        {
            _registrations.Add(serializer => serializer.RegisterMessageType<T>(type));
            return true;
        }

        internal static void ApplyTo(MessageSerializer serializer)
        {
            foreach (var reg in _registrations)
                reg(serializer);
        }
    }
}
