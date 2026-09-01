namespace xpTURN.Klotho
{
    /// <summary>
    /// Policy that determines which frame source to bind against when creating a view.
    ///
    /// <b>On the EVU spawn path the Factory decides this outright.</b>
    /// <c>EntityViewFactory.TryGetBindBehaviour</c>'s answer is assigned wholesale, which is safe because
    /// it covers the whole enum — nothing is lost by overwriting. The consequence is that a value
    /// serialized on a prefab is <b>discarded there</b>; it only takes effect on creation paths that
    /// bypass EVU (a view wired directly, see <c>EntityView</c>). Contrast <see cref="ViewFlags"/>: a
    /// bitfield the Factory only partly decides, and therefore merges instead of replacing.
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
