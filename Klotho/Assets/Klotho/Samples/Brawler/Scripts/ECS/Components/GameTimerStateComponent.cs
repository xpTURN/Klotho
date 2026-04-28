using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(106)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct GameTimerStateComponent : IComponent
    {
        public int StartTick;
        public int LastReportedSeconds;
        public bool GameOverFired;
    }
}
