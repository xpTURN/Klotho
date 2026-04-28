using System;
using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Manages entity lifecycles using generation indices and free-list slot reuse.
    /// Pre-allocates a fixed capacity to avoid runtime allocations.
    /// </summary>
    public class EntityManager
    {
        private int[] _versions;
        private bool[] _alive;
        private int _aliveCount;
        private int _nextIndex;
        private int _capacity;
        private int[] _freeList;
        private int _freeCount;

        public int Count => _aliveCount;
        public int Capacity => _capacity;
        public int UsedSlotCount => _nextIndex;

        public EntityManager(int capacity)
        {
            _capacity = capacity;
            _versions = new int[capacity];
            _alive = new bool[capacity];
            _freeList = new int[capacity];
            _freeCount = 0;
            _aliveCount = 0;
            _nextIndex = 0;
        }

        public EntityRef Create()
        {
            int index;

            if (_freeCount > 0)
            {
                index = _freeList[--_freeCount];
            }
            else
            {
                if (_nextIndex >= _capacity)
                    throw new InvalidOperationException(
                        $"EntityManager capacity exceeded: {_capacity}");
                index = _nextIndex++;
            }

            _versions[index]++;
            _alive[index] = true;
            _aliveCount++;

            return new EntityRef(index, _versions[index]);
        }

        public void Destroy(EntityRef entity)
        {
            if (!IsAlive(entity))
                return;

            _alive[entity.Index] = false;
            _freeList[_freeCount++] = entity.Index;
            _aliveCount--;
        }

        public bool IsAlive(EntityRef entity)
        {
            if (entity.Index < 0 || entity.Index >= _capacity)
                return false;

            return _alive[entity.Index] && _versions[entity.Index] == entity.Version;
        }

        /// <summary>
        /// Checks whether a slot index is alive (no version check).
        /// Used in hash computation for deterministic ordering.
        /// </summary>
        public bool IsAlive(int index)
        {
            if (index < 0 || index >= _capacity)
                return false;

            return _alive[index];
        }

        /// <summary>
        /// Returns the current version of a slot index.
        /// </summary>
        public int GetVersion(int index)
        {
            return _versions[index];
        }

        /// <summary>
        /// Copies state from another EntityManager (for snapshot/rollback).
        /// </summary>
        public void CopyFrom(EntityManager source)
        {
            if (source._capacity != _capacity)
                throw new InvalidOperationException("EntityManager capacity mismatch");

            Buffer.BlockCopy(source._versions, 0, _versions, 0, _capacity * sizeof(int));
            Buffer.BlockCopy(source._freeList, 0, _freeList, 0, _capacity * sizeof(int));

            // bool[] is not blittable, so copy manually
            Array.Copy(source._alive, 0, _alive, 0, _capacity);

            _aliveCount = source._aliveCount;
            _nextIndex = source._nextIndex;
            _freeCount = source._freeCount;
        }

        public void Clear()
        {
            Array.Clear(_versions, 0, _capacity);
            Array.Clear(_alive, 0, _capacity);
            Array.Clear(_freeList, 0, _capacity);
            _aliveCount = 0;
            _nextIndex = 0;
            _freeCount = 0;
        }

        public int GetSerializedSize()
        {
            // aliveCount(4) + nextIndex(4) + freeCount(4)
            // + versions (nextIndex * 4) + alive (nextIndex * 1) + freeList (freeCount * 4) size calculation
            return 12 + _nextIndex * sizeof(int) + _nextIndex + _freeCount * sizeof(int);
        }

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(_aliveCount);
            writer.WriteInt32(_nextIndex);
            writer.WriteInt32(_freeCount);

            // Only write up to _nextIndex — slots beyond that have never been used
            var versionsBytes = MemoryMarshal.AsBytes(_versions.AsSpan(0, _nextIndex));
            writer.WriteRawBytes(versionsBytes);

            for (int i = 0; i < _nextIndex; i++)
                writer.WriteBool(_alive[i]);

            var freeListBytes = MemoryMarshal.AsBytes(_freeList.AsSpan(0, _freeCount));
            writer.WriteRawBytes(freeListBytes);
        }

        public void Deserialize(ref SpanReader reader)
        {
            _aliveCount = reader.ReadInt32();
            _nextIndex = reader.ReadInt32();
            _freeCount = reader.ReadInt32();

            // Clear slots beyond the range to be read
            if (_nextIndex < _capacity)
            {
                Array.Clear(_versions, _nextIndex, _capacity - _nextIndex);
                Array.Clear(_alive, _nextIndex, _capacity - _nextIndex);
            }

            var versionsBytes = reader.ReadRawBytes(_nextIndex * sizeof(int));
            versionsBytes.CopyTo(MemoryMarshal.AsBytes(_versions.AsSpan(0, _nextIndex)));

            for (int i = 0; i < _nextIndex; i++)
                _alive[i] = reader.ReadBool();

            if (_freeCount > 0)
            {
                var freeListBytes = reader.ReadRawBytes(_freeCount * sizeof(int));
                freeListBytes.CopyTo(MemoryMarshal.AsBytes(_freeList.AsSpan(0, _freeCount)));
            }

            if (_freeCount < _capacity)
                Array.Clear(_freeList, _freeCount, _capacity - _freeCount);
        }
    }
}
