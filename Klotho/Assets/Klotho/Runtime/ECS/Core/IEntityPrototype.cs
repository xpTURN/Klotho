namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Entity prototype. Receives a Frame and EntityRef to initialize components.
    /// </summary>
    public interface IEntityPrototype
    {
        void Apply(Frame frame, EntityRef entity);
    }
}
