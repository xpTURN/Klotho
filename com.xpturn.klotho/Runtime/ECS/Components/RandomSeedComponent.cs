using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Engine-injected singleton component carrying the session-agreed random seed.
    /// Added automatically by KlothoEngine.Start before OnInitializeWorld dispatches.
    /// LateJoin / Reconnect / Spectator / Replay paths receive it via FullState restore.
    /// </summary>
    [KlothoComponent(5)]
    [KlothoSingletonComponent]
    [KlothoCoreComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct RandomSeedComponent : IComponent
    {
        public ulong Seed;
    }
}
