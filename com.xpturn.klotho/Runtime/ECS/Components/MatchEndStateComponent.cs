using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Engine-injected singleton carrying the deterministic match-end state.
    /// The game's match-end system writes {Ended, WinnerPlayerId, Reason} when the match
    /// terminates; the engine reads it via ISimulation.IsMatchEndedState / GetActiveMatchEnd at
    /// resync/restore boundaries to back-stop a lost OnMatchEnded and to gate Pause-grace
    /// StopCommand injection off the *current* state rather than the one-way notification latch.
    /// Part of full state — round-trips through serialize/restore and rolls back with snapshots
    /// (so a rollback past the end tick correctly reports Ended=false again). Added by KlothoEngine
    /// before OnInitializeWorld on non-SD-client peers; SD clients receive it via FullState restore.
    /// </summary>
    // Reason (FixedString32) is intentionally NOT persisted here: the component-serialization generator
    // does not support FixedString32 fields, and the engine's use (gate + backstop) needs only the
    // termination flag and winner. The normal OnMatchEnded path still carries the game event's full Reason;
    // only the rare resync/restore backstop supplies a sentinel reason.
    [KlothoComponent(26)]
    [KlothoSingletonComponent]
    [KlothoCoreComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MatchEndStateComponent : IComponent
    {
        public bool Ended;
        public int WinnerPlayerId;     // -1 = draw / no winner (same shape as IMatchEndEvent)
    }
}
