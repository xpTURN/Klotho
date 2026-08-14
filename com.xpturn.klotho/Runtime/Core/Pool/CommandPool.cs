using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Command object pool (shared, lock-guarded, GC-free).
    /// </summary>
    public static class CommandPool
    {
        // Shared across all threads: the SD dedicated server runs each room's
        // Update on rotating ThreadPool workers, so a command rented during deserialize on one worker
        // is returned during deferred buffer cleanup on another. A [ThreadStatic] pool never lined up
        // across that hop; a single lock-guarded pool does. _gate is a leaf lock — never hold it across
        // a call that could re-enter CommandPool or acquire another lock (the diagnostic log is emitted
        // after the lock is released).
        private static readonly object _gate = new object();

        private static readonly Dictionary<int, Stack<ICommand>> _pools = new Dictionary<int, Stack<ICommand>>();
        private const int MAX_POOL_SIZE = 64;

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Ownership-violation diagnostic. Tracks instances handed out by Get<T>; Return
        // uses it to detect non-pool-origin or double-Return inputs before they enter the pool stacks
        // (would otherwise poison the pool). Shared (not ThreadStatic) and guarded by _gate in
        // lockstep with _pools, so cross-thread Get/Return is tracked correctly.
        private static readonly HashSet<ICommand> _outstanding = new HashSet<ICommand>();

        // Logger slot for the ownership-violation diagnostic. Optional — when unset the diagnostic
        // is silent (the Return still skips the offending instance, preventing pool poisoning).
        private static IKLogger _diagnosticLogger;

        public static void SetDiagnosticLogger(IKLogger logger) => _diagnosticLogger = logger;
#endif

        /// <summary>
        /// Rents a pooled command instance. The caller owns the instance until it hands it off to
        /// the engine via a public entry point (<c>ICommandSender.Send</c>, <c>KlothoEngine.InputCommand</c>)
        /// or an interface surface (<c>ILockstepNetworkService.SendCommand</c>, <c>IInputBuffer.AddCommand</c>) —
        /// ownership then transfers to the engine and the caller MUST NOT retain or reuse the instance.
        /// <para><b>Only <c>PlayerId</c> and <c>Tick</c> are reset.</b> A recycled instance still holds
        /// the previous caller's values in every field the command type declares, so the caller must
        /// assign ALL of them — not just the ones that differ from a default. This is the trap when
        /// converting a <c>new T { … }</c> initializer to a rent: the initializer left the rest at
        /// zero, and this does not.</para>
        /// </summary>
        public static T Get<T>() where T : CommandBase, new()
        {
            var typeId = CommandPoolTypeCache<T>.TypeId;
            T cmd;
            lock (_gate)
            {
                if (_pools.TryGetValue(typeId, out var stack) && stack.Count > 0)
                {
                    cmd = (T)stack.Pop();
                    cmd.PlayerId = 0;
                    cmd.Tick = 0;
                }
                else
                {
                    cmd = new T();
                }
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                _outstanding.Add(cmd);
#endif
            }
            return cmd;
        }

        public static void Return(ICommand cmd)
        {
            if (cmd == null) return;
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            bool ownershipViolation;
#endif
            lock (_gate)
            {
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                // Either the instance was never rented from the pool (game-side `new`) or this is a
                // double-Return. Skip the pool insert so the offending instance cannot poison the
                // stack. The diagnostic log is emitted after the lock.
                ownershipViolation = !_outstanding.Remove(cmd);
                if (!ownershipViolation)
#endif
                {
                    if (!_pools.TryGetValue(cmd.CommandTypeId, out var stack))
                    {
                        stack = new Stack<ICommand>();
                        _pools[cmd.CommandTypeId] = stack;
                    }
                    if (stack.Count < MAX_POOL_SIZE)
                        stack.Push(cmd);
                    // Pool cap overflow: instance is left to GC. _outstanding was already cleared above so
                    // no dangling tracking entry remains.
                }
            }
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            if (ownershipViolation)
            {
                // Emit outside the lock — logger I/O must not extend the critical section or risk a
                // lock-order inversion with the logger's own lock (_gate stays a leaf lock).
                var logger = _diagnosticLogger;
                if (logger != null)
                {
                    logger.KError($"[CommandPool] Return called on non-pool instance or wrong thread / double-Return — likely game-side `new`, cross-thread Return, or aliased game-side reference. Ownership contract violation suspected. Skipping pool insert. type={cmd.GetType().Name}");
                }
            }
#endif
        }

        public static void ClearAll()
        {
            lock (_gate)
            {
                foreach (var stack in _pools.Values)
                    stack.Clear();
                _pools.Clear();
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                _outstanding.Clear();
#endif
            }
        }

        // Diagnostic — total count of pooled command instances across all typeIds (shared pool).
        public static int GetTotalPooledCount()
        {
            lock (_gate)
            {
                int total = 0;
                foreach (var stack in _pools.Values)
                    total += stack.Count;
                return total;
            }
        }

        // Diagnostic — count of distinct command typeIds currently held in the pool.
        public static int GetPooledTypeCount()
        {
            lock (_gate)
                return _pools.Count;
        }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic — count of currently outstanding (rented but not returned) instances.
        public static int GetOutstandingCount()
        {
            lock (_gate)
                return _outstanding.Count;
        }
#endif

        private static class CommandPoolTypeCache<T> where T : CommandBase, new()
        {
            public static readonly int TypeId = new T().CommandTypeId;
        }
    }
}
