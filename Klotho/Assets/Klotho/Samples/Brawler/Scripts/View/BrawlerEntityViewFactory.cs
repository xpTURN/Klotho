using Cysharp.Threading.Tasks;
using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// EntityViewFactory implementation for the Brawler sample.
    /// Determines per-entity BindBehaviour and ViewFlags based on combinations of 5 flags:
    /// Mode, IsServer, IsReplayMode, IsSpectatorMode, OwnerId ↔ LocalPlayerId.
    ///
    /// Per mode/entity decisions:
    ///   P2P / SD-Server / Replay : all entities NonVerified + None
    ///   SD-Client                : local player NonVerified + None / remote players & NPCs Verified + SnapshotInterpolation
    ///   Spectator                : all entities Verified + SnapshotInterpolation
    ///
    /// Engine queries are not performed in the constructor or OnEnable. Engine queries are only allowed inside TryGetBindBehaviour / GetViewFlags / CreateAsync.
    /// </summary>
    [CreateAssetMenu(menuName = "Brawler/EntityViewFactory", fileName = "BrawlerEntityViewFactory")]
    public class BrawlerEntityViewFactory : EntityViewFactory
    {
        [Header("Character Prefabs (CharacterClass index)")]
        [Tooltip("[0]=Warrior  [1]=Mage  [2]=Rogue  [3]=Knight")]
        [SerializeField] private GameObject[] _characterPrefabs;

        [Header("Item Prefabs (ItemType index)")]
        [Tooltip("[0]=Shield  [1]=Boost  [2]=Bomb")]
        [SerializeField] private GameObject[] _itemPrefabs;

        // ── BindBehaviour / ViewFlags decision ──

        public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
        {
            // Brawler renders only Character / Item via EVU. Others (SpawnMarker, etc.) are skipped.
            bool isCharacter = frame.Has<CharacterComponent>(entity);
            bool isItem      = frame.Has<ItemComponent>(entity);
            if (!isCharacter && !isItem)
            {
                behaviour = BindBehaviour.Verified;
                return false;
            }

            // Player-owned entity — distinguish local/remote by OwnerComponent.OwnerId
            if (frame.Has<OwnerComponent>(entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                behaviour = IsPredictedRender(owner.OwnerId)
                    ? BindBehaviour.NonVerified
                    : BindBehaviour.Verified;
                return true;
            }

            // Server-authoritative entity (Item, etc.) — no Owner, bound to Verified on SD-Client/Spectator.
            bool useVerified = UseVerifiedPath() && !Engine.IsReplayMode;
            behaviour = useVerified ? BindBehaviour.Verified : BindBehaviour.NonVerified;
            return true;
        }

        public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
        {
            bool hasOwner = frame.Has<OwnerComponent>(entity);
            int  ownerId  = hasOwner ? frame.GetReadOnly<OwnerComponent>(entity).OwnerId : -1;

            bool useVerifiedPath = UseVerifiedPath() && !Engine.IsReplayMode;
            bool predictedRender = hasOwner
                ? !useVerifiedPath || (ownerId == Engine.LocalPlayerId)
                : !useVerifiedPath;

            return predictedRender ? ViewFlags.None : ViewFlags.EnableSnapshotInterpolation;
        }

        // 5-flag decision core — only for entities with Owner.
        private bool IsPredictedRender(int ownerId)
        {
            // If not SD-Client / Spectator, always predicted (prioritize local responsiveness).
            // Replay is also predicted (all ticks are verified → Predicted ↔ PredictedPrevious is sufficient as lerp reference).
            // Only on SD-Client / Spectator: local player = Predicted, remote player = Verified.
            return !UseVerifiedPath() || Engine.IsReplayMode || (ownerId == Engine.LocalPlayerId);
        }

        private bool UseVerifiedPath()
        {
            bool isSDClient = (Engine.SimulationConfig.Mode == NetworkMode.ServerDriven) && !Engine.IsServer;
            return isSDClient || Engine.IsSpectatorMode;
        }

        // ── Prefab instantiation (Pool integration) ──

        public override async UniTask<EntityView> CreateAsync(
            Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
        {
            var prefab = ResolvePrefab(frame, entity);
            if (prefab == null) return null;

            EntityView view;
            if (Pool != null)
            {
                view = await Pool.Rent(prefab);
            }
            else
            {
                var go = Object.Instantiate(prefab);
                view = go.GetComponent<EntityView>();
                if (view == null)
                {
                    Object.Destroy(go);
                    return null;
                }
            }
            return view;
        }

        public override void Destroy(EntityView view)
        {
            if (view == null) return;
            if (Pool != null) Pool.Return(view);
            else Object.Destroy(view.gameObject);
        }

        private GameObject ResolvePrefab(Frame frame, EntityRef entity)
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
    }
}
