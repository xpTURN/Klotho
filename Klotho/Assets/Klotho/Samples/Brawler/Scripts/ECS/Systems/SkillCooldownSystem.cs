using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    public class SkillCooldownSystem : ISystem
    {
        readonly EventSystem _events;

        public SkillCooldownSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<SkillCooldownComponent>();
            while (filter.Next(out var entity))
            {
                ref var cooldown = ref frame.Get<SkillCooldownComponent>(entity);

                if (cooldown.Skill0Cooldown > 0)
                    cooldown.Skill0Cooldown--;

                if (cooldown.Skill1Cooldown > 0)
                    cooldown.Skill1Cooldown--;

                if (cooldown.ShieldTicks > 0)
                    cooldown.ShieldTicks--;

                if (cooldown.BoostTicks > 0)
                    cooldown.BoostTicks--;
            }

            var charFilter = frame.Filter<CharacterComponent>();
            while (charFilter.Next(out var entity))
            {
                ref var ch = ref frame.Get<CharacterComponent>(entity);
                if (ch.HitReactionTicks > 0)
                    ch.HitReactionTicks--;
            }
        }
    }
}
