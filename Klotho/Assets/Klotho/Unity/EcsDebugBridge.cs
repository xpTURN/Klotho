using System.Collections.Generic;
using UnityEngine;
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

        public FPNavMesh NavMesh { get; private set; }
        public FPNavMeshQuery NavQuery { get; private set; }
        public INavAgentSnapshotProvider NavAgentSnapshotProvider { get; private set; }
        public NavAgentSnapshot[] AgentSnapshots { get; } = new NavAgentSnapshot[64];
        public int AgentSnapshotCount { get; private set; }

        private EntityRef[] _allEntitiesBuf;

        void Awake()
        {
            Instance = this;
        }

        public void Register(EcsSimulation simulation)
        {
            Simulation = simulation;
            _allEntitiesBuf = new EntityRef[simulation.Frame.Entities.Capacity];
            HFSMFixedArrayReaders.Register();
        }

        public void RegisterNavMesh(FPNavMesh navMesh, FPNavMeshQuery query)
        {
            NavMesh = navMesh;
            NavQuery = query;
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
            if (Instance == this) Instance = null;
            NavMesh = null;
            NavQuery = null;
            NavAgentSnapshotProvider = null;
            AgentSnapshotCount = 0;
        }
    }
}
