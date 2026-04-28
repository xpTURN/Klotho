using System;

namespace xpTURN.Klotho.ECS
{
    // The single-type Filter does not need a Has check, so it omits the storage field
    // and captures only the DenseToSparse span and Count in the ctor for iteration.
    public ref struct Filter<T1> where T1 : unmanaged, IComponent
    {
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal Filter(ComponentStorageFlat<T1> storage1, EntityManager entities)
        {
            _entities = entities;
            _denseToSparse = storage1.DenseToSparse;   // span derived once in ctor (one MemoryMarshal.Cast)
            _count = storage1.Count;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        public int Count => _count;
    }

    public ref struct Filter<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal Filter(ComponentStorageFlat<T1> storage1, ComponentStorageFlat<T2> storage2, EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _entities = entities;

            // For efficiency, iterate over the smaller storage
            if (storage1.Count <= storage2.Count)
            {
                _denseToSparse = storage1.DenseToSparse;
                _count = storage1.Count;
            }
            else
            {
                _denseToSparse = storage2.DenseToSparse;
                _count = storage2.Count;
            }
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        public int Count => _count;
    }

    public ref struct Filter<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal Filter(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _entities = entities;

            // Select and iterate over the smallest storage
            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min)
            {
                min = storage2.Count;
                _denseToSparse = storage2.DenseToSparse;
            }
            if (storage3.Count < min)
            {
                min = storage3.Count;
                _denseToSparse = storage3.DenseToSparse;
            }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        public int Count => _count;
    }

