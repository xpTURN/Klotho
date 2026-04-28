namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Interface for sending commands from within the OnPollInput callback.
    /// Thin wrapper around Engine.InputCommand().
    /// </summary>
    public interface ICommandSender
    {
        void Send(ICommand command);
    }
}
