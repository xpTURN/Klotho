using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Empty command (no input).
    /// </summary>
    [KlothoSerializable(0)]
    public partial class EmptyCommand : CommandBase
    {
        public EmptyCommand() : base() { }
        public EmptyCommand(int playerId, int tick) : base(playerId, tick) { }
    }
}
