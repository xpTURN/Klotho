using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// EntityViewFactory implementation for the Brawler sample.
    /// Provides only the game-specific decisions (which entities to render, which prefab to use).
    /// BindBehaviour / ViewFlags / Pool integration are handled by the base class.
    ///
    /// Moving platforms are the exception: their visual lives in the scene, so this factory adopts the
    /// placed instance rather than instantiating one (GameDevWorkflow Step 7).
    /// </summary>
    [CreateAssetMenu(menuName = "Brawler/EntityViewFactory", fileName = "BrawlerEntityViewFactory")]
    public class BrawlerEntityViewFactory : EntityViewFactory
    {
        [field: Header("Character Prefabs (CharacterClass index)")]
        [Tooltip("[0]=Warrior  [1]=Mage  [2]=Rogue  [3]=Knight")]
        [SerializeField] private GameObject[] _characterPrefabs;

        [field: Header("Item Prefabs (ItemType index)")]
        [Tooltip("[0]=Shield  [1]=Boost  [2]=Bomb")]
        [SerializeField] private GameObject[] _itemPrefabs;

        // Scene-placed platform views, handed over at session start by BrawlerViewSync. A
        // ScriptableObject cannot serialize scene references, so this is runtime-only state — and it
        // survives play sessions in the editor, which is why BindPlacedPlatforms rebuilds it from
        // scratch every time rather than appending.
        private readonly List<PlatformView> _availablePlatforms = new();

        // Whether this session has ANY placed platform to adopt. Fixed at BindPlacedPlatforms and not
        // touched afterwards — deliberately NOT "how many are left". Gating on the remaining count makes
        // the render decision flip the moment the last one is adopted, and EVU reads that decision every
        // tick: the entity drops out of CollectPresent, DestroyStale sees it missing and destroys the live
        // view, Destroy hands it back, the count goes up, and the next tick spawns it again — a
        // spawn/destroy oscillation that is worse than the silent retry it was meant to fix (IMP105 C-11).
        private bool _hasPlacedPlatforms;

        // One warning per session for "a platform entity wanted a view and there was none left".
        private bool _warnedNoPlacedPlatform;

        /// <summary>
        /// Hands the scene's platform views to the factory. Called once per session, before the first
        /// Reconcile. Passing null or an array of nulls simply leaves nothing to adopt — platform
        /// entities then get no view, which is what happens when the scene wiring is missing.
        /// </summary>
        public void BindPlacedPlatforms(PlatformView[] placed)
        {
            _availablePlatforms.Clear();
            _warnedNoPlacedPlatform = false;
            if (placed != null)
                for (int i = 0; i < placed.Length; i++)
                    if (placed[i] != null) _availablePlatforms.Add(placed[i]);
            _hasPlacedPlatforms = _availablePlatforms.Count > 0;
        }

        protected override bool ShouldRender(Frame frame, EntityRef entity)
        {
            return frame.Has<CharacterComponent>(entity)
                || frame.Has<ItemComponent>(entity)
                || frame.Has<PlatformComponent>(entity);
        }

        protected override GameObject ResolvePrefab(Frame frame, EntityRef entity)
        {
            if (frame.Has<CharacterComponent>(entity) && _characterPrefabs != null && _characterPrefabs.Length > 0)
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                int idx = Mathf.Clamp(c.CharacterClass, 0, _characterPrefabs.Length - 1);
                return _characterPrefabs[idx];
            }
            if (frame.Has<ItemComponent>(entity) && _itemPrefabs != null && _itemPrefabs.Length > 0)
            {
                ref readonly var i = ref frame.GetReadOnly<ItemComponent>(entity);
                int idx = Mathf.Clamp(i.ItemType, 0, _itemPrefabs.Length - 1);
                return _itemPrefabs[idx];
            }
            return null;
        }

        /// <summary>
        /// Platforms render on the predicted timeline, not the verified one. The local character is
        /// client-predicted, so a platform drawn from the Verified window would lag it by the render
        /// delay and slide under the player's feet while they stand on it. Paired with
        /// <see cref="GetViewFlags"/> below — EVU evaluates the two independently, so a view whose
        /// binding says predicted but whose flags say snapshot would take its lifetime from one
        /// timeline and its pose from the other.
        /// </summary>
        public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
        {
            if (frame.Has<PlatformComponent>(entity))
            {
                // Nothing placed in the scene this session means the game asked for no platform visual at
                // all, so the entity is not a render candidate. Saying "yes" here instead put EVU in a
                // permanent loop: CreateAsync finds no instance to adopt, falls through to the base path,
                // ResolvePrefab has no platform branch and returns null, EVU discards the spawn and
                // re-dispatches on the very next tick — forever, and without a single log line to say why
                // the platform is invisible (IMP105 C-11). The answer is stable for the whole session, so
                // it cannot oscillate the way a remaining-count gate would.
                if (!_hasPlacedPlatforms)
                {
                    behaviour = BindBehaviour.Verified;
                    return false;
                }
                behaviour = BindBehaviour.NonVerified;
                return true;
            }
            return base.TryGetBindBehaviour(frame, entity, out behaviour);
        }

        /// <summary>The other half of the timeline decision above — no snapshot interpolation for platforms.</summary>
        public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
        {
            if (frame.Has<PlatformComponent>(entity)) return ViewFlags.None;
            return base.GetViewFlags(frame, entity);
        }

        /// <summary>
        /// Adopts a scene-placed view for platform entities; everything else goes through the base
        /// instantiate/pool path. Re-activation is the factory's job because EVU never touches
        /// SetActive — <see cref="Destroy"/> below is what deactivated it.
        /// </summary>
        public override UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
        {
            if (frame.Has<PlatformComponent>(entity))
            {
                if (TryTakePlacedPlatform(out var placed))
                {
                    // Adopting bypasses base.CreateAsync, and with it the spawn-pose write. The scene
                    // authored this object wherever the level designer put it, which is not where the
                    // entity is — and a view that never runs the position line (DisableUpdate /
                    // DisablePositionUpdate) would stay there forever. Platforms here do run it, so this
                    // only closes the gap before the first ApplyTransform, but the asymmetry is the
                    // defect: every other spawn path already places the view (IMP105 C-13).
                    if (TryGetSpawnPose(frame, entity, out Vector3 spawnPos, out Quaternion spawnRot))
                        placed.transform.SetPositionAndRotation(spawnPos, spawnRot);
                    placed.gameObject.SetActive(true);
                    return UniTask.FromResult<EntityView>(placed);
                }

                // Reachable only with fewer placed views than platform entities — the all-or-nothing case
                // is already refused in TryGetBindBehaviour. Without this the platform is simply absent
                // and nothing says so; the base path below cannot help either, since ResolvePrefab has no
                // platform branch to fall back on.
                if (!_warnedNoPlacedPlatform)
                {
                    _warnedNoPlacedPlatform = true;
                    Engine?.Logger?.KWarning(
                        $"[Brawler] a platform entity has no scene PlatformView left to adopt (placed={_availablePlatforms.Count} exhausted) — it will have no visual. Place one MovingPlatform per platform entity in the stage scene.");
                }
            }
            return base.CreateAsync(frame, entity, behaviour, flags);
        }

        /// <summary>
        /// Returns adopted views to the placed pool instead of destroying them. Overriding this is not
        /// optional: the base implementation hands the view to the Pool, and DefaultEntityViewPool
        /// destroys anything it did not itself rent out — which would delete the scene object.
        /// </summary>
        public override void Destroy(EntityView view)
        {
            if (view is PlatformView placed)
            {
                if (placed != null)
                {
                    placed.gameObject.SetActive(false);
                    if (!_availablePlatforms.Contains(placed)) _availablePlatforms.Add(placed);
                }
                return;
            }
            base.Destroy(view);
        }

        private bool TryTakePlacedPlatform(out PlatformView view)
        {
            // Entries can go null between sessions (scene reload), so skip them rather than handing
            // EVU a destroyed object.
            while (_availablePlatforms.Count > 0)
            {
                int last = _availablePlatforms.Count - 1;
                view = _availablePlatforms[last];
                _availablePlatforms.RemoveAt(last);
                if (view != null) return true;
            }
            view = null;
            return false;
        }
    }
}
