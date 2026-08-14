using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace xpTURN.Klotho.ECS.Tests
{
    // Fingerprint test components — 9230 block to avoid other fixtures' slots.

    [KlothoComponent(9230)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct FpAlphaComponent : IComponent
    {
        public int Value;
    }

    [KlothoComponent(9231)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct FpBetaComponent : IComponent
    {
        public int Value;
    }

    /// <summary>
    /// <see cref="ComponentStorageRegistry.LayoutFingerprint"/> exists so that a state-hash
    /// divergence caused by two peers having different component registries is diagnosable from
    /// one log line instead of a per-component hash hunt. Its value is only useful if it is
    /// (a) stable for a given layout, (b) sensitive to the things that actually change the state
    /// hash, and (c) reproducible across processes.
    ///
    /// (c) is the subtle one: the fingerprint mixes type NAMES, and string.GetHashCode() is
    /// randomized per process in .NET — using it would make the same build report different
    /// fingerprints on every run, i.e. the diagnostic would accuse every match of a build
    /// mismatch. These tests cannot observe another process, so they pin the property that makes
    /// cross-process equality possible: the value is a pure function of the layout inputs.
    /// </summary>
    [TestFixture]
    public class ComponentRegistryFingerprintTests
    {
        private const int Max = 64;

        [SetUp]
        public void SetUp() => ComponentStorageRegistry.ResetForTesting();

        [Test]
        public void Fingerprint_IsStable_ForTheSameLayout()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            long first = ComponentStorageRegistry.LayoutFingerprint;
            int types = ComponentStorageRegistry.LayoutTypeCount;

            Assert.AreNotEqual(0, first, "a frozen layout must have a non-zero fingerprint");
            Assert.Greater(types, 0);

            // Re-affirming the same inputs is idempotent — it must not perturb the value.
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            Assert.AreEqual(first, ComponentStorageRegistry.LayoutFingerprint,
                "an idempotent re-affirm must not change the fingerprint");

            // Recomputing the identical layout from scratch must land on the same value — this is
            // what makes two processes running the same build comparable.
            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            Assert.AreEqual(first, ComponentStorageRegistry.LayoutFingerprint,
                "the fingerprint must be a pure function of the layout inputs, not of process state");
            Assert.AreEqual(types, ComponentStorageRegistry.LayoutTypeCount);
        }

        [Test]
        public void Fingerprint_ChangesWhen_ATypeLeavesTheLayout()
        {
            // This is the case the diagnostic exists for: a peer whose registry holds a type the
            // other peer does not have. Pruning reproduces it locally — a pruned type drops out of
            // the layout exactly like a type from an assembly the other build does not load.
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            long all = ComponentStorageRegistry.LayoutFingerprint;
            int allCount = ComponentStorageRegistry.LayoutTypeCount;
            int beta = ComponentStorageRegistry.GetTypeId<FpBetaComponent>();

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, new List<int> { beta });

            Assert.AreNotEqual(all, ComponentStorageRegistry.LayoutFingerprint,
                "dropping a type from the layout must change the fingerprint — that difference is " +
                "exactly what makes the two peers' state hashes disagree");
            Assert.AreEqual(allCount - 1, ComponentStorageRegistry.LayoutTypeCount);
        }

        [Test]
        public void Fingerprint_ChangesWith_MaxEntities()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            long atMax = ComponentStorageRegistry.LayoutFingerprint;

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max * 2, null, null);

            Assert.AreNotEqual(atMax, ComponentStorageRegistry.LayoutFingerprint,
                "maxEntities is a layout input and must be covered");
        }

        [Test]
        public void Fingerprint_ChangesWith_MaxCountOverrides()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            long plain = ComponentStorageRegistry.LayoutFingerprint;
            int alpha = ComponentStorageRegistry.GetTypeId<FpAlphaComponent>();

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(Max, new Dictionary<int, int> { { alpha, 3 } }, null);

            Assert.AreNotEqual(plain, ComponentStorageRegistry.LayoutFingerprint,
                "a per-type slot cap changes the layout and must be covered");
        }

        [Test]
        public void UnregisteredPruneIds_AreInert()
        {
            // The denylist is shared verbatim by every peer, but the peers do not all register the
            // same types: a dedicated server has no Editor-only test components, yet it is handed
            // the same list that prunes them on the client. That only works if pruning a typeId
            // that was never registered is a no-op rather than an error, so pin it.
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            long plain = ComponentStorageRegistry.LayoutFingerprint;
            int types = ComponentStorageRegistry.LayoutTypeCount;

            ComponentStorageRegistry.ResetForTesting();
            ComponentStorageRegistry.EnsureLayoutComputed(
                Max, null, new List<int> { 8001, 8002, 8003 });   // ids no fixture declares

            Assert.AreEqual(plain, ComponentStorageRegistry.LayoutFingerprint,
                "pruning ids that were never registered must not change the layout");
            Assert.AreEqual(types, ComponentStorageRegistry.LayoutTypeCount);
        }

        [Test]
        public void Fingerprint_IsClearedOnReset()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);
            Assert.AreNotEqual(0, ComponentStorageRegistry.LayoutFingerprint);

            ComponentStorageRegistry.ResetForTesting();

            Assert.AreEqual(0, ComponentStorageRegistry.LayoutFingerprint,
                "an unfrozen registry must not report a stale fingerprint");
            Assert.AreEqual(0, ComponentStorageRegistry.LayoutTypeCount);
        }

        [Test]
        public void ConflictingRecompute_StillThrows_WithoutTheTestOptIn()
        {
            // The freeze guard is a determinism protection: maxEntities and the override/prune sets
            // have to be uniform across a process, so a shipping build must refuse a conflicting
            // recompute rather than quietly relaying it.
            //
            // This suite turns that refusal OFF assembly-wide (TestAssemblySetup) because fixtures
            // legitimately want different layouts. That opt-in is the only reason the relaxation
            // happens, and nothing else asserted it — the behaviour used to be selected by #if, so
            // a test for the shipping side could not exist in the configuration that has it. With
            // the gate moved to a runtime flag it can, and this is it: clear the flag and the
            // throw must come back, in Debug and Release alike.
            bool saved = ComponentStorageRegistry.AllowLayoutRecompute;
            try
            {
                ComponentStorageRegistry.AllowLayoutRecompute = false;
                ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null);

                Assert.DoesNotThrow(() => ComponentStorageRegistry.EnsureLayoutComputed(Max, null, null),
                    "an identical re-affirm is idempotent and must never be treated as a conflict");

                var ex = Assert.Throws<InvalidOperationException>(
                    () => ComponentStorageRegistry.EnsureLayoutComputed(Max * 2, null, null),
                    "a shipping build must refuse a conflicting layout recompute");
                StringAssert.Contains("frozen at", ex.Message);
            }
            finally
            {
                ComponentStorageRegistry.AllowLayoutRecompute = saved;
                ComponentStorageRegistry.ResetForTesting();
            }
        }
    }
}