    public ref struct Filter<T1, T2, T3, T4>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly ComponentStorageFlat<T4> _storage4;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal Filter(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            ComponentStorageFlat<T4> storage4,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _storage4 = storage4;
            _entities = entities;

            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min) { min = storage2.Count; _denseToSparse = storage2.DenseToSparse; }
            if (storage3.Count < min) { min = storage3.Count; _denseToSparse = storage3.DenseToSparse; }
            if (storage4.Count < min) { min = storage4.Count; _denseToSparse = storage4.DenseToSparse; }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex)
                    && _storage4.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        public int Count => _count;
    }

    public ref struct Filter<T1, T2, T3, T4, T5>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        where T5 : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly ComponentStorageFlat<T4> _storage4;
        private readonly ComponentStorageFlat<T5> _storage5;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal Filter(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            ComponentStorageFlat<T4> storage4,
            ComponentStorageFlat<T5> storage5,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _storage4 = storage4;
            _storage5 = storage5;
            _entities = entities;

            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min) { min = storage2.Count; _denseToSparse = storage2.DenseToSparse; }
            if (storage3.Count < min) { min = storage3.Count; _denseToSparse = storage3.DenseToSparse; }
            if (storage4.Count < min) { min = storage4.Count; _denseToSparse = storage4.DenseToSparse; }
            if (storage5.Count < min) { min = storage5.Count; _denseToSparse = storage5.DenseToSparse; }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex)
                    && _storage4.Has(entityIndex)
                    && _storage5.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        public int Count => _count;
    }

    /// <summary>
    /// Filter that adds a single exclusion condition to Filter<T1>.
    /// Used as frame.FilterWithout&lt;Health, Dead&gt;() instead of the
    /// frame.Filter&lt;Health&gt;().Without&lt;Dead&gt;() pattern.
    /// </summary>
    public ref struct FilterWithout<T1, TExclude>
        where T1 : unmanaged, IComponent
        where TExclude : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<TExclude> _exclude;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal FilterWithout(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<TExclude> exclude,
            EntityManager entities)
        {
            _storage1 = storage1;
            _exclude = exclude;
            _entities = entities;
            _denseToSparse = storage1.DenseToSparse;
            _count = storage1.Count;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && !_exclude.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }
    }

    public ref struct FilterWithout<T1, T2, TExclude>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where TExclude : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<TExclude> _exclude;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal FilterWithout(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<TExclude> exclude,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _exclude = exclude;
            _entities = entities;

            if (storage1.Count <= storage2.Count)
            {
                _denseToSparse = storage1.DenseToSparse;
                _count = storage1.Count;
            }
            else
            {
                _denseToSparse = storage2.DenseToSparse;
                _count = storage2.Count;
            }
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && !_exclude.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }
    }

    public ref struct FilterWithout<T1, T2, T3, TExclude>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where TExclude : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly ComponentStorageFlat<TExclude> _exclude;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal FilterWithout(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            ComponentStorageFlat<TExclude> exclude,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _exclude = exclude;
            _entities = entities;

            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min) { min = storage2.Count; _denseToSparse = storage2.DenseToSparse; }
            if (storage3.Count < min) { min = storage3.Count; _denseToSparse = storage3.DenseToSparse; }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex)
                    && !_exclude.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }
    }

    public ref struct FilterWithout<T1, T2, T3, T4, TExclude>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        where TExclude : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly ComponentStorageFlat<T4> _storage4;
        private readonly ComponentStorageFlat<TExclude> _exclude;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal FilterWithout(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            ComponentStorageFlat<T4> storage4,
            ComponentStorageFlat<TExclude> exclude,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _storage4 = storage4;
            _exclude = exclude;
            _entities = entities;

            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min) { min = storage2.Count; _denseToSparse = storage2.DenseToSparse; }
            if (storage3.Count < min) { min = storage3.Count; _denseToSparse = storage3.DenseToSparse; }
            if (storage4.Count < min) { min = storage4.Count; _denseToSparse = storage4.DenseToSparse; }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex)
                    && _storage4.Has(entityIndex)
                    && !_exclude.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }
    }

    public ref struct FilterWithout<T1, T2, T3, T4, T5, TExclude>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        where T5 : unmanaged, IComponent
        where TExclude : unmanaged, IComponent
    {
        private readonly ComponentStorageFlat<T1> _storage1;
        private readonly ComponentStorageFlat<T2> _storage2;
        private readonly ComponentStorageFlat<T3> _storage3;
        private readonly ComponentStorageFlat<T4> _storage4;
        private readonly ComponentStorageFlat<T5> _storage5;
        private readonly ComponentStorageFlat<TExclude> _exclude;
        private readonly EntityManager _entities;
        private readonly ReadOnlySpan<int> _denseToSparse;
        private readonly int _count;
        private int _index;

        internal FilterWithout(
            ComponentStorageFlat<T1> storage1,
            ComponentStorageFlat<T2> storage2,
            ComponentStorageFlat<T3> storage3,
            ComponentStorageFlat<T4> storage4,
            ComponentStorageFlat<T5> storage5,
            ComponentStorageFlat<TExclude> exclude,
            EntityManager entities)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
            _storage4 = storage4;
            _storage5 = storage5;
            _exclude = exclude;
            _entities = entities;

            int min = storage1.Count;
            _denseToSparse = storage1.DenseToSparse;

            if (storage2.Count < min) { min = storage2.Count; _denseToSparse = storage2.DenseToSparse; }
            if (storage3.Count < min) { min = storage3.Count; _denseToSparse = storage3.DenseToSparse; }
            if (storage4.Count < min) { min = storage4.Count; _denseToSparse = storage4.DenseToSparse; }
            if (storage5.Count < min) { min = storage5.Count; _denseToSparse = storage5.DenseToSparse; }

            _count = min;
            _index = 0;
        }

        public bool Next(out EntityRef entity)
        {
            while (_index < _count)
            {
                int entityIndex = _denseToSparse[_index];
                _index++;

                if (_entities.IsAlive(entityIndex)
                    && _storage1.Has(entityIndex)
                    && _storage2.Has(entityIndex)
                    && _storage3.Has(entityIndex)
                    && _storage4.Has(entityIndex)
                    && _storage5.Has(entityIndex)
                    && !_exclude.Has(entityIndex))
                {
                    entity = new EntityRef(entityIndex, _entities.GetVersion(entityIndex));
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }
    }
}
