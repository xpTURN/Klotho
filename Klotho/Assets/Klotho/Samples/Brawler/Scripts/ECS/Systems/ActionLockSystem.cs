using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// Decrements ActionLockTicks each tick and fires ActionCompletedEvent the moment it transitions to 0.
    /// Paths that reset immediately (e.g. Death/Knockback) also go through the CompleteAction helper so the event still fires.
    /// </summary>
    public class ActionLockSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref var c = ref frame.Get<CharacterComponent>(entity);
                if (c.ActionLockTicks <= 0) continue;

                int previousActionType = c.ActiveSkillSlot;
                c.ActionLockTicks--;

                // Natural-decay edge: fire ActionCompleted on the N→0 transition
                if (c.ActionLockTicks == 0)
                    RaiseActionCompleted(ref frame, entity, previousActionType, ref c);
            }
        }

        /// <summary>
        /// Common helper used when ActionLockTicks must be set to 0 immediately, such as in Death/Knockback paths.
        /// No-op if ActionLockTicks is already 0; after firing the event, ActiveSkillSlot is also reset to -1.
        /// </summary>
        public static void CompleteAction(ref Frame frame, EntityRef entity)
        {
            ref var c = ref frame.Get<CharacterComponent>(entity);
            if (c.ActionLockTicks <= 0) return;

            int previousActionType = c.ActiveSkillSlot;
            c.ActionLockTicks = 0;
            RaiseActionCompleted(ref frame, entity, previousActionType, ref c);
        }

        private static void RaiseActionCompleted(
            ref Frame frame, EntityRef entity, int previousActionType, ref CharacterComponent c)
        {
            c.ActiveSkillSlot = -1;

            if (frame.EventRaiser == null) return;
            var evt = EventPool.Get<ActionCompletedEvent>();
            evt.Actor = entity;
            evt.PreviousActionType = previousActionType;
            frame.EventRaiser.RaiseEvent(evt);
        }
    }
}
