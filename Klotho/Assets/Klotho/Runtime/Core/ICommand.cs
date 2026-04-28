using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Command interface representing player input.
    /// All game commands must implement this interface.
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// Command type identifier.
        /// </summary>
        int CommandTypeId { get; }

        /// <summary>
        /// ID of the player that issued this command.
        /// </summary>
        int PlayerId { get; }

        /// <summary>
        /// The tick number at which this command must be executed.
        /// </summary>
        int Tick { get; }

        /// <summary>
        /// Serializes the command using SpanWriter (cross-platform, GC-free).
        /// </summary>
        void Serialize(ref SpanWriter writer);

        /// <summary>
        /// Deserializes the command from a SpanReader (cross-platform, GC-free).
        /// </summary>
        void Deserialize(ref SpanReader reader);

        /// <summary>
        /// Maximum serialized size in bytes.
        /// </summary>
        int GetSerializedSize();
    }

    /// <summary>
    /// System command interface.
    /// Stored separately from player input in the InputBuffer
    /// and not counted by HasAllCommands.
    /// </summary>
    public interface ISystemCommand : ICommand
    {
        /// <summary>
        /// Deterministic ordering key for system commands at the same tick.
        /// </summary>
        int OrderKey { get; }
    }

    /// <summary>
    /// Command factory interface.
    /// </summary>
    public interface ICommandFactory
    {
        /// <summary>
        /// Creates the appropriate command instance based on the command type.
        /// </summary>
        ICommand CreateCommand(int commandType);

        /// <summary>
        /// Restores a command from a length-prefixed SpanReader: reads [size][commandData...].
        /// </summary>
        ICommand DeserializeCommand(ref SpanReader reader);

        /// <summary>
        /// Restores a command from a raw SpanReader (no length prefix).
        /// </summary>
        ICommand DeserializeCommandRaw(ref SpanReader reader);

        /// <summary>
        /// Calculates the serialized size and stores the commands in the internal cache.
        /// </summary>
        int GetSerializedCommandsSize(List<ICommand> commands);

        /// <summary>
        /// Serializes the cached commands into the destination span.
        /// GetSerializedCommandsSize must be called first.
        /// </summary>
        int SerializeCommandsTo(Span<byte> destination);

        /// <summary>
        /// Deserializes multiple commands from a span.
        /// Note: the contents of the returned list are mutated on the next call.
        /// </summary>
        List<ICommand> DeserializeCommands(ReadOnlySpan<byte> data);

        /// <summary>
        /// Creates a single empty command instance (for caching).
        /// The returned instance is reused via PopulateEmpty and must not be returned to the pool.
        /// </summary>
        ICommand CreateEmptyCommand();

        /// <summary>
        /// Overwrites the fields of the cached empty command instance (no GC allocation).
        /// Sets PlayerId and Tick, and resets data fields to the "empty" state.
        /// </summary>
        void PopulateEmpty(ICommand cmd, int playerId, int tick);
    }
}
