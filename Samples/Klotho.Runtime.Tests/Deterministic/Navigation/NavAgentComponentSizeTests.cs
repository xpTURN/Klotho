using System.Runtime.CompilerServices;
using NUnit.Framework;

using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The two size baselines for <c>NavAgentComponent</c>: what it occupies in memory, and what it
    /// occupies on the wire.
    ///
    /// <para><b>Why the wire half exists at all.</b> Engine components had no wire-size pin —
    /// every <c>GetSerializedSize</c> assertion in this suite was about the navmesh serializer, and
    /// none about a component. That absence is dangerous in a specific way:
    /// <c>ComponentStorageRegistry</c> reads a component's wire size WITHOUT comparing it to what
    /// the local build expects, so two peers built from different sources do not skip a mismatched
    /// block — they MISREAD it, and every type after it in the stream is corrupted in turn. The
    /// generated size is a pure function of the field list, so a pin here is the cheapest place to
    /// see that a field addition just broke replay compatibility.</para>
    ///
    /// <para><b>Why the in-memory half exists on CoreCLR.</b> The cross-runtime size matrix
    /// (<c>SizeOfCrossRuntimeTests</c>) is Unity PlayMode only, so a layout change is invisible to
    /// every dotnet run — including the server's. Same reasoning, and the same idiom, as
    /// <see cref="FPNavMeshTriangleSizeTests"/>. With this pin in place, PlayMode is left with the
    /// job it is uniquely good for: confirming the IL2CPP and Mono values.</para>
    /// </summary>
    [TestFixture]
    public class NavAgentComponentSizeTests
    {
        // Pack = 4, sequential. 8 bytes of the total are padding, and they are located:
        //   3 after `bool PathIsValid` / 2 after `HasNavDestination`+`HasPath` / 3 after `byte Status`
        // An `int` field therefore costs a full 4 bytes wherever it is placed — none of those holes
        // can hold one. A `ushort` placed in the 2-byte hole would cost 0, which is why the width
        // question was measured rather than assumed before the two fields were widened to int.
        //
        // 708 -> 716 when PlanAreaMaskOverride and WalkAreaMaskOverride were added: two ints, +4
        // each, and the padding total did not move (the third test below is what pins that).
        private const int ExpectedInMemorySize = 716;

        // Generated: `public int GetSerializedSize() => 708;`. Smaller than the in-memory size by
        // exactly the padding, because the writer emits fields and not holes. 700 -> 708 with the
        // same two fields, which is why ReplayMetadata.CURRENT_VERSION had to move with them.
        private const int ExpectedWireSize = 708;

        [Test]
        public void NavAgentComponent_InMemorySizeIsStable_CoreClr()
        {
            int actual = Unsafe.SizeOf<NavAgentComponent>();
            TestContext.Out.WriteLine($"Unsafe.SizeOf<NavAgentComponent>() = {actual} (CoreCLR)");
            Assert.AreEqual(ExpectedInMemorySize, actual,
                "the NavAgentComponent layout changed. If that was intended, update "
                + "ExpectedInMemorySize here AND re-run the Unity PlayMode size matrix "
                + "(SizeOfCrossRuntimeTests), which pins the Mono and IL2CPP values this test "
                + "cannot see.");
        }

        [Test]
        public void NavAgentComponent_WireSizeIsStable()
        {
            var nav = default(NavAgentComponent);
            int actual = ((IComponent)nav).GetSerializedSize();
            TestContext.Out.WriteLine($"NavAgentComponent.GetSerializedSize() = {actual}");
            Assert.AreEqual(ExpectedWireSize, actual,
                "the NavAgentComponent wire size changed, so this build cannot read a snapshot or "
                + "replay written by the previous one — and ComponentStorageRegistry will misread "
                + "rather than refuse it. If the change was intended, bump "
                + "ReplayMetadata.CURRENT_VERSION and update ExpectedWireSize.");
        }

        /// <summary>
        /// The wire size must stay SMALLER than the in-memory size, by exactly the padding. If the
        /// two ever meet, either the struct lost its holes or the writer started emitting them —
        /// and the second one would put uninitialised bytes into the determinism stream.
        /// </summary>
        [Test]
        public void WireSize_IsTheInMemorySize_MinusPadding()
        {
            var nav = default(NavAgentComponent);
            int wire = ((IComponent)nav).GetSerializedSize();
            int mem = Unsafe.SizeOf<NavAgentComponent>();

            Assert.Less(wire, mem,
                "the wire size caught up with the in-memory size — check whether the writer began "
                + "emitting padding");
            Assert.AreEqual(8, mem - wire,
                "the padding total moved. That is not wrong by itself, but it means the layout "
                + "changed shape rather than merely growing, so both pins above need re-deriving "
                + "from the field list.");
        }
    }
}
