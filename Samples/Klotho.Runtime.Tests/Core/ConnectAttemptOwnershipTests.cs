using System.Threading;
using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// KlothoSessionFlow connect-attempt ownership: supersede + identity-guarded End.
    /// These cover the overlapping-supersede race deterministically — driving Begin/End in a
    /// controlled order exercises the guard without depending on cancellation propagation timing,
    /// which an in-editor play session cannot reliably reproduce.
    /// </summary>
    public class ConnectAttemptOwnershipTests
    {
        static KlothoSessionFlow NewFlow()
            => new KlothoSessionFlow(new KlothoFlowSetup { CallbacksFactory = (_, __) => default });

        [Test]
        public void Begin_SetsIsConnecting_AndLinksExternalToken()
        {
            var flow = NewFlow();
            using var external = new CancellationTokenSource();

            var cts = flow.BeginConnectAttempt(external.Token);
            Assert.IsTrue(flow.IsConnecting);
            Assert.IsFalse(cts.Token.IsCancellationRequested);

            external.Cancel();   // external (e.g. destroy) token cancels the linked attempt
            Assert.IsTrue(cts.Token.IsCancellationRequested);

            flow.EndConnectAttempt(cts);
            Assert.IsFalse(flow.IsConnecting);
        }

        [Test]
        public void Supersede_CancelsPrior_KeepsIsConnectingTrue_ForLiveAttempt()
        {
            var flow = NewFlow();

            var cts1 = flow.BeginConnectAttempt(CancellationToken.None);
            var token1 = cts1.Token;

            var cts2 = flow.BeginConnectAttempt(CancellationToken.None);

            Assert.IsTrue(token1.IsCancellationRequested, "prior attempt is superseded (canceled)");
            Assert.IsFalse(cts2.Token.IsCancellationRequested, "new attempt is live");
            Assert.IsTrue(flow.IsConnecting, "IsConnecting stays true for the live attempt");

            flow.EndConnectAttempt(cts2);
            Assert.IsFalse(flow.IsConnecting);
        }

        [Test]
        public void End_OfSupersededAttempt_IsNoop()
        {
            var flow = NewFlow();

            var cts1 = flow.BeginConnectAttempt(CancellationToken.None);
            var cts2 = flow.BeginConnectAttempt(CancellationToken.None);

            // Identity-guard: ending the superseded attempt must not clear the live attempt's state.
            flow.EndConnectAttempt(cts1);
            Assert.IsTrue(flow.IsConnecting, "superseded End must not clear the live attempt's gating");
            Assert.IsFalse(cts2.Token.IsCancellationRequested, "live attempt left untouched");

            // The current attempt clears.
            flow.EndConnectAttempt(cts2);
            Assert.IsFalse(flow.IsConnecting);
        }

        [Test]
        public void CancelConnect_CancelsCurrentOnly_AndIsSafeWhenNone()
        {
            var flow = NewFlow();
            Assert.DoesNotThrow(() => flow.CancelConnect(), "no in-flight attempt");

            var cts = flow.BeginConnectAttempt(CancellationToken.None);
            flow.CancelConnect();
            Assert.IsTrue(cts.Token.IsCancellationRequested);

            flow.EndConnectAttempt(cts);
            Assert.DoesNotThrow(() => flow.CancelConnect(), "already cleared -> no-op");
        }

        [Test]
        public void DisposeConnect_IsIdempotent_AndSafe()
        {
            var flow = NewFlow();
            Assert.DoesNotThrow(() => flow.DisposeConnect(), "nothing in-flight");

            flow.BeginConnectAttempt(CancellationToken.None);
            Assert.DoesNotThrow(() => flow.DisposeConnect());
            Assert.DoesNotThrow(() => flow.DisposeConnect(), "second call is a no-op");
        }
    }
}
