using NUnit.Framework;

using xpTURN.Klotho.Core;          // PlayerConfigBase
using xpTURN.Klotho.Network;       // IdentityValidationOutcome, PlayerConfigVerdict(Kind), IPlayerConfigEntitlementGuard, ServerNetworkService
using xpTURN.Klotho.Serialization; // SpanWriter, SpanReader

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Contract tests for the public entitlement seams: the opaque entitlement carried on an accepted
    /// identity outcome, the player-config verdict factory shapes, and the server-side guard wiring.
    /// These lock the public API surface headlessly (the full join-flow / guard-fires behaviour needs the
    /// Unity harness that can drive the sync handshake).
    /// </summary>
    public sealed class EntitlementContractTests
    {
        // Minimal concrete PlayerConfigBase so the Clamp verdict can carry a real replacement reference.
        private sealed class StubPlayerConfig : PlayerConfigBase
        {
            public override NetworkMessageType MessageTypeId => (NetworkMessageType)200; // UserDefined range
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }

        // ── IdentityValidationOutcome: opaque entitlement carry ──────────────────────────────

        [Test] // the 3-arg accept carries the entitlement reference verbatim onto an accepted outcome
        public void Accept_WithEntitlement_CarriesReference()
        {
            var ent = new byte[] { 1, 2, 3 };
            var outcome = IdentityValidationOutcome.Accept("acct", "Name", ent);

            Assert.IsTrue(outcome.Accepted);
            Assert.AreEqual("acct", outcome.Account);
            Assert.AreEqual("Name", outcome.DisplayName);
            Assert.AreSame(ent, outcome.Entitlement);
        }

        [Test] // the legacy 2-arg accept leaves the entitlement null (no-entitlement path unchanged)
        public void Accept_WithoutEntitlement_NullEntitlement()
        {
            var outcome = IdentityValidationOutcome.Accept("acct", "Name");

            Assert.IsTrue(outcome.Accepted);
            Assert.IsNull(outcome.Entitlement);
        }

        [Test] // a reject never carries an entitlement and is not accepted
        public void Reject_NotAccepted_NullEntitlement()
        {
            var outcome = IdentityValidationOutcome.Reject(9);

            Assert.IsFalse(outcome.Accepted);
            Assert.IsNull(outcome.Entitlement);
            Assert.AreEqual(9, outcome.RejectWireCode);
        }

        // ── PlayerConfigVerdict: factory shapes ──────────────────────────────────────────────

        [Test]
        public void Pass_HasPassKind_NoReplacement()
        {
            var v = PlayerConfigVerdict.Pass();

            Assert.AreEqual(PlayerConfigVerdictKind.Pass, v.Kind);
            Assert.IsNull(v.Replacement);
            Assert.AreEqual(0, v.RejectWireCode);
        }

        [Test] // clamp carries the server-chosen replacement on the verdict (no out-param)
        public void Clamp_CarriesReplacement()
        {
            var replacement = new StubPlayerConfig();
            var v = PlayerConfigVerdict.Clamp(replacement);

            Assert.AreEqual(PlayerConfigVerdictKind.Clamp, v.Kind);
            Assert.AreSame(replacement, v.Replacement);
        }

        [Test]
        public void Reject_CarriesWireCode()
        {
            var v = PlayerConfigVerdict.Reject(11);

            Assert.AreEqual(PlayerConfigVerdictKind.Reject, v.Kind);
            Assert.IsNull(v.Replacement);
            Assert.AreEqual(11, v.RejectWireCode);
        }

        // ── ServerNetworkService seam wiring (public surface) ────────────────────────────────

        [Test] // the guard setter is part of the public surface and accepts null (unset = passthrough)
        public void SetPlayerConfigEntitlementGuard_AcceptsNull()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            svc.SetPlayerConfigEntitlementGuard(null); // no throw
        }

        [Test] // an unknown player has no entitlement (no join → FindPlayerById null → null blob)
        public void GetPlayerEntitlement_UnknownPlayer_Null()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            Assert.IsNull(svc.GetPlayerEntitlement(999));
        }

        // ── reliable-command entitlement gate (public surface) ───────────────────────────────

        [Test] // Accept factory has the Accept kind
        public void ReliableCommandVerdict_Accept_HasAcceptKind()
        {
            var v = ReliableCommandVerdict.Accept();
            Assert.AreEqual(ReliableCommandVerdictKind.Accept, v.Kind);
        }

        [Test] // Drop factory has the Drop kind
        public void ReliableCommandVerdict_Drop_HasDropKind()
        {
            var v = ReliableCommandVerdict.Drop();
            Assert.AreEqual(ReliableCommandVerdictKind.Drop, v.Kind);
        }

        [Test] // the gate setter is part of the public surface and accepts null (unset = accept all)
        public void SetReliableCommandEntitlementGate_AcceptsNull()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            svc.SetReliableCommandEntitlementGate(null); // no throw
        }
    }
}
