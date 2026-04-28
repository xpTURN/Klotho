using System.Collections.Generic;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Per-scene static collider load/unload interface.
    /// </summary>
    public interface IStaticColliderService
    {
        void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders);
        void UnloadStaticColliders(string sceneKey);
    }
}
