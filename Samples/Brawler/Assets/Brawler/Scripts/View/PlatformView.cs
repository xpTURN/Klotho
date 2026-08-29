using xpTURN.Klotho;

namespace Brawler
{
    /// <summary>
    /// Moving platform view. The scene instance is adopted by <see cref="BrawlerEntityViewFactory"/>
    /// instead of being instantiated from a prefab (GameDevWorkflow Step 7, "Adopting a scene-placed
    /// object as a View"), so EVU owns the lifecycle and the base EntityView pipeline drives the
    /// transform — interpolation, teleport handling and version-safe frame lookups all come from there.
    /// The type itself is the adoption marker the Factory branches on.
    /// </summary>
    public class PlatformView : EntityView
    {
    }
}
