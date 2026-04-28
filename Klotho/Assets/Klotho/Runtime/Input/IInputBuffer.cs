using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Input
{
    /// <summary>
    /// Input buffer interface
    /// Buffers inputs to compensate for network latency
    /// </summary>
    public interface IInputBuffer
    {
        /// <summary>
        /// Number of inputs stored in the buffer
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Oldest tick number
        /// </summary>
        int OldestTick { get; }

        /// <summary>
        /// Newest tick number
        /// </summary>
        int NewestTick { get; }

        /// <summary>
        /// Add a command
        /// </summary>
        void AddCommand(ICommand command);

        /// <summary>
        /// Get all commands for a specific tick
        /// </summary>
        IEnumerable<ICommand> GetCommands(int tick);

        /// <summary>
        /// Get the command for a specific player at a specific tick
        /// </summary>
        ICommand GetCommand(int tick, int playerId);

        /// <summary>
        /// Check whether a command exists for a specific tick
        /// </summary>
        bool HasCommandForTick(int tick);

        /// <summary>
        /// Check whether a command exists for a specific player and tick
        /// </summary>
        bool HasCommandForTick(int tick, int playerId);

        /// <summary>
        /// Remove all commands before a specific tick
        /// </summary>
        void ClearBefore(int tick);

        /// <summary>
        /// Remove all commands after a specific tick (used for rollback)
        /// </summary>
        void ClearAfter(int tick);

        /// <summary>
        /// Remove all commands
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Input predictor interface
    /// Predicts inputs from other players that have not yet been received
    /// </summary>
    public interface IInputPredictor
    {
        /// <summary>
        /// Predict input for a specific player
        /// </summary>
        /// <param name="playerId">Player ID</param>
        /// <param name="tick">Tick to predict for</param>
        /// <param name="previousCommands">Previous commands (used for prediction)</param>
        ICommand PredictInput(int playerId, int tick, List<ICommand> previousCommands);

        /// <summary>
        /// Update prediction accuracy (compared against actual input)
        /// </summary>
        void UpdateAccuracy(ICommand predicted, ICommand actual);

        /// <summary>
        /// Prediction accuracy (0.0 ~ 1.0)
        /// </summary>
        float Accuracy { get; }
    }
}
