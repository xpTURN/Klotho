namespace xpTURN.Klotho.ECS.FSM
{
    public abstract class HFSMDecision
    {
        public abstract bool Decide(ref AIContext context);
    }
}
