using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    // Tag component — no fields. On Mono, an empty struct with [StructLayout(Sequential)] yields Unsafe.SizeOf<T>()=0,
    // causing a DivideByZero in ComponentStorageFlat's MemoryMarshal.Cast.
    // Size=1 forces a 1-byte managed size (GetSerializedSize remains 0 since there are no serialized fields).
    [KlothoComponent(3)]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 1)]
    public partial struct ErrorCorrectionTargetComponent : IComponent
    {
    }
}
