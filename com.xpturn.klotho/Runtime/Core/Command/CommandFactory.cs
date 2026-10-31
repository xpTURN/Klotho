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
        
        // Cached lists (avoid GC) — receive and send directions are kept separate:
        // a single shared cache made every consumer of a DeserializeCommands result depend on
        // the implicit "do not serialize through this factory before you finish reading" order.
        private readonly List<ICommand> _deserializeListCache = new List<ICommand>();
        private readonly List<ICommand> _serializeStagingCache = new List<ICommand>();

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
            try
            {
                command.Deserialize(ref reader);
            }
            catch
            {
                // A malformed payload leaves a half-filled pooled instance — hand it back before rethrowing.
                CommandPool.Return(command);
                throw;
            }
            return command;
        }

        /// <summary>
        /// Computes the serialized size and stores the commands in the internal cache.
        /// </summary>
        public int GetSerializedCommandsSize(List<ICommand> commands)
        {
            _serializeStagingCache.Clear();
            int totalSize = 4; // count
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                _serializeStagingCache.Add(cmd);
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
            writer.WriteInt32(_serializeStagingCache.Count);

            for (int i = 0; i < _serializeStagingCache.Count; i++)
            {
                int sizePos = writer.Position;
                writer.WriteInt32(0); // placeholder
                int cmdStart = writer.Position;
                _serializeStagingCache[i].Serialize(ref writer);
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
            _deserializeListCache.Clear();

            var reader = new SpanReader(data);
            int count = reader.ReadInt32();

            try
            {
                for (int i = 0; i < count; i++)
                {
                    ICommand cmd = DeserializeCommand(ref reader);
                    if (cmd != null)
                        _deserializeListCache.Add(cmd);
                }
            }
            catch
            {
                // An array that fails part-way hands back the commands it already decoded before rethrowing.
                for (int i = 0; i < _deserializeListCache.Count; i++)
                    CommandPool.Return(_deserializeListCache[i]);
                _deserializeListCache.Clear();
                throw;
            }

            return _deserializeListCache;
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

    public static class CommandFactoryExtensions
    {
        /// <summary>
        /// Decodes a raw command without throwing. False: the payload is malformed (the decode threw).
        /// True with a null command: the command type is unknown (DeserializeCommandRaw's null).
        /// </summary>
        public static bool TryDeserializeCommandRaw(this ICommandFactory factory, ref SpanReader reader, out ICommand command)
            => TryDeserializeCommandRaw(factory, ref reader, out command, out _);

        /// <summary>
        /// Same as the three-argument form, and also hands back what the decode threw (null when it did not),
        /// so a caller can tell a malformed payload from a local fault in its log.
        /// </summary>
        public static bool TryDeserializeCommandRaw(this ICommandFactory factory, ref SpanReader reader, out ICommand command, out Exception error)
        {
            // Outside the try: a missing factory is a setup bug, not a malformed peer payload.
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            try
            {
                command = factory.DeserializeCommandRaw(ref reader);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                command = null;
                error = e;
                return false;
            }
        }
    }
}
