using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Serialization
{
    public struct SerializationBuffer : IDisposable
    {
        private byte[] _buffer;
        private bool _fromPool;

        public Span<byte> Span => _buffer.AsSpan(0, _buffer.Length);

        public static SerializationBuffer Create(int minSize)
        {
            return new SerializationBuffer
            {
                _buffer = StreamPool.GetBuffer(minSize),
                _fromPool = true
            };
        }

        public void Dispose()
        {
            if (_fromPool && _buffer != null)
            {
                StreamPool.ReturnBuffer(_buffer);
                _buffer = null;
                _fromPool = false;
            }
        }
    }
}
