namespace xpTURN.Klotho.ECS
{
    public interface ISignal { }

    public delegate void SignalInvoker<TSignal>(TSignal signal, ref Frame frame)
        where TSignal : class, ISignal;

    public interface ISignalOnComponentAdded<T> where T : unmanaged, IComponent
    {
        void OnAdded(ref Frame frame, EntityRef entity, ref T component);
    }

    public interface ISignalOnComponentRemoved<T> where T : unmanaged, IComponent
    {
        void OnRemoved(ref Frame frame, EntityRef entity, T component);
    }
}
