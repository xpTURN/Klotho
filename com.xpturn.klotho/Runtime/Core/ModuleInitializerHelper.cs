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
#pragma warning disable UAC0005 // Unity's Mono editor returns the same list as CurrentAssemblies.GetLoadedAssemblies(), which this engine-free assembly cannot call. Revisit on a CoreCLR editor.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
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
