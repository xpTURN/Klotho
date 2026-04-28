using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS.Systems
{
    public class CommandSystem : ICommandSystem
    {
        public void OnCommand(ref Frame frame, ICommand command)
        {
            if (command is MoveCommand moveCmd)
            {
                HandleMoveCommand(ref frame, moveCmd);
            }
        }

        private static void HandleMoveCommand(ref Frame frame, MoveCommand moveCmd)
        {
            var filter = frame.Filter<OwnerComponent, MovementComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId != moveCmd.PlayerId)
                    continue;

                ref var movement = ref frame.Get<MovementComponent>(entity);
                movement.TargetPosition = moveCmd.Target;
                movement.IsMoving = true;
            }
        }
    }
}
