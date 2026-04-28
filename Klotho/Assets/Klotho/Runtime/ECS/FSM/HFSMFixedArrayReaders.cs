using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.ECS.FSM
{
    internal static class HFSMFixedArrayReaders
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;

            ComponentStorageRegistry.RegisterFixedArrayReader(
                typeof(HFSMComponent), nameof(HFSMComponent.ActiveStateIds),
                boxed =>
                {
                    var comp = (HFSMComponent)boxed;
                    var buf = new int[HFSMComponent.MaxDepth];
                    unsafe { for (int i = 0; i < HFSMComponent.MaxDepth; i++) buf[i] = comp.ActiveStateIds[i]; }
                    return buf;
                });

            ComponentStorageRegistry.RegisterFixedArrayReader(
                typeof(HFSMComponent), nameof(HFSMComponent.PendingEventIds),
                boxed =>
                {
                    var comp = (HFSMComponent)boxed;
                    var buf = new int[HFSMComponent.MaxPendingEvents];
                    unsafe { for (int i = 0; i < HFSMComponent.MaxPendingEvents; i++) buf[i] = comp.PendingEventIds[i]; }
                    return buf;
                });
        }
    }
}
