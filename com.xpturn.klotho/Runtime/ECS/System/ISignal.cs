namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Marker for a custom broadcast interface dispatched through <c>SystemRunner.Signal&lt;TSignal&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Unrelated to the component signals below — <see cref="ISignalOnComponentAdded{T}"/> and
    /// <see cref="ISignalOnComponentRemoved{T}"/> deliberately do NOT derive from this, so
    /// <c>Signal&lt;TSignal&gt;</c> cannot dispatch them (the engine calls them directly from
    /// <c>Frame.Add</c>/<c>Remove</c>).
    /// </remarks>
    public interface ISignal { }

    /// <summary>
    /// Caller-supplied invocation for <c>SystemRunner.Signal&lt;TSignal&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A lambda here usually captures, so each broadcast allocates. Fine for occasional, event-shaped
    /// calls; do not put it on a path that runs every tick.
    /// </remarks>
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

    /// <summary>
    /// Where <see cref="Frame"/> sends component signals. <c>SystemRunner</c> implements it (its existing
    /// routing methods already match these signatures) and <c>EcsSimulation.Initialize</c> hands the live
    /// frame its runner.
    /// </summary>
    /// <remarks>
    /// A frame whose sink is <c>null</c> fires nothing. That is what keeps every non-executing Frame
    /// instance silent — ring slots and the sync-test buffer are state containers, never get a sink, and
    /// <c>Frame.CopyFrom</c> does not copy the field.
    /// </remarks>
    internal interface IComponentSignalSink
    {
        void OnComponentAdded<T>(ref Frame frame, EntityRef entity, ref T component)
            where T : unmanaged, IComponent;

        void OnComponentRemoved<T>(ref Frame frame, EntityRef entity, T component)
            where T : unmanaged, IComponent;
    }

    /// <summary>
    /// Per-typeId "does anyone listen" bits, so <c>Frame.Add</c>/<c>Remove</c> can skip the sink without
    /// paying for a dispatch. <c>Any</c> is the whole gate for a project with no listeners: one field read.
    /// </summary>
    /// <remarks>
    /// The holder instance is created with the runner and handed to the frame once; it is then mutated in
    /// place and never replaced, because the frame keeps the reference. The bit arrays may be reallocated
    /// on a rebuild — callers always read them through this object.
    ///
    /// <para>Scope of the gate: it protects projects that register <b>no</b> listeners. A typeId that
    /// passes still walks the whole system list inside the sink, so a game that does use signals keeps
    /// paying O(systems) per Add for that type.</para>
    /// </remarks>
    internal sealed class ComponentSignalMasks
    {
        public bool Any;
        public bool[] Added = System.Array.Empty<bool>();
        public bool[] Removed = System.Array.Empty<bool>();

        public void Reset(int length)
        {
            Any = false;
            if (Added.Length != length)
            {
                Added = new bool[length];
                Removed = new bool[length];
            }
            else
            {
                System.Array.Clear(Added, 0, length);
                System.Array.Clear(Removed, 0, length);
            }
        }
    }
}
