using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using xpTURN.Klotho.ECS;     // FixedString64
using xpTURN.Klotho.Network; // messages, MessageSerializer, IPropagatedTicketVerifier, IdentityValidationOutcome, KlothoNetworkService

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Headless contract tests for the P2P original-ticket propagation surface: the additive
    /// wire fields (OriginalTicket / RosterTickets) round-trip through the serializer incl. the gate-off
    /// empty case (no same-version regression), and the public re-verify seam + service setters.
    /// The full propagate→re-verify behaviour is driven by the sync handshake and needs the Unity/multi
    /// harness (mirrors EntitlementContractTests), so these lock the public/wire surface only.
    /// </summary>
    public sealed class PropagatedTicketContractTests
    {
        private static MessageSerializer NewSerializer() => new MessageSerializer();

        private static T RoundTrip<T>(T msg) where T : class, INetworkMessage
        {
            var ser = NewSerializer();
            byte[] bytes = ser.Serialize(msg);
            return ser.Deserialize(bytes, bytes.Length) as T;
        }

        // ── Single-player notifications: string OriginalTicket trailing field ─────────────────

        [Test] // PlayerJoinNotification round-trips a populated OriginalTicket (gate-on shape)
        public void PlayerJoinNotification_OriginalTicket_RoundTrips()
        {
            var back = RoundTrip(new PlayerJoinNotificationMessage
            {
                PlayerId = 3, ConnectionState = 1, IsReady = true,
                Account = "acct", DisplayName = "Name", OriginalTicket = "payload.signature",
            });

            Assert.IsNotNull(back);
            Assert.AreEqual(3, back.PlayerId);
            Assert.AreEqual("acct", back.Account);
            Assert.AreEqual("Name", back.DisplayName);
            Assert.AreEqual("payload.signature", back.OriginalTicket);
        }

        [Test] // gate-off shape: empty OriginalTicket round-trips as "" (no same-version regression)
        public void PlayerJoinNotification_EmptyOriginalTicket_RoundTrips()
        {
            var back = RoundTrip(new PlayerJoinNotificationMessage
            {
                PlayerId = 1, Account = "a", DisplayName = "n", OriginalTicket = "",
            });

            Assert.IsNotNull(back);
            Assert.AreEqual("", back.OriginalTicket);
        }

        [Test] // LateJoinNotification round-trips OriginalTicket (single-player path)
        public void LateJoinNotification_OriginalTicket_RoundTrips()
        {
            var back = RoundTrip(new LateJoinNotificationMessage
            {
                PlayerId = 5, JoinTick = 42, Account = "acct", DisplayName = "Late", OriginalTicket = "tok",
            });

            Assert.IsNotNull(back);
            Assert.AreEqual(42, back.JoinTick);
            Assert.AreEqual("tok", back.OriginalTicket);
        }

        // ── Roster snapshots: List<string> RosterTickets, index-parallel to Roster ────────────

        [Test] // SyncComplete round-trips RosterTickets, order preserved (index-parallel to Roster)
        public void SyncComplete_RosterTickets_RoundTripsInOrder()
        {
            var msg = new SyncCompleteMessage { Magic = 7, PlayerId = 2, SharedEpoch = 1, ClockOffset = 0 };
            msg.RosterTickets.Add("t0");
            msg.RosterTickets.Add("t1");
            msg.RosterTickets.Add("t2");

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            Assert.AreEqual(3, back.RosterTickets.Count);
            Assert.AreEqual("t0", back.RosterTickets[0]);
            Assert.AreEqual("t1", back.RosterTickets[1]);
            Assert.AreEqual("t2", back.RosterTickets[2]);
        }

        [Test] // gate-off shape: empty RosterTickets round-trips as an empty list (no regression)
        public void SyncComplete_EmptyRosterTickets_RoundTrips()
        {
            var back = RoundTrip(new SyncCompleteMessage { Magic = 7, PlayerId = 2 });

            Assert.IsNotNull(back);
            Assert.IsNotNull(back.RosterTickets);
            Assert.IsEmpty(back.RosterTickets);
        }

        [Test] // LateJoinAccept: RosterTickets co-exists with the separate PlayerConfigData/Lengths fields
        public void LateJoinAccept_RosterTickets_CoexistWithPlayerConfig()
        {
            var msg = new LateJoinAcceptMessage { PlayerId = 4, CurrentTick = 10, Magic = 9, PlayerCount = 2 };
            msg.PlayerConfigData = new byte[] { 1, 2 };
            msg.PlayerConfigLengths.Add(2);
            msg.RosterTickets.Add("rtA");
            msg.RosterTickets.Add("rtB");

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            Assert.AreEqual(2, back.RosterTickets.Count);
            Assert.AreEqual("rtA", back.RosterTickets[0]);
            Assert.AreEqual("rtB", back.RosterTickets[1]);
            // The trusted-ticket field and the untrusted-config field stay distinct on the wire.
            Assert.AreEqual(new byte[] { 1, 2 }, back.PlayerConfigData);
            XAssert.Single(back.PlayerConfigLengths);
        }

        [Test] // ReconnectAccept round-trips RosterTickets (reconnect path)
        public void ReconnectAccept_RosterTickets_RoundTrips()
        {
            var msg = new ReconnectAcceptMessage { PlayerId = 6, CurrentTick = 20, PlayerCount = 1 };
            msg.RosterTickets.Add("rc0");

            var back = RoundTrip(msg);

            Assert.IsNotNull(back);
            XAssert.Single(back.RosterTickets);
            Assert.AreEqual("rc0", back.RosterTickets[0]);
        }

        // ── Re-verify seam (IPropagatedTicketVerifier) ───────────────────────────────────────

        // Mock verifier — the test controls the verdict; mirrors how a game implements the seam.
        private sealed class StubVerifier : IPropagatedTicketVerifier
        {
            private readonly IdentityValidationOutcome _outcome;
            public string LastTicket;
            public StubVerifier(IdentityValidationOutcome outcome) { _outcome = outcome; }
            public IdentityValidationOutcome ReverifyPropagatedTicket(string ticket)
            {
                LastTicket = ticket;
                return _outcome;
            }
        }

        [Test] // the seam is implementable and returns an accepted outcome carrying the ticket identity
        public void Verifier_Accept_CarriesTicketIdentity()
        {
            IPropagatedTicketVerifier v = new StubVerifier(IdentityValidationOutcome.Accept("ticketAcct", "TicketName"));

            var outcome = v.ReverifyPropagatedTicket("some.ticket");

            Assert.IsTrue(outcome.Accepted);
            Assert.AreEqual("ticketAcct", outcome.Account);
            Assert.AreEqual("TicketName", outcome.DisplayName);
        }

        [Test] // a failed signature re-verification is simply not accepted (caller routes it, RejectWireCode unused)
        public void Verifier_Reject_NotAccepted()
        {
            IPropagatedTicketVerifier v = new StubVerifier(IdentityValidationOutcome.Reject(6));

            var outcome = v.ReverifyPropagatedTicket("forged");

            Assert.IsFalse(outcome.Accepted);
        }

        // ── KlothoNetworkService public setters (original-ticket gate plumbing) ──────────────

        [Test] // the re-verifier setter is public and accepts null (unset = no re-verification)
        public void SetPropagatedTicketVerifier_AcceptsNull()
        {
            var svc = new KlothoNetworkService();
            svc.SetPropagatedTicketVerifier(null);                                  // no throw
            svc.SetPropagatedTicketVerifier(new StubVerifier(default));             // no throw
        }

        [Test] // the propagation gate setter is public; enabling without a verifier is fail-closed (refused, no throw)
        public void SetOriginalTicketPropagation_FailClosed_NoThrow()
        {
            var svc = new KlothoNetworkService();

            svc.SetOriginalTicketPropagation(false);   // off — no throw
            svc.SetOriginalTicketPropagation(true);    // requested without a verifier → refused (gate stays off), no throw

            // With a verifier present, enabling is accepted (no throw). Gate state is private; the
            // observable contract here is that the public seam does not throw on either path.
            svc.SetPropagatedTicketVerifier(new StubVerifier(default));
            svc.SetOriginalTicketPropagation(true);
        }

        // ── KlothoNetworkService entitlement surface ─────────────────────────────────────────

        // Minimal guard stub — the test controls the verdict (mirrors a game's IPlayerConfigEntitlementGuard).
        private sealed class StubGuard : IPlayerConfigEntitlementGuard
        {
            private readonly PlayerConfigVerdict _verdict;
            public StubGuard(PlayerConfigVerdict verdict) { _verdict = verdict; }
            public PlayerConfigVerdict Check(int playerId, byte[] entitlement, Core.PlayerConfigBase selection) => _verdict;
        }

        [Test] // the P2P guard setter is public and accepts null, leaving behaviour unchanged when unset
        public void P2P_SetPlayerConfigEntitlementGuard_AcceptsNullAndGuard()
        {
            var svc = new KlothoNetworkService();
            svc.SetPlayerConfigEntitlementGuard(null);                          // no throw
            svc.SetPlayerConfigEntitlementGuard(new StubGuard(PlayerConfigVerdict.Pass())); // no throw
        }

        [Test] // the P2P entitlement accessor is public; an unknown player has no roster entry, so it is null
        public void P2P_GetPlayerEntitlement_UnknownPlayer_Null()
        {
            var svc = new KlothoNetworkService();
            Assert.IsNull(svc.GetPlayerEntitlement(999));
        }

        [Test] // the re-verify seam carries the entitlement through to the outcome — the bytes the guest
                // stores — locking the accept-with-entitlement flow on the public surface headlessly
        public void Verifier_Accept_CarriesEntitlement()
        {
            var ent = new byte[] { 9, 8, 7 };
            IPropagatedTicketVerifier v = new StubVerifier(IdentityValidationOutcome.Accept("a", "n", ent));

            var outcome = v.ReverifyPropagatedTicket("some.ticket");

            Assert.IsTrue(outcome.Accepted);
            Assert.AreSame(ent, outcome.Entitlement);
        }

        // ── Catchup seed path (late-join / cold-reconnect) re-verification ─────────────────────
        //
        // Unlike the wire-only tests above, these drive the PUBLIC seed entry points
        // (SeedLateJoinPlayers / SeedReconnectPlayers) directly, so they lock actual behaviour: the seed
        // path re-verifies each propagated ticket and adopts its entitlement/identity. Previously the seed
        // dropped RosterTickets, so the entitlement assertions below returned null. LocalPlayerId defaults
        // to 0 on a fresh service, so the roster entry with PlayerId 0 exercises the OWN branch (identity
        // kept local, own entitlement extracted) and PlayerId 2 the OTHER branch (identity adopted from the
        // verified ticket, not the host relay).

        private static RosterEntry Entry(int playerId, string account, string displayName) => new RosterEntry
        {
            PlayerId = playerId,
            ConnectionState = 1,
            ReadyState = 0,
            Account = FixedString64.FromString(account),
            DisplayName = FixedString64.FromString(displayName),
        };

        // Verifier keyed by ticket string, recording which tickets were re-verified.
        private sealed class MapVerifier : IPropagatedTicketVerifier
        {
            private readonly Dictionary<string, IdentityValidationOutcome> _map;
            public readonly List<string> Seen = new List<string>();
            public MapVerifier(Dictionary<string, IdentityValidationOutcome> map) { _map = map; }
            public IdentityValidationOutcome ReverifyPropagatedTicket(string ticket)
            {
                Seen.Add(ticket);
                return _map.TryGetValue(ticket, out var o) ? o : IdentityValidationOutcome.Reject(6);
            }
        }

        private static KlothoNetworkService NewGateOnService(MapVerifier verifier)
        {
            var svc = new KlothoNetworkService();
            svc.SetPropagatedTicketVerifier(verifier);
            svc.SetOriginalTicketPropagation(true);   // fail-closed: accepted because a verifier is present
            return svc;
        }

        [Test] // late-join seed re-verifies each ticket, adopts OTHER identity from the ticket (not the relay),
               // keeps OWN identity local, and extracts entitlement for both (previously: entitlement == null)
        public void SeedLateJoin_GateOn_ReverifiesAndAdoptsEntitlement()
        {
            var ent0 = new byte[] { 0 };
            var ent2 = new byte[] { 2, 2 };
            var verifier = new MapVerifier(new Dictionary<string, IdentityValidationOutcome>
            {
                ["t0"] = IdentityValidationOutcome.Accept("hostAcct", "HostName", ent0),
                ["t2"] = IdentityValidationOutcome.Accept("verifiedAcct2", "VerifiedName2", ent2),
            });
            var svc = NewGateOnService(verifier);   // LocalPlayerId == 0 → entry 0 is OWN

            var accept = new LateJoinAcceptMessage { PlayerId = 2, RandomSeed = 123, PlayerCount = 2 };
            accept.Roster.Add(Entry(0, "localHost", "LocalHost"));
            accept.Roster.Add(Entry(2, "RELAY-TAMPERED", "RelayName"));
            accept.RosterTickets.Add("t0");
            accept.RosterTickets.Add("t2");

            svc.SeedLateJoinPlayers(new Core.LateJoinPayload { AcceptMessage = accept });

            // entitlement derived from the re-verified ticket for BOTH own and other (the fixed behaviour)
            Assert.AreSame(ent0, svc.GetPlayerEntitlement(0));
            Assert.AreSame(ent2, svc.GetPlayerEntitlement(2));
            // each propagated ticket was re-verified
            XAssert.Contains("t0", verifier.Seen);
            XAssert.Contains("t2", verifier.Seen);
            // OTHER: identity adopted from the ticket (zero-trust — NOT the tampered relay value)
            var p2 = svc.Players.First(p => p.PlayerId == 2);
            Assert.AreEqual("verifiedAcct2", p2.Account);
            Assert.AreEqual("VerifiedName2", p2.DisplayName);
            // OWN: locally-known identity kept (throwaway refs), entitlement still extracted
            var p0 = svc.Players.First(p => p.PlayerId == 0);
            Assert.AreEqual("localHost", p0.Account);
            Assert.AreEqual("LocalHost", p0.DisplayName);
        }

        [Test] // gate off: seed does NOT re-verify (entitlement null) and names stay at the host-relayed
               // roster values (roster-init, aligned with warm reconnect) — entitlement/guard path unchanged
        public void SeedLateJoin_GateOff_NoReverify_KeepsRosterNames()
        {
            var svc = new KlothoNetworkService();   // no verifier, propagation off (default)

            var accept = new LateJoinAcceptMessage { PlayerId = 2, RandomSeed = 1, PlayerCount = 1 };
            accept.Roster.Add(Entry(2, "relayAcct", "RelayName"));
            accept.RosterTickets.Add("t2");         // present but ignored while the gate is off

            svc.SeedLateJoinPlayers(new Core.LateJoinPayload { AcceptMessage = accept });

            Assert.IsNull(svc.GetPlayerEntitlement(2));
            var p2 = svc.Players.First(p => p.PlayerId == 2);
            Assert.AreEqual("relayAcct", p2.Account);
            Assert.AreEqual("RelayName", p2.DisplayName);
        }

        [Test] // cold-start reconnect seed adopts the ticket entitlement too (SeedReconnectPlayers passes RosterTickets)
        public void SeedReconnect_GateOn_ReverifiesAndAdoptsEntitlement()
        {
            var ent = new byte[] { 7, 7 };
            var verifier = new MapVerifier(new Dictionary<string, IdentityValidationOutcome>
            {
                ["rc2"] = IdentityValidationOutcome.Accept("verifiedAcct", "VerifiedName", ent),
            });
            var svc = NewGateOnService(verifier);

            var accept = new ReconnectAcceptMessage { PlayerId = 2, RandomSeed = 5, PlayerCount = 1 };
            accept.Roster.Add(Entry(2, "relayAcct", "RelayName"));
            accept.RosterTickets.Add("rc2");

            svc.SeedReconnectPlayers(new Core.ReconnectPayload { AcceptMessage = accept });

            Assert.AreSame(ent, svc.GetPlayerEntitlement(2));
            Assert.AreEqual("verifiedAcct", svc.Players.First(p => p.PlayerId == 2).Account);
        }

        [Test] // bound check: a RosterTickets list shorter than Roster does not throw; the unmatched entry
               // gets a null ticket → no-op (entitlement null), the matched entry is still re-verified
        public void SeedLateJoin_ShortRosterTickets_NoIndexOutOfRange()
        {
            var ent = new byte[] { 1 };
            var verifier = new MapVerifier(new Dictionary<string, IdentityValidationOutcome>
            {
                ["t0"] = IdentityValidationOutcome.Accept("a", "n", ent),
            });
            var svc = NewGateOnService(verifier);

            var accept = new LateJoinAcceptMessage { PlayerId = 2, RandomSeed = 1, PlayerCount = 2 };
            accept.Roster.Add(Entry(0, "h", "H"));
            accept.Roster.Add(Entry(2, "g", "G"));
            accept.RosterTickets.Add("t0");         // only one ticket for two roster entries

            svc.SeedLateJoinPlayers(new Core.LateJoinPayload { AcceptMessage = accept });

            Assert.AreEqual(2, svc.Players.Count);                 // seed completed, no exception
            Assert.AreSame(ent, svc.GetPlayerEntitlement(0));      // index 0 re-verified (OWN branch)
            Assert.IsNull(svc.GetPlayerEntitlement(2));           // index 1 out of range → null ticket → no-op
        }
    }
}
