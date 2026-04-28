namespace xpTURN.Klotho.ECS
{
    // Per-type reflector for editor debugging. Since Frame does not own per-type storage instances,
    // the reflector takes heap and layout directly as arguments to expose the entity's component.
    public interface IStorageReflector
    {
        bool   Has(byte[] heap, in StorageLayout layout, int entityIndex);
        object GetBoxed(byte[] heap, in StorageLayout layout, int entityIndex);
    }

    // Return type of Frame.TryGetReflectableStorage. Bundles heap + layout + reflector together
    // so consumers can query the entity's component using only Has(idx) / GetBoxed(idx).
    public readonly struct ReflectableView
    {
        private readonly byte[] _heap;
        private readonly StorageLayout _layout;
        private readonly IStorageReflector _reflector;

        internal ReflectableView(byte[] heap, in StorageLayout layout, IStorageReflector reflector)
        {
            _heap = heap;
            _layout = layout;
            _reflector = reflector;
        }

        public bool   Has(int entityIndex)      => _reflector.Has(_heap, in _layout, entityIndex);
        public object GetBoxed(int entityIndex) => _reflector.GetBoxed(_heap, in _layout, entityIndex);
    }

    // Per-type reflector implementation. The Registry holds one instance per type.
    // [Preserve] prevents the generic instantiation from being stripped out by IL2CPP managed stripping.
    [Preserve]
    internal sealed class ComponentReflector<T> : IStorageReflector where T : unmanaged, IComponent
    {
        public bool Has(byte[] heap, in StorageLayout layout, int idx)
        {
            var storage = new ComponentStorageFlat<T>(heap, in layout);
            return storage.Has(idx);
        }

        public object GetBoxed(byte[] heap, in StorageLayout layout, int idx)
        {
            var storage = new ComponentStorageFlat<T>(heap, in layout);
            return storage.Get(idx);   // T → object boxing (editor-only, unavoidable)
        }
    }
}
