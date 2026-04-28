using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Static registry of command types.
    /// Each command's generated code calls Register through [ModuleInitializer]
    /// to support automatic cross-assembly registration.
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly List<Action<CommandFactory>> _registrations = new List<Action<CommandFactory>>();

        public static bool Register<T>(int typeId) where T : CommandBase, new()
        {
            _registrations.Add(factory => factory.RegisterCommand<T>(typeId));
            return true;
        }

        internal static void ApplyTo(CommandFactory factory)
        {
            foreach (var reg in _registrations)
                reg(factory);
        }
    }
}
