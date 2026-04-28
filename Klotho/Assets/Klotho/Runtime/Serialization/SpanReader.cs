using System;
using System.Buffers.Binary;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Serialization
{
    public ref struct SpanReader
    {
        private ReadOnlySpan<byte> _buffer;
        private int _position;
        private readonly byte[] _sourceBuffer;

        public int Position => _position;
        public int Length => _buffer.Length;
        public int Remaining => _buffer.Length - _position;
        public byte[] SourceBuffer => _sourceBuffer;

        public SpanReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
            _sourceBuffer = null;
        }

        public SpanReader(byte[] buffer, int offset, int length)
        {
            _sourceBuffer = buffer;
            _buffer = buffer.AsSpan(offset, length);
            _position = 0;
        }

        public byte ReadByte()
        {
            return _buffer[_position++];
        }

        public bool ReadBool()
        {
            return _buffer[_position++] != 0;
        }

        public short ReadInt16()
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        public ushort ReadUInt16()
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        public int PeekInt32()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
        }

        public uint ReadUInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        public ulong ReadUInt64()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        public EntityRef ReadEntityRef()
        {
            int index   = ReadInt32();
            int version = ReadInt32();
            return new EntityRef(index, version);
        }

        public DataAssetRef ReadDataAssetRef()
        {
            return new DataAssetRef(ReadInt32());
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            int length = ReadInt32();
            var slice = _buffer.Slice(_position, length);
            _position += length;
            return slice;
        }

        public ReadOnlySpan<byte> ReadRawBytes(int length)
        {
            var slice = _buffer.Slice(_position, length);
            _position += length;
            return slice;
        }

        public void Skip(int count)
        {
            _position += count;
        }

        public string ReadString()
        {
            int byteCount = ReadInt32();
            if (byteCount == 0)
                return string.Empty;
            var slice = _buffer.Slice(_position, byteCount);
            _position += byteCount;
            return System.Text.Encoding.UTF8.GetString(slice);
        }
    }
}
