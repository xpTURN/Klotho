namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Single selection point for the production Ed25519 backend. Runtime call sites obtain the backend from
    /// <see cref="Default"/>, so the production implementation is chosen in one place rather than at every
    /// construction site.
    /// </summary>
    public static class Ed25519Backends
    {
        /// <summary>Production Ed25519 backend used by runtime call sites (Atropos). It is stateless and
        /// thread-safe, so a single shared instance is exposed.</summary>
        public static readonly IEd25519Backend Default = new PureEd25519Backend();
    }
}
