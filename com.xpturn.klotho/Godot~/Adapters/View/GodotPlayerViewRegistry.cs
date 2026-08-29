// Maps playerId -> view for Owner-bearing views.
// The EntityViewUpdaterNode populates this from OwnerComponent on spawn/despawn — game code typically
// only subscribes to the events or calls Get. Pure C# data structure.
//
// This maps a player to the view ON SCREEN, not to a live entity. A snapshot-bound view outlives its
// entity by the despawn grace and stays registered for that whole window — it is still interpolating and
// still the thing a camera should follow. So Get(playerId) can return a view whose entity has already left
// the Verified frame, and OnLocalViewUnregistered fires when the view is destroyed, not when the entity
// dies. Do not read this map as a liveness oracle; ask the frame for that.
//
// IsActuallyLocal disambiguates spectator mode (LocalPlayerId falls back to 0, colliding with the real
// host playerId 0) via IsSpectatorMode.
using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Godot
{
    public sealed class GodotPlayerViewRegistry<TView> where TView : EntityViewNode
    {
        private readonly Dictionary<int, TView> _views;
        private readonly IKlothoEngine _engine;

        public event Action<int, TView> OnViewRegistered;
        public event Action<int, TView> OnViewUnregistered;
        public event Action<TView>      OnLocalViewRegistered;
        public event Action<TView>      OnLocalViewUnregistered;

        public GodotPlayerViewRegistry(IKlothoEngine engine, int capacity)
        {
            _engine = engine;
            _views = new Dictionary<int, TView>(capacity);
        }

        public TView Get(int playerId) => _views.TryGetValue(playerId, out var v) ? v : null;

        // Direct ValueCollection — struct enumerator, no allocation.
        public Dictionary<int, TView>.ValueCollection Values => _views.Values;
        public int Count => _views.Count;

        private bool IsActuallyLocal(int playerId)
            => !_engine.IsSpectatorMode && playerId == _engine.LocalPlayerId;

        internal void Register(int playerId, TView view)
        {
            if (view == null) return;
            if (_views.TryGetValue(playerId, out var existing) && existing == view) return; // same instance — skip

            _views[playerId] = view;
            OnViewRegistered?.Invoke(playerId, view);
            if (IsActuallyLocal(playerId)) OnLocalViewRegistered?.Invoke(view);
        }

        internal void Unregister(int playerId, TView view)
        {
            // Only the currently-mapped view may unregister itself (a rebind may have replaced it).
            if (_views.TryGetValue(playerId, out var current) && current != view) return;
            if (!_views.Remove(playerId)) return;

            OnViewUnregistered?.Invoke(playerId, view);
            if (IsActuallyLocal(playerId)) OnLocalViewUnregistered?.Invoke(view);
        }

        internal void Clear() => _views.Clear();
    }
}
