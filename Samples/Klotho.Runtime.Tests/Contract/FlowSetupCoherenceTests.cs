using NUnit.Framework;

using xpTURN.Klotho.Core;    // KlothoFlowSetupBuilder, FlowSetupValidationException, PlayerConfigBase
using xpTURN.Klotho.Network; // IPlayerConfigEntitlementGuard, IPlayerIdentityValidator, PlayerConfigVerdict, IIdentityValidation, IdentityValidationRequest

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Coherence tests for the P2P entitlement-guard ↔ identity-validator dependency. A guard
    /// without a validator is silently dropped by KlothoSession.Create (the guard wiring is nested inside the
    /// validator branch, since the validator doubles as the P2P re-verifier), so the builder must fail fast.
    /// These pin the shared coherence predicate through the public Build() surface — the same predicate the
    /// KlothoSession.Create escape-hatch log uses, so verifying it here also covers that site's decision logic.
    /// </summary>
    public sealed class FlowSetupCoherenceTests
    {
        private sealed class StubGuard : IPlayerConfigEntitlementGuard
        {
            public PlayerConfigVerdict Check(int playerId, byte[] entitlement, PlayerConfigBase selection)
                => PlayerConfigVerdict.Pass();
        }

        private sealed class StubValidator : IPlayerIdentityValidator
        {
            // Build() never invokes the validator (it only checks presence), so a null handle is sufficient.
            public IIdentityValidation BeginValidate(in IdentityValidationRequest request) => null;
        }

        // The callbacks factory is stored, never invoked by Build(), so a trivial stub is enough.
        private static KlothoFlowSetupBuilder NewBuilder()
            => new KlothoFlowSetupBuilder((_, _) => default);

        [Test] // guard && !validator → the one true case of the predicate: Build() must throw.
        public void Build_GuardWithoutValidator_Throws()
        {
            var ex = Assert.Throws<FlowSetupValidationException>(() =>
                NewBuilder()
                    .WithTransport(new FakeTransport())
                    .WithPlayerConfigEntitlementGuard(new StubGuard())
                    .Build());
            XAssert.Contains("WithIdentityValidator", ex.Message);
        }

        [Test] // guard && validator → false: the normal P2P host config is not blocked. Transport avoids the
                // validator-without-transport advisory noise.
        public void Build_GuardWithValidator_Succeeds()
        {
            var setup = NewBuilder()
                .WithTransport(new FakeTransport())
                .WithIdentityValidator(new StubValidator())
                .WithPlayerConfigEntitlementGuard(new StubGuard())
                .Build();
            Assert.IsNotNull(setup);
        }

        [Test] // !guard && validator → false: identity-only P2P (no entitlements) is unchanged.
        public void Build_ValidatorOnly_Succeeds()
        {
            var setup = NewBuilder()
                .WithTransport(new FakeTransport())
                .WithIdentityValidator(new StubValidator())
                .Build();
            Assert.IsNotNull(setup);
        }

        [Test] // !guard && !validator → false: LAN / no-lobby is unchanged.
        public void Build_NeitherGuardNorValidator_Succeeds()
        {
            var setup = NewBuilder()
                .WithTransport(new FakeTransport())
                .Build();
            Assert.IsNotNull(setup);
        }

        [Test] // recording off + save path → advisory: nothing will exist to save, and Stop warns every time.
        public void Build_NoRecordingWithReplaySave_IsAdvisory()
        {
            var ex = Assert.Throws<FlowSetupValidationException>(() =>
                NewBuilder()
                    .WithTransport(new FakeTransport())
                    .WithoutReplayRecording()
                    .WithReplaySave("/tmp/never-written.rply")
                    .Build(strict: true));
            XAssert.Contains("WithoutReplayRecording", ex.Message);

            // Advisory, not an error: the non-strict build still succeeds (it logs instead).
            Assert.IsNotNull(NewBuilder()
                .WithTransport(new FakeTransport())
                .WithoutReplayRecording()
                .WithReplaySave("/tmp/never-written.rply")
                .Build());
        }

        [Test] // recording off ALONE is the intended solo configuration — it must not be flagged.
        public void Build_NoRecordingWithoutReplaySave_Succeeds()
        {
            Assert.IsNotNull(NewBuilder()
                .WithTransport(new FakeTransport())
                .WithoutReplayRecording()
                .Build(strict: true));
        }
    }
}
