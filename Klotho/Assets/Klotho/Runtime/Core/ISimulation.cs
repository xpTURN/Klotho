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
        /// Creates entities required by the simulation and notifies the engine
        /// to update _playerCount via the OnPlayerCountChanged callback.
        /// Automatically invoked when a PlayerJoinCommand is detected inside Tick(commands).
        /// Implementations MUST raise OnPlayerCountChanged (contract).
        /// </summary>
        void OnPlayerJoined(int playerId, int tick);

        /// <summary>
        /// Callback to notify the engine when the player count changes (newPlayerCount, changedPlayerId).
        /// Raised inside ISimulation.OnPlayerJoined.
        /// </summary>
        event Action<int, int> OnPlayerCountChanged;
    }

}
