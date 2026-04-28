using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    public enum SystemPhase
    {
        PreUpdate,
        Update,
        PostUpdate,
        LateUpdate
    }

    public interface ISystem
    {
        void Update(ref Frame frame);
    }

    public interface IInitSystem
    {
        void OnInit(ref Frame frame);
    }

    public interface IDestroySystem
    {
        void OnDestroy(ref Frame frame);
    }

    public interface ICommandSystem
    {
        void OnCommand(ref Frame frame, ICommand command);
    }

    public interface IEntityCreatedSystem
    {
        void OnEntityCreated(ref Frame frame, EntityRef entity);
    }

    public interface IEntityDestroyedSystem
    {
        void OnEntityDestroyed(ref Frame frame, EntityRef entity);
    }

    public interface ISyncEventSystem
    {
        void EmitSyncEvents(ref Frame frame);
    }
}
