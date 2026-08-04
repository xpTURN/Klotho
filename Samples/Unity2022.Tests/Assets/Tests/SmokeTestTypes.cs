using System.Runtime.InteropServices;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Unity2022.Tests
{
    // Declaration-only file: types the smoke fixtures share. Every member these types expose is
    // emitted by KlothoGenerator (shipped as Plugins/Analyzers/KlothoGenerator.dll with the
    // RoslynAnalyzer label), so this file compiling at all is the compatibility signal — Unity 2022.3
    // must load a generator built against the Roslyn version its C# compiler ships.
    //
    // typeIds live in the 93xx block, kept clear of the package (1..200) and of the other test
    // suites (90xx/92xx in Brawler + Klotho.Runtime.Tests) so no id collides in a shared domain.

    /// <summary>ECS component probe. Field order/layout is mirrored by the golden dump in the fixed-point fixture.</summary>
    [KlothoComponent(9301)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SmokeProbeComponent : IComponent
    {
        public int Counter;
        public bool Flag;
        public FP64 Value;
        public FPVector3 Position;
    }

    /// <summary>Reusable inline struct codec ([KlothoSerializableStruct]) with a fixed buffer — exercises the unsafe emit path.</summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe partial struct SmokeBundle
    {
        public int RootId;
        public fixed int Slots[4];
        public FP64 Margin;
    }

    /// <summary>Component embedding the bundle — verifies nested codec delegation is generated.</summary>
    [KlothoComponent(9302)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SmokeBundleComponent : IComponent
    {
        public SmokeBundle Bundle;
    }

    /// <summary>Singleton component — a distinct storage-layout path (one value slot, full lookup table).</summary>
    [KlothoComponent(9303)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SmokeSingletonComponent : IComponent
    {
        public int Value;
    }

    /// <summary>DataAsset probe — generated binary codec plus the Newtonsoft contract resolver path.</summary>
    [KlothoDataAsset(9304, AssetId = 9304, Key = "SmokeAsset")]
    public partial class SmokeAsset : IDataAsset
    {
        [KlothoOrder(0)] public FP64 Speed = FP64.FromInt(3);
        [KlothoOrder(1)] public int Cost = 7;
        [KlothoOrder(2)] public FPVector3 Offset = new FPVector3(1, 2, 3);
    }
}
