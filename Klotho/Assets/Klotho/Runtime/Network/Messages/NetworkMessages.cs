using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public struct SerializedMessage : IDisposable
    {
        public byte[] Data;
        public int Length;
        private bool _fromPool;

        internal SerializedMessage(byte[] data, int length, bool fromPool)
        {
            Data = data;
            Length = length;
            _fromPool = fromPool;
        }

        public void Dispose()
        {
            if (_fromPool && Data != null)
            {
                StreamPool.ReturnBuffer(Data);
                Data = null;
            }
        }
    }

    /// <summary>
    /// Base class for network messages
    /// </summary>
    public abstract class NetworkMessageBase : INetworkMessage
    {
        public abstract NetworkMessageType MessageTypeId { get; }

        // NetworkMessageBase header: byte(1) = 1 byte
        public virtual int GetSerializedSize() => 1;

        public virtual void Serialize(ref SpanWriter writer)
        {
            writer.WriteByte((byte)MessageTypeId);
            SerializeData(ref writer);
        }

        public virtual void Deserialize(ref SpanReader reader)
        {
            reader.ReadByte(); // MessageType has already been checked
            DeserializeData(ref reader);
        }

        protected abstract void SerializeData(ref SpanWriter writer);
        protected abstract void DeserializeData(ref SpanReader reader);
    }

    /// <summary>
    /// Message serialization utility
    /// </summary>
    public partial class MessageSerializer : IMessageSerializer
    {
        static partial void RegisterGeneratedTypes(MessageSerializer serializer);

        private readonly Dictionary<NetworkMessageType, Func<INetworkMessage>> _creators
            = new Dictionary<NetworkMessageType, Func<INetworkMessage>>();

        private readonly Dictionary<NetworkMessageType, INetworkMessage> _messageCache
            = new Dictionary<NetworkMessageType, INetworkMessage>();

        public MessageSerializer()
        {
            RegisterGeneratedTypes(this);
            MessageRegistry.ApplyTo(this);
        }

        public void RegisterMessageType<T>(NetworkMessageType type) where T : INetworkMessage, new()
        {
            if (_creators.ContainsKey(type))
                return;
            _creators[type] = () => new T();
        }

        public byte[] Serialize(INetworkMessage message)
        {
            int size = message.GetSerializedSize();
            using (var buf = SerializationBuffer.Create(size))
            {
                var writer = new SpanWriter(buf.Span);
                message.Serialize(ref writer);
                return buf.Span.Slice(0, writer.Position).ToArray();
            }
        }

        public SerializedMessage SerializePooled(INetworkMessage message)
        {
            int size = message.GetSerializedSize();
            byte[] buf = StreamPool.GetBuffer(size);
            var writer = new SpanWriter(buf.AsSpan(0, buf.Length));
            message.Serialize(ref writer);
            return new SerializedMessage(buf, writer.Position, fromPool: true);
        }

        public INetworkMessage Deserialize(byte[] data)
        {
            if (data == null || data.Length < 1)
                return null;
            return Deserialize(data, data.Length);
        }

        public INetworkMessage Deserialize(byte[] data, int length)
        {
            return Deserialize(data, length, 0);
        }

        public INetworkMessage Deserialize(byte[] data, int length, int offset)
        {
            if (data == null || length < 1 || offset < 0 || offset + length > data.Length)
                return null;

            NetworkMessageType type = (NetworkMessageType)data[offset];

            if (!_creators.TryGetValue(type, out var creator))
            {
                // The cross-assembly ModuleInitializer may have run after the constructor — retry registration
                MessageRegistry.ApplyTo(this);
                if (!_creators.TryGetValue(type, out creator))
                    return null;
            }

            if (!_messageCache.TryGetValue(type, out var message))
            {
                message = creator();
                _messageCache[type] = message;
            }

            var reader = new SpanReader(data, offset, length);
            message.Deserialize(ref reader);
            return message;
        }
    }
}
