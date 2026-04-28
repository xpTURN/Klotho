using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    public class SystemRunner
    {
        private struct SystemEntry
        {
            public object System;
            public SystemPhase Phase;
            public int Order;
        }

        private readonly List<SystemEntry> _entries = new List<SystemEntry>();
        private SystemEntry[] _sorted;
        private bool _dirty = true;
        private int _nextOrder;

        public void AddSystem(object system, SystemPhase phase)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            _entries.Add(new SystemEntry
            {
                System = system,
                Phase = phase,
                Order = _nextOrder++
            });
            _dirty = true;
        }

        private void EnsureSorted()
        {
            if (!_dirty) return;

            _sorted = _entries.ToArray();
            Array.Sort(_sorted, (a, b) =>
            {
                int cmp = ((int)a.Phase).CompareTo((int)b.Phase);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            _dirty = false;
        }

        public void Init(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IInitSystem init)
                    init.OnInit(ref frame);
            }
        }

        public void Destroy(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IDestroySystem destroy)
                    destroy.OnDestroy(ref frame);
            }
        }

        public void RunUpdateSystems(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISystem sys)
                    sys.Update(ref frame);
            }
        }

        public void RunCommandSystems(ref Frame frame, ICommand command)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ICommandSystem cmdSys)
                    cmdSys.OnCommand(ref frame, command);
            }
        }

        public void OnComponentAdded<T>(ref Frame frame, EntityRef entity, ref T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentAdded<T> sys)
                    sys.OnAdded(ref frame, entity, ref component);
            }
        }

        public void OnComponentRemoved<T>(ref Frame frame, EntityRef entity, T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentRemoved<T> sys)
                    sys.OnRemoved(ref frame, entity, component);
            }
        }

        public void OnEntityCreated(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityCreatedSystem sys)
                    sys.OnEntityCreated(ref frame, entity);
            }
        }

        public void OnEntityDestroyed(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityDestroyedSystem sys)
                    sys.OnEntityDestroyed(ref frame, entity);
            }
        }

        public void EmitSyncEvents(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISyncEventSystem syncSys)
                    syncSys.EmitSyncEvents(ref frame);
            }
        }

        public void Signal<TSignal>(ref Frame frame, SignalInvoker<TSignal> invoke)
            where TSignal : class, ISignal
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is TSignal sys)
                    invoke(sys, ref frame);
            }
        }
    }
}
