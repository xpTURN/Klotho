namespace xpTURN.Klotho.ECS.FSM
{
    public abstract class AIAction
    {
        public abstract void Execute(ref AIContext context);
    }
}
