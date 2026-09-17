using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace xpTURN.Klotho
{
    /// <summary>
    /// A simple view pool keyed by prefab.
    /// If an inactive view exists in the pool, it is reactivated and returned synchronously; otherwise, it is instantiated immediately.
    /// The first OnInitialize call is handled by the EVU side.
    /// </summary>
    public class DefaultEntityViewPool : MonoBehaviour, IEntityViewPool
    {
        [SerializeField] private Transform _poolRoot;

        // prefab → idle view queue
        private readonly Dictionary<GameObject, Queue<EntityView>> _pools = new();
        // active view → source prefab (for correct return)
        private readonly Dictionary<EntityView, GameObject>        _sourcePrefab = new();

        private Transform PoolRoot => _poolRoot != null ? _poolRoot : transform;

        public UniTask<EntityView> Rent(GameObject prefab, Vector3? pos = null, Quaternion? rot = null)
        {
            if (prefab == null) return UniTask.FromResult<EntityView>(null);

            if (_pools.TryGetValue(prefab, out var queue) && queue.Count > 0)
            {
                var view = queue.Dequeue();
                ActivateFromPool(view, pos, rot);
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
            _sourcePrefab[newView] = prefab;
            return UniTask.FromResult(newView);
        }

        public void Return(EntityView view)
        {
            if (view == null) return;

            if (!_sourcePrefab.TryGetValue(view, out var prefab))
            {
                // Created outside the pool — just Destroy
                Destroy(view.gameObject);
                return;
            }

            view.gameObject.SetActive(false);
            view.transform.SetParent(PoolRoot, worldPositionStays: false);

            if (!_pools.TryGetValue(prefab, out var queue))
            {
                queue = new Queue<EntityView>();
                _pools[prefab] = queue;
            }
            queue.Enqueue(view);
        }

        public void Prewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0) return;

            if (!_pools.TryGetValue(prefab, out var queue))
            {
                queue = new Queue<EntityView>();
                _pools[prefab] = queue;
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
                _sourcePrefab[view] = prefab;
                queue.Enqueue(view);
            }
        }

        private void ActivateFromPool(EntityView view, Vector3? pos, Quaternion? rot)
        {
            var t = view.transform;
            t.SetParent(null, worldPositionStays: false);
            if (pos.HasValue) t.position = pos.Value;
            if (rot.HasValue) t.rotation = rot.Value;
            view.gameObject.SetActive(true);
        }
    }
}
