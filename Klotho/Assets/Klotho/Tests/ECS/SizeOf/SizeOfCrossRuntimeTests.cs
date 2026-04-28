using System.Collections;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using Brawler;

namespace xpTURN.Klotho.ECS.Tests
{
    // Unsafe.SizeOf<T>() cross runtime matrix
    //
    // includePlatforms: [] → runs on both Mono editor + IL2CPP Standalone PlayMode.
    // UnityEditor.TestRunner not referenced → included in Standalone build.
    //
    // Usage:
    //   1. Editor (Mono): Test Runner → PlayMode → run SizeOfCrossRuntimeTests
    //   2. IL2CPP Standalone: Build Settings → Include Tests → build and run
    //   3. If Expected_* value is -1, Assert.Ignore (skip). Measure via PrintSizes and fill in.
    [TestFixture]
    public class SizeOfCrossRuntimeTests
    {
        // ── Measured values based on editor (Mono) ──────────────────────────────────────────
        // TODO: replace with actual values after running PrintSizes.
        private const int Expected_TransformComponent             =  92;
        private const int Expected_OwnerComponent                 =   4;
        private const int Expected_ErrorCorrectionTargetComponent =   1;
        private const int Expected_NavAgentComponent              = 708;
        private const int Expected_HealthComponent                =   8;
        private const int Expected_CombatComponent                =   8;
        private const int Expected_MovementComponent              =  36;
        private const int Expected_VelocityComponent              =  24;
        private const int Expected_CharacterComponent             =  72;
        private const int Expected_SkillCooldownComponent         =  16;

        // ── Print measured values ────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator PrintSizes()
        {
            Debug.Log("[SizeOfMatrix] === Unsafe.SizeOf measured values ===");
            Debug.Log($"  TransformComponent             = {Unsafe.SizeOf<TransformComponent>()}");
            Debug.Log($"  OwnerComponent                 = {Unsafe.SizeOf<OwnerComponent>()}");
            Debug.Log($"  ErrorCorrectionTargetComponent = {Unsafe.SizeOf<ErrorCorrectionTargetComponent>()}");
            Debug.Log($"  NavAgentComponent              = {Unsafe.SizeOf<NavAgentComponent>()}");
            Debug.Log($"  HealthComponent                = {Unsafe.SizeOf<HealthComponent>()}");
            Debug.Log($"  CombatComponent                = {Unsafe.SizeOf<CombatComponent>()}");
            Debug.Log($"  MovementComponent              = {Unsafe.SizeOf<MovementComponent>()}");
            Debug.Log($"  VelocityComponent              = {Unsafe.SizeOf<VelocityComponent>()}");
            Debug.Log($"  CharacterComponent             = {Unsafe.SizeOf<CharacterComponent>()}");
            Debug.Log($"  SkillCooldownComponent         = {Unsafe.SizeOf<SkillCooldownComponent>()}");
            yield return null;
            Assert.Pass("PrintSizes complete — apply the values above to the Expected_* constants.");
        }

        // ── Cross runtime assert ──────────────────────────────────────────────

        [UnityTest] public IEnumerator Verify_TransformComponent()             { yield return null; AssertSize<TransformComponent>(Expected_TransformComponent); }
        [UnityTest] public IEnumerator Verify_OwnerComponent()                 { yield return null; AssertSize<OwnerComponent>(Expected_OwnerComponent); }
        [UnityTest] public IEnumerator Verify_ErrorCorrectionTargetComponent() { yield return null; AssertSize<ErrorCorrectionTargetComponent>(Expected_ErrorCorrectionTargetComponent); }
        [UnityTest] public IEnumerator Verify_NavAgentComponent()              { yield return null; AssertSize<NavAgentComponent>(Expected_NavAgentComponent); }
        [UnityTest] public IEnumerator Verify_HealthComponent()                { yield return null; AssertSize<HealthComponent>(Expected_HealthComponent); }
        [UnityTest] public IEnumerator Verify_CombatComponent()                { yield return null; AssertSize<CombatComponent>(Expected_CombatComponent); }
        [UnityTest] public IEnumerator Verify_MovementComponent()              { yield return null; AssertSize<MovementComponent>(Expected_MovementComponent); }
        [UnityTest] public IEnumerator Verify_VelocityComponent()              { yield return null; AssertSize<VelocityComponent>(Expected_VelocityComponent); }
        [UnityTest] public IEnumerator Verify_CharacterComponent()             { yield return null; AssertSize<CharacterComponent>(Expected_CharacterComponent); }
        [UnityTest] public IEnumerator Verify_SkillCooldownComponent()         { yield return null; AssertSize<SkillCooldownComponent>(Expected_SkillCooldownComponent); }

        private static void AssertSize<T>(int expected) where T : unmanaged
        {
            if (expected == -1)
            {
                Assert.Ignore("Expected value not set. Run PrintSizes first.");
                return;
            }
            int actual = Unsafe.SizeOf<T>();
            Assert.AreEqual(expected, actual,
                $"{typeof(T).Name}: SizeOf={actual} != expected={expected}. " +
                $"Runtime layout mismatch — check [StructLayout] Pack=4.");
        }
    }
}
