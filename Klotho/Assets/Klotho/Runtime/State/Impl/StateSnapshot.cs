using System;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.State
{
    /// <summary>
    /// State snapshot implementation
    /// </summary>
    [Serializable]
    public class StateSnapshot : IStateSnapshot
    {
        public int Tick { get; private set; }

        private byte[] _data;
        private int _dataLength;
        private ulong _hash;
        private bool _hashCalculated;

        public StateSnapshot(int tick)
        {
            Tick = tick;
        }

        public StateSnapshot(int tick, byte[] data) : this(tick)
        {
            _data = data;
            _dataLength = data?.Length ?? 0;
        }

        // tick(4) + dataLength prefix(4) + data
        public int GetSerializedSize() => 4 + 4 + _dataLength;

        public byte[] Serialize()
        {
            int size = GetSerializedSize();
            using (var buf = SerializationBuffer.Create(size))
            {
                var writer = new SpanWriter(buf.Span);
                Serialize(ref writer);
                return buf.Span.Slice(0, writer.Position).ToArray();
            }
        }

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            if (_data != null && _dataLength > 0)
                writer.WriteBytes(new ReadOnlySpan<byte>(_data, 0, _dataLength));
            else
                writer.WriteInt32(0);
        }

        public void Deserialize(byte[] data)
        {
            var reader = new SpanReader(data);
            Deserialize(ref reader);
        }

        public void Deserialize(ref SpanReader reader)
        {
            Tick = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (length > 0)
            {
                _data = reader.ReadRawBytes(length).ToArray();
                _dataLength = length;
            }
            else
            {
                _data = null;
                _dataLength = 0;
            }

            _hashCalculated = false;
        }

        public ulong CalculateHash()
        {
            if (_hashCalculated)
                return _hash;

            if (_data == null || _dataLength == 0)
            {
                _hash = 0;
                _hashCalculated = true;
                return _hash;
            }

            // FNV-1a hash
            _hash = 14695981039346656037UL;
            for (int i = 0; i < _dataLength; i++)
            {
                _hash ^= _data[i];
                _hash *= 1099511628211UL;
            }

            _hashCalculated = true;
            return (ulong)_hash;
        }

        public byte[] GetData()
        {
            return _data;
        }

        public int GetDataLength()
        {
            return _dataLength;
        }

        /// <summary>
        /// Release only the data reference without returning the buffer to the pool.
        /// Used by RingSnapshotManager to prevent double-return.
        /// </summary>
        internal void ClearDataWithoutReturn()
        {
            _data = null;
            _dataLength = 0;
            _hashCalculated = false;
        }

        public void SetData(byte[] data)
        {
            // Return the previous buffer to the pool
            if (_data != null)
                StreamPool.ReturnBuffer(_data);

            _data = data;
            _dataLength = data?.Length ?? 0;
            _hashCalculated = false;
        }

        public void SetData(byte[] data, int length)
        {
            // Return the previous buffer to the pool
            if (_data != null)
                StreamPool.ReturnBuffer(_data);

            _data = data;
            _dataLength = length;
            _hashCalculated = false;
        }
    }
}
