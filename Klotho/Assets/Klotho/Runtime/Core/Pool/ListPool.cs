using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Generic List pool (GC-free)
    /// </summary>
    public static class ListPool<T>
    {
        private static readonly Stack<List<T>> _pool = new Stack<List<T>>();
        private const int MAX_POOL_SIZE = 32;

        /// <summary>
        /// Get a List from the pool
        /// </summary>
        public static List<T> Get()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                {
                    var list = _pool.Pop();
                    list.Clear();
                    return list;
                }
            }
            return new List<T>();
        }
        
        /// <summary>
        /// Return a List to the pool
        /// </summary>
        public static void Return(List<T> list)
        {
            if (list == null)
                return;
                
            lock (_pool)
            {
                if (_pool.Count < MAX_POOL_SIZE)
                {
                    list.Clear();
                    _pool.Push(list);
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
    /// List pool helper methods
    /// </summary>
    public static class ListPoolHelper
    {
        /// <summary>
        /// Get a List&lt;int&gt;
        /// </summary>
        public static List<int> GetIntList()
        {
            return ListPool<int>.Get();
        }

        /// <summary>
        /// Return a List&lt;int&gt;
        /// </summary>
        public static void ReturnIntList(List<int> list)
        {
            ListPool<int>.Return(list);
        }

        /// <summary>
        /// Clear the int list pool
        /// </summary>
        public static void ClearIntListPool()
        {
            ListPool<int>.Clear();
        }

        /// <summary>
        /// Get a List&lt;ICommand&gt;
        /// </summary>
        public static List<ICommand> GetCommandList()
        {
            return ListPool<ICommand>.Get();
        }

        /// <summary>
        /// Return a List&lt;ICommand&gt;
        /// </summary>
        public static void ReturnCommandList(List<ICommand> list)
        {
            ListPool<ICommand>.Return(list);
        }

        /// <summary>
        /// Clear the command list pool
        /// </summary>
        public static void ClearCommandListPool()
        {
            ListPool<ICommand>.Clear();
        }
    }
}
