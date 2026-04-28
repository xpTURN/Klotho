using System;
using System.IO;
using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Pooling utility for MemoryStream and byte arrays (GC-free)
    /// </summary>
    public static class StreamPool
    {
        private static readonly Stack<MemoryStream> _streamPool = new Stack<MemoryStream>();
        // Bucket sizes: 4KB, 16KB, 32KB, 64KB
        private static readonly int[] _BUCKET_SIZES = { 4096, 16384, 32768, 65536 };
        private static readonly Stack<byte[]>[] _bufferBuckets = {
            new Stack<byte[]>(), new Stack<byte[]>(),
            new Stack<byte[]>(), new Stack<byte[]>()
        };

        private const int DEFAULT_BUFFER_SIZE = 4096;
        private const int MAX_BUFFER_SIZE = 65536; // 64KB — snapshot buffers up to this size are pooled
        private const int MAX_POOL_SIZE = 16; // per bucket

        #region MemoryStream Pool

        /// <summary>
        /// Get a MemoryStream from the pool
        /// </summary>
        public static MemoryStream GetStream()
        {
            lock (_streamPool)
            {
                if (_streamPool.Count > 0)
                {
                    var stream = _streamPool.Pop();
                    stream.SetLength(0);
                    stream.Position = 0;
                    return stream;
                }
            }
            return new MemoryStream(DEFAULT_BUFFER_SIZE);
        }
        
        /// <summary>
        /// Return a MemoryStream to the pool
        /// </summary>
        public static void ReturnStream(MemoryStream stream)
        {
            if (stream == null)
                return;

            lock (_streamPool)
            {
                if (_streamPool.Count < MAX_POOL_SIZE)
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                    _streamPool.Push(stream);
                }
                // If the pool is full, discard (delegate to GC)
            }
        }
        
        #endregion
        
        #region Buffer Pool
        
        private static int GetBucketIndex(int size)
        {
            for (int i = 0; i < _BUCKET_SIZES.Length; i++)
            {
                if (size <= _BUCKET_SIZES[i])
                    return i;
            }
            return -1; // Exceeds the largest bucket
        }

        /// <summary>
        /// Get a byte array from the pool (guaranteed minimum size)
        /// </summary>
        public static byte[] GetBuffer(int minSize)
        {
            int bucketIndex = GetBucketIndex(minSize);
            if (bucketIndex >= 0)
            {
                var bucket = _bufferBuckets[bucketIndex];
                lock (bucket)
                {
                    if (bucket.Count > 0)
                        return bucket.Pop();
                }
                return new byte[_BUCKET_SIZES[bucketIndex]];
            }
            return new byte[minSize];
        }

        /// <summary>
        /// Return a byte array to the pool
        /// </summary>
        public static void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null || buffer.Length > MAX_BUFFER_SIZE)
                return;

            int bucketIndex = GetBucketIndex(buffer.Length);
            if (bucketIndex >= 0 && buffer.Length == _BUCKET_SIZES[bucketIndex])
            {
                var bucket = _bufferBuckets[bucketIndex];
                lock (bucket)
                {
                    if (bucket.Count < MAX_POOL_SIZE)
                        bucket.Push(buffer);
                }
            }
        }
        
        /// <summary>
        /// Copy the MemoryStream contents into a new byte array (taken from the pool).
        /// Note: the returned array must be returned to the pool via ReturnBuffer after use.
        /// </summary>
        public static byte[] ToArrayPooled(MemoryStream stream)
        {
            int length = (int)stream.Length;
            byte[] result = GetBuffer(length);

            // Copy only the actual data
            stream.Position = 0;
            stream.Read(result, 0, length);

            // If an exact-sized array is required, allocate a new one
            if (result.Length != length)
            {
                byte[] exact = new byte[length];
                Array.Copy(result, exact, length);
                ReturnBuffer(result);
                return exact;
            }
            
            return result;
        }
        
        /// <summary>
        /// Copy the MemoryStream contents into a byte array (always exact size).
        /// Used when an exact size is required, e.g. for snapshot storage.
        /// </summary>
        public static byte[] ToArrayExact(MemoryStream stream)
        {
            int length = (int)stream.Length;
            byte[] result = new byte[length];
            
            stream.Position = 0;
            stream.Read(result, 0, length);
            
            return result;
        }
        
        #endregion
        
        /// <summary>
        /// Clear MemoryStream and Buffer pools (for testing)
        /// </summary>
        public static void Clear()
        {
            lock (_streamPool)
            {
                _streamPool.Clear();
            }
            for (int i = 0; i < _BUCKET_SIZES.Length; i++)
            {
                lock (_bufferBuckets[i])
                {
                    _bufferBuckets[i].Clear();
                }
            }
        }
    }
    
    /// <summary>
    /// PooledMemoryStream - automatically returned via a using statement
    /// </summary>
    public struct PooledMemoryStream : IDisposable
    {
        public MemoryStream Stream { get; private set; }
        private bool _disposed;
        
        public static PooledMemoryStream Create()
        {
            return new PooledMemoryStream
            {
                Stream = StreamPool.GetStream(),
                _disposed = false
            };
        }
        
        public void Dispose()
        {
            if (!_disposed && Stream != null)
            {
                StreamPool.ReturnStream(Stream);
                Stream = null;
                _disposed = true;
            }
        }
    }
}
