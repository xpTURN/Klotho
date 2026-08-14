using System.Collections.Generic;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Unity
{
    public class EcsDebugBridge : MonoBehaviour
    {
        public static EcsDebugBridge Instance { get; private set; }
        public EcsSimulation Simulation { get; private set; }
        public Dictionary<int, EntityRef> AllEntityIndexMap { get; } = new Dictionary<int, EntityRef>();

        // Read through to the provider instead of caching: a runtime rebake swaps the
        // simulation's mesh, and a cached copy would go stale for the rest of the session.
        // Consumers should read these ONCE into a local per callback — every access is an
        // independent read, so two reads can straddle a swap and hand you a mismatched
        // mesh/query pair.
        private INavMeshProvider _navMeshProvider;
        public FPNavMesh NavMesh => _navMeshProvider?.NavMesh;
        public FPNavMeshQuery NavQuery => _navMeshProvider?.NavQuery;
        public INavAgentSnapshotProvider NavAgentSnapshotProvider { get; private set; }
        public NavAgentSnapshot[] AgentSnapshots { get; } = new NavAgentSnapshot[64];
        public int AgentSnapshotCount { get; private set; }

        private EntityRef[] _allEntitiesBuf;

        void Awake()
        {
            Instance = this;
#if UNITY_EDITOR
            KlothoSession.OnSessionCreated += HandleSessionCreated;
#endif
        }

        public void Register(EcsSimulation simulation)
        {
            Simulation = simulation;
            _allEntitiesBuf = new EntityRef[simulation.Frame.Entities.Capacity];
            HFSMFixedArrayReaders.Register();
        }

        public void RegisterNavMeshProvider(INavMeshProvider provider)
        {
            _navMeshProvider = provider;
        }

        public void UnregisterNavMeshProvider(INavMeshProvider provider)
        {
            if (_navMeshProvider == provider)
                _navMeshProvider = null;
        }

        public void RegisterNavAgentProvider(INavAgentSnapshotProvider provider)
        {
            NavAgentSnapshotProvider = provider;
        }

        public void UnregisterNavAgentProvider(INavAgentSnapshotProvider provider)
        {
            if (NavAgentSnapshotProvider == provider)
                NavAgentSnapshotProvider = null;
        }

        void LateUpdate()
        {
            if (Simulation == null) return;

            AllEntityIndexMap.Clear();
            int count = Simulation.Frame.GetAllLiveEntities(_allEntitiesBuf);
            for (int i = 0; i < count; i++)
                AllEntityIndexMap[_allEntitiesBuf[i].Index] = _allEntitiesBuf[i];

            if (NavAgentSnapshotProvider != null)
            {
                NavAgentSnapshotProvider.CollectSnapshots(AgentSnapshots, out int snapshotCount);
                AgentSnapshotCount = snapshotCount;
            }
            else
            {
                AgentSnapshotCount = 0;
            }
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            KlothoSession.OnSessionCreated -= HandleSessionCreated;
#endif
            if (Instance == this) Instance = null;
            _navMeshProvider = null;
            NavAgentSnapshotProvider = null;
            AgentSnapshotCount = 0;
        }

#if UNITY_EDITOR
        private void HandleSessionCreated(KlothoSession session)
        {
            if (session?.Simulation == null) return;
            Register(session.Simulation);

            var cb = session.SimulationCallbacks;
            if (cb is INavMeshProvider navMesh)
                RegisterNavMeshProvider(navMesh);
            if (cb is INavAgentProvider agentProvider)
                RegisterNavAgentProvider(agentProvider.NavAgentSnapshotProvider);
        }
#endif
    }
}
