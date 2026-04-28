using xpTURN.Klotho;

namespace Brawler
{
    /// <summary>
    /// Item view for Shield/Boost/Bomb, etc.
    /// Since TransformComponent does not change after initial placement, rendering is possible with only the EntityView default pipeline.
    /// BindBehaviour/ViewFlags are decided and overridden by BrawlerEntityViewFactory.
    /// </summary>
    public class ItemView : EntityView
    {
    }
}
