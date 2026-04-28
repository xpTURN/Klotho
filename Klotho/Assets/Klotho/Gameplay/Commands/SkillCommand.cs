using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Skill use command. Contains skill ID, target entity, and target position.
    /// </summary>
    [KlothoSerializable(3)]
    public partial class SkillCommand : CommandBase
    {
        [KlothoOrder]
        public int SkillId { get; set; }

        [KlothoOrder]
        public int TargetEntityId { get; set; }

        [KlothoOrder]
        public FPVector3 Target { get; set; }

        public SkillCommand() : base() { }

        public SkillCommand(int playerId, int tick, int skillId, int targetEntityId = -1)
            : base(playerId, tick)
        {
            SkillId = skillId;
            TargetEntityId = targetEntityId;
        }
    }
}
