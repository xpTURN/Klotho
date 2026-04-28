using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Event raised when a character's Attack/Skill action completes (ActionLockTicks transitions to 0).
    /// Because EventMode.Synced is used, it is dispatched exactly once at the verified point so the VFX Stop handler is not invoked twice.
    ///
    /// Actor is the entity whose action ended; PreviousActionType is the value of CharacterComponent.ActiveSkillSlot just before completion
    /// (-1 = Attack, 0 = Skill0, 1 = Skill1), and the VFX handler uses it to determine which effect to stop.
    ///
    /// There are two raise paths, and the raise point must be identical across reset paths.
    ///   (a) Natural decay: when ActionLockTicks transitions 1→0 in ActionLockSystem
    ///   (b) Forced reset: when ActionLockTicks is immediately set to 0 by Death/Knockback, etc.
    ///                     also raised through the ActionLockSystem.CompleteAction helper to keep the path consistent
    /// </summary>
    [KlothoSerializable(112)]
    public partial class ActionCompletedEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder] public EntityRef Actor;

        /// <summary>ActiveSkillSlot just before completion (-1=Attack, 0=Skill0, 1=Skill1).</summary>
        [KlothoOrder] public int PreviousActionType;
    }
}
