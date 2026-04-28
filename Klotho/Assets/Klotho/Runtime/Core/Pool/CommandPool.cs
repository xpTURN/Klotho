using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Command object pool (ThreadStatic, GC-free).
    /// </summary>
    public static class CommandPool
    {
        [System.ThreadStatic]
        private static Dictionary<int, Stack<ICommand>> _pools;

        private static Dictionary<int, Stack<ICommand>> Pools => _pools ??= new Dictionary<int, Stack<ICommand>>();
        private const int MAX_POOL_SIZE = 64;

        public static T Get<T>() where T : CommandBase, new()
        {
            var typeId = CommandPoolTypeCache<T>.TypeId;
            if (Pools.TryGetValue(typeId, out var stack) && stack.Count > 0)
            {
                var cmd = (T)stack.Pop();
                cmd.PlayerId = 0;
                cmd.Tick = 0;
                return cmd;
            }
            return new T();
        }

        public static void Return(ICommand cmd)
        {
            if (cmd == null) return;
            if (!Pools.TryGetValue(cmd.CommandTypeId, out var stack))
            {
                stack = new Stack<ICommand>();
                Pools[cmd.CommandTypeId] = stack;
            }
            if (stack.Count < MAX_POOL_SIZE)
                stack.Push(cmd);
        }

        public static void ClearAll()
        {
            foreach (var stack in Pools.Values)
                stack.Clear();
            Pools.Clear();
        }

        private static class CommandPoolTypeCache<T> where T : CommandBase, new()
        {
            public static readonly int TypeId = new T().CommandTypeId;
        }
    }
}
