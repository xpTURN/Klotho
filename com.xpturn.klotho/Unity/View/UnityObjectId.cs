using UnityEngine;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Process-local identifier of a Unity object for diagnostic logs (view lifetime / binding traces).
    /// Not a serialization or comparison contract: the value is the raw <c>EntityId</c> on Unity 6000.4+
    /// and the sign-extended instance ID before that, so it differs across editor versions.
    /// Returns 0 only for a null reference; a destroyed object still yields its last id.
    /// </summary>
    public static class UnityObjectId
    {
        public static long Of(Object o)
        {
            if ((object)o == null) return 0;
#if UNITY_6000_4_OR_NEWER
            return unchecked((long)EntityId.ToULong(o.GetEntityId()));
#else
            return o.GetInstanceID();
#endif
        }
    }
}
