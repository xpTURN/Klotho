using System;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// Source of the per-frame dt that drives the session.
    /// <see cref="WallClock"/> measures real elapsed time (engine-independent); <see cref="EngineFrame"/>
    /// uses Unity's <c>Time.deltaTime</c> (honors timeScale / engine frame pacing).
    /// </summary>
    public enum SessionDtSource { WallClock, EngineFrame }

    /// <summary>
    /// MonoBehaviour adapter that drives a KlothoSession through Unity's Update loop and exposes
    /// hooks for pre/post-Update logic and Stop teardown. Game code attaches a session via
    /// <see cref="Attach"/> and tears down through <see cref="DetachAndStop"/>. The driver also owns
    /// the bound main transport: it pumps it while no session is attached (idle) and routes idle
    /// disconnects to <see cref="IKlothoSessionObserver.OnIdleDisconnected"/> (see <see cref="BindTransport"/>).
    ///
    /// Hook semantics:
    ///   PreSessionUpdate / PostSessionUpdate — steady-state. Exceptions propagate (no wrap).
    ///   Stopping — lifecycle transition. Wrapped in try/finally so Session.Stop is guaranteed
    ///   even if a subscriber throws.
    ///
    /// Multi-cast invocation follows C# default: first throw in the invocation list stops
    /// subsequent subscribers from firing.
    /// </summary>
    public sealed class KlothoSessionDriver : MonoBehaviour
    {
        public KlothoSession Session { get; private set; }

        // dt source for the session step. Default: WallClock (engine-independent real time).
        public SessionDtSource DtSource { get; set; } = SessionDtSource.WallClock;

        public event Action<KlothoSession, float> PreSessionUpdate;
        public event Action<KlothoSession, float> PostSessionUpdate;
        public event Action<KlothoSession> Stopping;

        long _lastTicks;
        bool _stopping;

        // Game-owned main transport plus the lifecycle observer and flow it belongs to. The driver
        // pumps this transport while no session is attached (idle) and routes idle disconnects through
        // the observer; the flow exposes whether a connect handshake is in flight.
        INetworkTransport _transport;
        IKlothoSessionObserver _observer;
        KlothoSessionFlow _flow;

        public void Attach(KlothoSession session)
        {
            Session = session;
            _lastTicks = 0;
        }

        // Bind the main transport once, before the first session is created, so the driver owns
        // its idle pumping and disconnect routing. Subscribing here (before any session, and thus
        // before NetworkService) makes the driver observe a disconnect's pre-transition Phase.
        public void BindTransport(INetworkTransport transport, IKlothoSessionObserver observer, KlothoSessionFlow flow)
        {
            _transport = transport;
            _observer = observer;
            _flow = flow;
            if (transport != null) transport.OnDisconnected += OnTransportDisconnected;
        }

        // Single transport-level chokepoint for the main transport's disconnect (client-semantics — a
        // host receives OnPeerDisconnected instead). Covers P2P and ServerDriven guests uniformly.
        // Never stops the session inline: while a session is live this fires inside Engine.Update's
        // PollEvents, so an inline Stop would re-enter the running Update. Non-Playing drops request a
        // deferred stop instead; the session ends after the current Update returns.
        void OnTransportDisconnected(DisconnectReason reason)
        {
            if (_flow != null && _flow.IsConnecting) return;                        // handshake — entry point owns it
            if (Session == null) { _observer?.OnIdleDisconnected(reason); return; } // genuinely idle — UI reset

            // Session present: only a host/guest session uses the main transport (NetworkService != null).
            // A spectator session (NetworkService == null) is driven by its own transport, so a main-transport
            // drop is irrelevant to it. Stop only a non-Playing host/guest session; Playing → no-op (the
            // NetworkService runs its own auto-reconnect).
            var ns = Session.NetworkService;
            if (ns != null && ns.Phase != SessionPhase.Playing)
                Session.RequestClientShutdown();
        }

        public void DetachAndStop(bool keepReconnectCredentials = false, bool saveReplay = true)
        {
            if (_stopping) return;

            var s = Session;
            if (s == null) return;

            _stopping = true;
            try { Stopping?.Invoke(s); }
            finally { s.Stop(keepReconnectCredentials, saveReplay); Session = null; _stopping = false; }
        }

        // The driver follows its session's lifecycle: when a framework-internal Stop (auto-shutdown /
        // spectator-drop) stops the session without going through DetachAndStop, the driver self-detaches
        // here. Fires Stopping so framework diagnostics still self-detach. try/finally mirrors
        // DetachAndStop: Session is nulled even if a Stopping subscriber throws (otherwise the stopped
        // session stays attached and this re-fires every frame).
        void SelfDetach(KlothoSession s)
        {
            try { Stopping?.Invoke(s); }
            finally { Session = null; }
        }

        void Update()
        {
            var s = Session;
            if (s == null)
            {
                _transport?.PollEvents();
                return;
            }

            float dt = DtSource == SessionDtSource.EngineFrame ? Time.deltaTime : WallClockDt();

            PreSessionUpdate?.Invoke(s, dt);
            if (s.IsStopped) { SelfDetach(s); return; }

            s.Update(dt);
            if (s.IsStopped) { SelfDetach(s); return; }

            PostSessionUpdate?.Invoke(s, dt);
        }

        // Real elapsed time since the previous step (engine-independent). 0 on the first step after Attach.
        float WallClockDt()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float dt = (_lastTicks > 0) ? (now - _lastTicks) * 0.001f : 0f;
            _lastTicks = now;
            return dt;
        }

        // Unity teardown — preserve cold-start Reconnect credentials so a relaunch can attempt Reconnect.
        // saveReplay: false — process-exit must not write a replay (matches the prior teardown behavior).
        // The driver owns the main transport's lifetime: it is kept connected across sessions and
        // disconnected only here, on process exit.
        void OnDestroy()
        {
            DetachAndStop(keepReconnectCredentials: true, saveReplay: false);
            if (_transport != null)
            {
                _transport.OnDisconnected -= OnTransportDisconnected;
                _transport.Disconnect();
            }
        }
    }
}
