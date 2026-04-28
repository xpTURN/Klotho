using System;

namespace xpTURN.Klotho.ECS
{
    // Attribute that marks target types/methods so they are not stripped by IL2CPP managed stripping.
    // Since the Runtime asmdef has noEngineReferences=true, the default PreserveAttribute cannot be used,
    // so we redefine it locally with the same name.
    // Stripping only inspects the simple name "Preserve", so a local redefinition behaves identically.
    // Primary targets are the StorageRegistrar class and [ModuleInitializer] method emitted by the generator,
    // along with the registration path including ComponentReflector<T>.
    [AttributeUsage(AttributeTargets.All)]
    public sealed class PreserveAttribute : Attribute { }
}
