using System;
using System.Runtime.CompilerServices;

namespace xpTURN.Klotho.Core
{
    internal static class ModuleInitializerHelper
    {
        private static readonly object _lock = new object();
        private static volatile bool _done;

        public static void EnsureAll()
        {
            if (_done) return;
            lock (_lock)
            {
                if (_done) return;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);
                    }
                    catch
                    {
                        // Ignore system assemblies and similar
                    }
                }
                _done = true;
            }
        }
    }
}
