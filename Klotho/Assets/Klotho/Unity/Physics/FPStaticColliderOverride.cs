using UnityEngine;

namespace xpTURN.Klotho.Unity.Physics
{
    [RequireComponent(typeof(Collider))]
    public class FPStaticColliderOverride : MonoBehaviour
    {
        [Tooltip("Explicit FPStaticCollider.id — 0 uses auto-assignment")]
        public int id;

        [Tooltip("Coefficient of restitution — 0: no bounce, 1: perfectly elastic")]
        public float restitution;

        [Tooltip("Coefficient of friction")]
        public float friction;
    }
}
