using System;
using System.Buffers.Binary;
using System.Diagnostics;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Serialization
{
    public ref struct SpanWriter
    {
        private Span<byte> _buffer;
        private int _position;

        public int Position => _position;
        public int Capacity => _buffer.Length;
        public int Remaining => _buffer.Length - _position;

        public SpanWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        [Conditional("DEBUG")]
        private void EnsureCapacity(int bytes)
        {
            if (_position + bytes > _buffer.Length)
                throw new InvalidOperationException(
                    $"SpanWriter buffer overflow: need {bytes} bytes at position {_position}, but capacity is {_buffer.Length}. Check GetSerializedSize().");
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        public void WriteBool(bool value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value ? (byte)1 : (byte)0;
        }

        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position), value);
            _position += 2;
        }

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), value);
            _position += 2;
        }

        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), value);
            _position += 4;
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position), value);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), value);
            _position += 8;
        }

        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position), value);
            _position += 8;
        }

        public void WriteEntityRef(EntityRef value)
        {
            WriteInt32(value.Index);
            WriteInt32(value.Version);
        }

        public void WriteDataAssetRef(DataAssetRef value)
        {
            WriteInt32(value.Id);
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(4 + data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), data.Length);
            _position += 4;
            data.CopyTo(_buffer.Slice(_position));
            _position += data.Length;
        }

        public void WriteRawBytes(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.Slice(_position));
            _position += data.Length;
        }

        public void WriteString(string value)
        {
            if (value == null) value = string.Empty;
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
            EnsureCapacity(4 + byteCount);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), byteCount);
            _position += 4;
            if (byteCount > 0)
            {
                System.Text.Encoding.UTF8.GetBytes(value.AsSpan(), _buffer.Slice(_position, byteCount));
                _position += byteCount;
            }
        }
    }
}
