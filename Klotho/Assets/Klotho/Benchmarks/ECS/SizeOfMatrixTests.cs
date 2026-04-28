using System.Runtime.CompilerServices;
using NUnit.Framework;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using Brawler;

namespace xpTURN.Klotho.ECS.Benchmarks
{
    // Cross-runtime matrix test for Unsafe.SizeOf<T>().
    //
    // Asserts in CI that the same type has identical memory size in Mono editor and IL2CPP environments.
    // The values measured in editor (Mono) are defined as constants and compared against the same constants
    // in all environments; on mismatch, Assert.Fail makes CI fail.
    //
    // Target types:
    //   Runtime  : TransformComponent, OwnerComponent, ErrorCorrectionTargetComponent(1-byte struct)
    //              NavAgentComponent(type with fixed buffer)
    //   Gameplay : HealthComponent, CombatComponent, MovementComponent, VelocityComponent
    //   Brawler  : CharacterComponent, SkillCooldownComponent
    //
    // ─────────────────────────────────────────────────────────────────────────────
    // How to update constants:
    //   1. Run the PrintSizes test in Unity Editor (Mono)
    //   2. Reflect the Console output values into the Expected_* constants below
    //   3. Run the Verify_* tests in Editor (Mono) and confirm they pass
    //
    // IL2CPP environment verification:
    //   See Tests/ECS/SizeOf/SizeOfCrossRuntimeTests.cs.
    //   It is split into the xpTURN.Klotho.SizeOfTests asmdef (includePlatforms:[])
    //   so the same Verify_* tests can run in IL2CPP Standalone PlayMode builds.
    // ─────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SizeOfMatrixTests
    {
        // ── Measured values based on Editor (Mono) ──────────────────────────────
        // TODO: Replace with actual values after running PrintSizes in Unity Editor Mono.
        //       The values below are compile-time estimates based on StructLayout(Sequential, Pack=4) rules.
        private const int Expected_TransformComponent             = 92;  // Not measured — update after running PrintSizes
        private const int Expected_OwnerComponent                 = 4;
        private const int Expected_ErrorCorrectionTargetComponent = 1;
        private const int Expected_NavAgentComponent              = 708;
        private const int Expected_HealthComponent                = 8;
        private const int Expected_CombatComponent                = 8;
        private const int Expected_MovementComponent              = 36;
        private const int Expected_VelocityComponent              = 24;
        private const int Expected_CharacterComponent             = 72;
        private const int Expected_SkillCooldownComponent         = 16;

        // ── Print measured values (run in Editor Mono to fix the constants) ──────

        [Test]
        public void PrintSizes()
        {
            UnityEngine.Debug.Log("[SizeOfMatrix] === Unsafe.SizeOf measured values ===");
            UnityEngine.Debug.Log($"  TransformComponent             = {Unsafe.SizeOf<TransformComponent>()}");
            UnityEngine.Debug.Log($"  OwnerComponent                 = {Unsafe.SizeOf<OwnerComponent>()}");
            UnityEngine.Debug.Log($"  ErrorCorrectionTargetComponent = {Unsafe.SizeOf<ErrorCorrectionTargetComponent>()}");
            UnityEngine.Debug.Log($"  NavAgentComponent              = {Unsafe.SizeOf<NavAgentComponent>()}");
            UnityEngine.Debug.Log($"  HealthComponent                = {Unsafe.SizeOf<HealthComponent>()}");
            UnityEngine.Debug.Log($"  CombatComponent                = {Unsafe.SizeOf<CombatComponent>()}");
            UnityEngine.Debug.Log($"  MovementComponent              = {Unsafe.SizeOf<MovementComponent>()}");
            UnityEngine.Debug.Log($"  VelocityComponent              = {Unsafe.SizeOf<VelocityComponent>()}");
            UnityEngine.Debug.Log($"  CharacterComponent             = {Unsafe.SizeOf<CharacterComponent>()}");
            UnityEngine.Debug.Log($"  SkillCooldownComponent         = {Unsafe.SizeOf<SkillCooldownComponent>()}");

            Assert.Pass("PrintSizes complete — reflect the values above into the Expected_* constants.");
        }

        // ── Cross-runtime assert (triggers CI failure) ───────────────────────────
        // If Expected_* is -1, it is unmeasured → skip. After setting values, run in all environments.

        [Test]
        public void Verify_TransformComponent()            => AssertSize<TransformComponent>(Expected_TransformComponent);
        [Test]
        public void Verify_OwnerComponent()                => AssertSize<OwnerComponent>(Expected_OwnerComponent);
        [Test]
        public void Verify_ErrorCorrectionTargetComponent() => AssertSize<ErrorCorrectionTargetComponent>(Expected_ErrorCorrectionTargetComponent);
        [Test]
        public void Verify_NavAgentComponent()             => AssertSize<NavAgentComponent>(Expected_NavAgentComponent);
        [Test]
        public void Verify_HealthComponent()               => AssertSize<HealthComponent>(Expected_HealthComponent);
        [Test]
        public void Verify_CombatComponent()               => AssertSize<CombatComponent>(Expected_CombatComponent);
        [Test]
        public void Verify_MovementComponent()             => AssertSize<MovementComponent>(Expected_MovementComponent);
        [Test]
        public void Verify_VelocityComponent()             => AssertSize<VelocityComponent>(Expected_VelocityComponent);
        [Test]
        public void Verify_CharacterComponent()            => AssertSize<CharacterComponent>(Expected_CharacterComponent);
        [Test]
        public void Verify_SkillCooldownComponent()        => AssertSize<SkillCooldownComponent>(Expected_SkillCooldownComponent);

        private static void AssertSize<T>(int expected) where T : unmanaged
        {
            if (expected == -1)
            {
                Assert.Ignore("Expected value not set. Run PrintSizes in Editor(Mono) first.");
                return;
            }
            int actual = Unsafe.SizeOf<T>();
            Assert.AreEqual(expected, actual,
                $"{typeof(T).Name}: Unsafe.SizeOf={actual} differs from expected={expected}. " +
                $"Runtime layout mismatch — check [StructLayout] Pack=4 and IL2CPP settings.");
        }
    }
}
