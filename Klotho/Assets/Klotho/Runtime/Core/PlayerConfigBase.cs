using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Base class for per-player custom data.
    /// Game code inherits this to define character selection, team, etc.
    /// Inherits from NetworkMessageBase to reuse the existing serialization pipeline ([KlothoSerializable] + Source Generator).
    /// </summary>
    public abstract class PlayerConfigBase : NetworkMessageBase
    {
    }
}
