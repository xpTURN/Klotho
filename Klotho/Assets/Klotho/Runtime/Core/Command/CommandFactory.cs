using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Command factory implementation.
    /// </summary>
    public partial class CommandFactory : ICommandFactory
    {
        static partial void RegisterGeneratedTypes(CommandFactory factory);

        private readonly Dictionary<int, Func<ICommand>> _creators = new Dictionary<int, Func<ICommand>>();
        
        // Cached list (avoids GC)
        private readonly List<ICommand> _commandListCache = new List<ICommand>();

        public CommandFactory()
        {
            RegisterGeneratedTypes(this);
            CommandRegistry.ApplyTo(this);
        }

        /// <summary>
        /// Registers a command type.
        /// </summary>
        public void RegisterCommand<T>(int commandType) where T : CommandBase, new()
        {
            if (_creators.ContainsKey(commandType))
                return;
            _creators[commandType] = () => CommandPool.Get<T>();
        }

        public ICommand CreateCommand(int commandType)
        {
            if (_creators.TryGetValue(commandType, out var creator))
            {
                return creator();
            }

            throw new ArgumentException($"Unknown command type: {commandType}");
        }

        /// <summary>
        /// Deserializes a length-prefixed command: reads [size][commandData...] from the reader.
        /// </summary>
        public ICommand DeserializeCommand(ref SpanReader reader)
        {
            int cmdSize = reader.ReadInt32();
            if (cmdSize < 4)
            {
                reader.ReadRawBytes(cmdSize);
                return null;
            }

            // Read command data as a sub-span
            var cmdSpan = reader.ReadRawBytes(cmdSize);
            var cmdReader = new SpanReader(cmdSpan);
            return DeserializeCommandRaw(ref cmdReader);
        }

        /// <summary>
        /// Raw command deserialization (no length prefix): reads commandData directly from the reader.
        /// </summary>
        public ICommand DeserializeCommandRaw(ref SpanReader reader)
        {
            if (reader.Remaining < 4)
                return null;

            int commandType = reader.PeekInt32();
            if (!_creators.ContainsKey(commandType))
            {
                CommandRegistry.ApplyTo(this);
                if (!_creators.ContainsKey(commandType))
                    return null;
            }

            ICommand command = CreateCommand(commandType);
            command.Deserialize(ref reader);
            return command;
        }

        /// <summary>
        /// Computes the serialized size and stores the commands in the internal cache.
        /// </summary>
        public int GetSerializedCommandsSize(List<ICommand> commands)
        {
            _commandListCache.Clear();
            int totalSize = 4; // count
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                _commandListCache.Add(cmd);
                totalSize += 4 + cmd.GetSerializedSize(); // size prefix + data
            }
            return totalSize;
        }

        /// <summary>
        /// Serializes the cached commands to the destination span.
        /// GetSerializedCommandsSize must be called first.
        /// </summary>
        public int SerializeCommandsTo(Span<byte> destination)
        {
            var writer = new SpanWriter(destination);
            writer.WriteInt32(_commandListCache.Count);

            for (int i = 0; i < _commandListCache.Count; i++)
            {
                int sizePos = writer.Position;
                writer.WriteInt32(0); // placeholder
                int cmdStart = writer.Position;
                _commandListCache[i].Serialize(ref writer);
                int cmdSize = writer.Position - cmdStart;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    destination.Slice(sizePos), cmdSize);
            }

            return writer.Position;
        }

        /// <summary>
        /// Deserializes a command array (Span-based).
        /// Note: the contents of the returned list change on the next call.
        /// </summary>
        public List<ICommand> DeserializeCommands(ReadOnlySpan<byte> data)
        {
            _commandListCache.Clear();

            var reader = new SpanReader(data);
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                ICommand cmd = DeserializeCommand(ref reader);
                if (cmd != null)
                    _commandListCache.Add(cmd);
            }

            return _commandListCache;
        }

        /// <summary>
        /// Deserializes a command array from byte[].
        /// Note: the contents of the returned list change on the next call.
        /// </summary>
        public List<ICommand> DeserializeCommands(byte[] data)
        {
            return DeserializeCommands(data.AsSpan());
        }

        public ICommand CreateEmptyCommand()
        {
            return new EmptyCommand();
        }

        public void PopulateEmpty(ICommand cmd, int playerId, int tick)
        {
            var empty = (EmptyCommand)cmd;
            empty.PlayerId = playerId;
            empty.Tick = tick;
        }
    }
}
