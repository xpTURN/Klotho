namespace xpTURN.Klotho
{
    /// <summary>
    /// Policy that determines which frame source to bind against when creating a view.
    /// The prefab Inspector value is the default; Factory can override it at runtime.
    /// </summary>
    public enum BindBehaviour
    {
        /// <summary>
        /// Creates the view as soon as it appears in the Predicted frame.
        /// Used for local players and immediately-responsive entities (e.g. projectiles).
        /// </summary>
        NonVerified,

        /// <summary>
        /// Creates the view only when it appears in the Verified frame.
        /// Used for remote players and to avoid create/destroy during misprediction.
        /// </summary>
        Verified,
    }
}
