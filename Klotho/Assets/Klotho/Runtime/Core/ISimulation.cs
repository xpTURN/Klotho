using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Deterministic simulation interface.
    /// Must guarantee the same output for the same input.
    /// </summary>
    public interface ISimulation
    {
        /// <summary>
        /// Current simulation tick.
        /// </summary>
        int CurrentTick { get; }

        /// <summary>
        /// Initialize the simulation.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Execute a single simulation tick.
        /// </summary>
        /// <param name="commands">Commands to execute in this tick.</param>
        void Tick(List<ICommand> commands);

        /// <summary>
        /// Roll back to a specific tick (used when prediction fails).
        /// </summary>
        void Rollback(int targetTick);

        /// <summary>
        /// Returns the hash of the current state (for sync validation).
        /// </summary>
        long GetStateHash();

        /// <summary>
        /// Reset the simulation.
        /// </summary>
        void Reset();

        void RestoreFromFullState(byte[] stateData);

        byte[] SerializeFullState();

        (byte[] data, long hash) SerializeFullStateWithHash();

        void EmitSyncEvents();

        /// <summary>
        /// Called when a new player is added via Late Join.
        /// Automatically invoked when a PlayerJoinCommand is detected inside Tick(commands).
        /// Implementations MUST raise OnPlayerJoinedNotification (contract).
        /// </summary>
        void OnPlayerJoined(int playerId, int tick);

        /// <summary>
        /// Raised inside ISimulation.OnPlayerJoined to signal the engine that a player joined.
        /// Carries only the joined playerId — sim entity count is internal to the simulation.
        /// </summary>
        event Action<int> OnPlayerJoinedNotification;
    }

}
