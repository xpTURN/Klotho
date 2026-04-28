using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace xpTURN.Klotho
{
    /// <summary>
    /// A simple view pool keyed by prefab instance ID.
    /// If an inactive view exists in the pool, it is reactivated and returned synchronously; otherwise, it is instantiated immediately.
    /// The first OnInitialize call is handled by the EVU side.
    /// </summary>
    public class DefaultEntityViewPool : MonoBehaviour, IEntityViewPool
    {
        [SerializeField] private Transform _poolRoot;

        // prefab instanceID → idle view queue
        private readonly Dictionary<int, Queue<EntityView>> _pools = new();
        // active view → source prefab instanceID (for correct return)
        private readonly Dictionary<EntityView, int>        _sourcePrefabId = new();
        // instanceID → prefab reference (for hit reuse active SetActive)
        private readonly Dictionary<int, GameObject>        _prefabById = new();

        private Transform PoolRoot => _poolRoot != null ? _poolRoot : transform;

        public UniTask<EntityView> Rent(GameObject prefab, Vector3? pos = null, Quaternion? rot = null)
        {
            if (prefab == null) return UniTask.FromResult<EntityView>(null);

            int id = prefab.GetInstanceID();
            _prefabById[id] = prefab;

            if (_pools.TryGetValue(id, out var queue) && queue.Count > 0)
            {
                var view = queue.Dequeue();
                ActivateFromPool(view, id, pos, rot);
                return UniTask.FromResult(view);   // hit — synchronous return
            }

            // miss — synchronous instantiation.
            var go = pos.HasValue
                ? Instantiate(prefab, pos.Value, rot ?? Quaternion.identity)
                : Instantiate(prefab);
            var newView = go.GetComponent<EntityView>();
            if (newView == null)
            {
                Destroy(go);
                return UniTask.FromResult<EntityView>(null);
            }
            _sourcePrefabId[newView] = id;
            return UniTask.FromResult(newView);
        }

        public void Return(EntityView view)
        {
            if (view == null) return;

            if (!_sourcePrefabId.TryGetValue(view, out int id))
            {
                // Created outside the pool — just Destroy
                Destroy(view.gameObject);
                return;
            }

            view.gameObject.SetActive(false);
            view.transform.SetParent(PoolRoot, worldPositionStays: false);

            if (!_pools.TryGetValue(id, out var queue))
            {
                queue = new Queue<EntityView>();
                _pools[id] = queue;
            }
            queue.Enqueue(view);
        }

        public void Prewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0) return;

            int id = prefab.GetInstanceID();
            _prefabById[id] = prefab;

            if (!_pools.TryGetValue(id, out var queue))
            {
                queue = new Queue<EntityView>();
                _pools[id] = queue;
            }

            for (int i = 0; i < count; i++)
            {
                var go = Instantiate(prefab, PoolRoot);
                go.SetActive(false);
                var view = go.GetComponent<EntityView>();
                if (view == null)
                {
                    Destroy(go);
                    continue;
                }
                _sourcePrefabId[view] = id;
                queue.Enqueue(view);
            }
        }

        private void ActivateFromPool(EntityView view, int prefabId, Vector3? pos, Quaternion? rot)
        {
            var t = view.transform;
            t.SetParent(null, worldPositionStays: false);
            if (pos.HasValue) t.position = pos.Value;
            if (rot.HasValue) t.rotation = rot.Value;
            view.gameObject.SetActive(true);
        }
    }
}
