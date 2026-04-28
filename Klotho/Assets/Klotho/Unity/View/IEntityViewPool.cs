using Cysharp.Threading.Tasks;
using UnityEngine;

namespace xpTURN.Klotho
{
    /// <summary>
    /// EntityView pool interface.
    ///
    /// Lifecycle:
    ///   Rent → (first time only) OnInitialize → OnActivate(frame) → ...
    ///   Return → OnDeactivate → back to pool
    ///   Re-rent → only OnActivate(frame) runs (OnInitialize is skipped)
    ///
    /// Rent returns synchronously if instances remain in the pool, otherwise takes the async load path.
    /// Either way, EVU checks the entity version to filter out entities that were destroyed before async completes.
    /// </summary>
    public interface IEntityViewPool
    {
        /// <summary>Rents one view matching <paramref name="prefab"/> from the pool. hit=synchronous / miss=asynchronous.</summary>
        UniTask<EntityView> Rent(GameObject prefab, Vector3? pos = null, Quaternion? rot = null);

        /// <summary>Returns a used view to the pool. Falls back to Destroy on failure.</summary>
        void Return(EntityView view);

        /// <summary>Pre-instantiates <paramref name="count"/> instances (Addressables warmup).</summary>
        void Prewarm(GameObject prefab, int count);
    }
}
