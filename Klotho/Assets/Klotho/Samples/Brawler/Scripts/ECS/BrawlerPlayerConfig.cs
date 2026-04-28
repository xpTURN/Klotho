using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Per-player custom data for the Brawler sample.
    /// Holds information that must be shared at session start, such as each player's selected character class.
    /// Serialized into PlayerConfigMessage.ConfigData and broadcast,
    /// then queried via KlothoEngine.GetPlayerConfig&lt;BrawlerPlayerConfig&gt;(playerId).
    ///
    /// MessageTypeId uses values beyond the Runtime reserved range (UserDefined_Start=200).
    /// </summary>
    [KlothoSerializable(MessageTypeId = (NetworkMessageType)200)]
    public partial class BrawlerPlayerConfig : PlayerConfigBase
    {
        [KlothoOrder] public int SelectedCharacterClass;
    }
}
