using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Generic Dictionary pool (GC-free)
    /// </summary>
    public static class DictionaryPool<TKey, TValue>
    {
        private static readonly Stack<Dictionary<TKey, TValue>> _pool = new Stack<Dictionary<TKey, TValue>>();
        private const int MAX_POOL_SIZE = 32;

        /// <summary>
        /// Get a Dictionary from the pool
        /// </summary>
        public static Dictionary<TKey, TValue> Get()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                {
                    var dict = _pool.Pop();
                    dict.Clear();
                    return dict;
                }
            }
            return new Dictionary<TKey, TValue>();
        }
        
        /// <summary>
        /// Return a Dictionary to the pool
        /// </summary>
        public static void Return(Dictionary<TKey, TValue> dict)
        {
            if (dict == null)
                return;
                
            lock (_pool)
            {
                if (_pool.Count < MAX_POOL_SIZE)
                {
                    dict.Clear();
                    _pool.Push(dict);
                }
            }
        }
        
        /// <summary>
        /// Clear the pool
        /// </summary>
        public static void Clear()
        {
            lock (_pool)
            {
                _pool.Clear();
            }
        }
    }
    
    /// <summary>
    /// Dictionary pool helper methods
    /// </summary>
    public static class DictionaryPoolHelper
    {
        /// <summary>
        /// Get a Dictionary&lt;int, T&gt;
        /// </summary>
        public static Dictionary<int, T> GetIntDictionary<T>()
        {
            return DictionaryPool<int, T>.Get();
        }

        /// <summary>
        /// Return a Dictionary&lt;int, T&gt;
        /// </summary>
        public static void ReturnIntDictionary<T>(Dictionary<int, T> dict)
        {
            DictionaryPool<int, T>.Return(dict);
        }
    }
}
