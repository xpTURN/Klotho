using System;
using UnityEngine;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho
{
    public class UKlothoBehaviour : MonoBehaviour
    {
        private IKlothoSession _session;
        private long _lastTicks;

        public IKlothoSession Session => _session;

        public void Bind(IKlothoSession session)
        {
            _session = session;
        }

        private void Update()
        {
            if (_session == null) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float dt = (_lastTicks > 0) ? (now - _lastTicks) * 0.001f : 0f;
            _lastTicks = now;

            _session.Update(dt);
        }

        private void OnDestroy()
        {
            _session?.Stop();
        }
    }
}
