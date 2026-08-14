using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Deterministic building removal. TargetEntityId = EntityRef.ToId(), so it carries index
    /// AND version.
    ///
    /// <para>The version is only worth carrying if the handler reads it, and reading it takes a
    /// deliberate call: <c>Frame.Has</c> forwards <c>entity.Index</c> alone, so it cannot tell a
    /// stale reference from the new building that took over the slot. The handler therefore gates
    /// on <c>Frame.Entities.IsAlive</c> — which does compare the version — before
    /// <c>Frame.Has</c>. Without that, a stale id resolved to whatever now occupies the slot and
    /// removed THAT, identically on every peer and with nothing reporting it
    /// (PlatformerCommandSystem.HandleRemoveBuilding).</para>
    /// </summary>
    [KlothoSerializable(116)]
    public partial class RemoveBuildingCommand : CommandBase, IReliableCommand
    {
        [KlothoOrder(0)] public long TargetEntityId;
        [KlothoOrder(1)] public int SequenceNumber { get; set; }

        public int OrderKey => 1;
    }
}
