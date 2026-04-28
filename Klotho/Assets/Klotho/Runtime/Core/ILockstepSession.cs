namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho session interface.
    /// An instance representing the combination of engine + simulation + network.
    /// </summary>
    public interface IKlothoSession
    {
        KlothoEngine Engine { get; }
        ECS.EcsSimulation Simulation { get; }
        int LocalPlayerId { get; }
        KlothoState State { get; }

        void Update(float deltaTime);
        void InputCommand(ICommand command);
        void Stop();
    }
}
