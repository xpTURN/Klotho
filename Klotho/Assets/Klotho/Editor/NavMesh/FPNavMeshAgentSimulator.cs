using UnityEditor;
using UnityEngine;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Simulates NavMesh agent pathfinding in the editor.
    /// T3: uses lightweight Frame + NavAgentComponent.
    /// </summary>
    internal class FPNavMeshAgentSimulator
    {
        public const int MAX_AGENTS = 32;

        // Simulation state
        public bool IsRunning;
        public int CurrentTick;
        public float SimulationSpeed = 1.0f;

        // Default agent settings
        public float DefaultSpeed = 5.0f;
        public float DefaultRadius = 0.5f;
        public float DefaultAcceleration = 10.0f;
        public bool EnableAvoidance = true;

        // For ORCA visualization
        public FPNavAvoidance Avoidance => _avoidance;
        public int LastOrcaComputedAgentIndex { get; private set; } = -1;

        public int AgentCount => _entityCount;

        // Internal
        private Frame _simFrame;
        private EntityRef[] _entities = new EntityRef[MAX_AGENTS];
        private int _entityCount;

        private FPNavAgentSystem _agentSystem;
        private FPNavAvoidance _avoidance;
        private FPNavMeshVisualizerData _data;
        private double _lastUpdateTime;
        private double _accumulator;
        private readonly FP64 _dt = FP64.FromDouble(1.0 / 60.0);
        private const double FIXED_DT = 1.0 / 60.0;

        // Remember initial positions (for reset)
        private Vector3[] _initialPositions = new Vector3[MAX_AGENTS];

        public void Initialize(FPNavMeshVisualizerData data)
        {
            _data = data;
            if (data == null || !data.IsLoaded) return;

            _simFrame = new Frame(MAX_AGENTS, null);

            _agentSystem = new FPNavAgentSystem(
                data.NavMesh, data.Query, data.Pathfinder, data.Funnel, data.Logger);

            _avoidance = new FPNavAvoidance();
            if (EnableAvoidance)
                _agentSystem.SetAvoidance(_avoidance);

            CurrentTick = 0;
            _accumulator = 0;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
        }

        public int AddAgent(Vector3 position)
        {
            if (_entityCount >= MAX_AGENTS) return -1;
            if (_data == null || !_data.IsLoaded || _simFrame == null) return -1;

            var entity = _simFrame.CreateEntity();
            _simFrame.Add(entity, default(NavAgentComponent));
            ref var nav = ref _simFrame.Get<NavAgentComponent>(entity);

            FPVector3 fpPos = position.ToFPVector3();
            NavAgentComponent.Init(ref nav, fpPos);
            nav.Speed = FP64.FromFloat(DefaultSpeed);
            nav.Radius = FP64.FromFloat(DefaultRadius);
            nav.Acceleration = FP64.FromFloat(DefaultAcceleration);
            nav.CurrentTriangleIndex = _data.FindTriangleAtPosition(position);

            int idx = _entityCount;
            _entities[idx] = entity;
            _initialPositions[idx] = position;
            _entityCount++;
            return idx;
        }

        public void RemoveAgent(int index)
        {
            if (index < 0 || index >= _entityCount) return;

            _entityCount--;
            if (index < _entityCount)
            {
                _entities[index] = _entities[_entityCount];
                _initialPositions[index] = _initialPositions[_entityCount];
            }
        }

        public void SetAgentDestination(int index, Vector3 dest)
        {
            if (index < 0 || index >= _entityCount || _simFrame == null) return;

            ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[index]);
            NavAgentComponent.SetDestination(ref nav, dest.ToFPVector3());
        }

        public void StopAgent(int index)
        {
            if (index < 0 || index >= _entityCount || _simFrame == null) return;

            ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[index]);
            NavAgentComponent.Stop(ref nav);
        }

        public void ClearAllAgents()
        {
            _entityCount = 0;
            IsRunning = false;
            if (_simFrame != null)
                _simFrame = new Frame(MAX_AGENTS, null);
        }

        public void Start()
        {
            if (_agentSystem == null) return;

            if (EnableAvoidance)
                _agentSystem.SetAvoidance(_avoidance);
            else
                _agentSystem.SetAvoidance(null);

            IsRunning = true;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
            _accumulator = 0;
        }

        public void Pause()
        {
            IsRunning = false;
        }

        public void Step()
        {
            if (_agentSystem == null || _entityCount == 0 || _simFrame == null) return;

            if (EnableAvoidance)
                _agentSystem.SetAvoidance(_avoidance);
            else
                _agentSystem.SetAvoidance(null);

            // Debug: record state before update
            var prevStatus = new byte[_entityCount];
            for (int i = 0; i < _entityCount; i++)
            {
                ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[i]);
                prevStatus[i] = nav.Status;
            }

            CurrentTick++;
            _agentSystem.Update(ref _simFrame, _entities, _entityCount, CurrentTick, _dt);

            // Debug: log status changes
            for (int i = 0; i < _entityCount; i++)
            {
                ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[i]);
                if (nav.Status != prevStatus[i])
                {
                    Debug.Log($"[NavMeshSim] Agent #{i}: {(FPNavAgentStatus)prevStatus[i]} -> {(FPNavAgentStatus)nav.Status}" +
                        $" (dest={nav.HasNavDestination}, path={nav.HasPath}, tri={nav.CurrentTriangleIndex})");
                }
                else if (prevStatus[i] == (byte)FPNavAgentStatus.PathPending)
                {
                    Debug.LogWarning($"[NavMeshSim] Agent #{i}: PathPending persists" +
                        $" (tri={nav.CurrentTriangleIndex}, dest={nav.Destination.ToVector3()})");
                }
            }

            UpdateLastOrcaAgent();
        }

        public void Reset()
        {
            IsRunning = false;
            CurrentTick = 0;
            _accumulator = 0;

            if (_simFrame == null) return;

            for (int i = 0; i < _entityCount; i++)
            {
                ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[i]);
                Vector3 pos = _initialPositions[i];
                NavAgentComponent.Init(ref nav, pos.ToFPVector3());
                nav.Speed = FP64.FromFloat(DefaultSpeed);
                nav.Radius = FP64.FromFloat(DefaultRadius);
                nav.Acceleration = FP64.FromFloat(DefaultAcceleration);
                if (_data != null)
                    nav.CurrentTriangleIndex = _data.FindTriangleAtPosition(pos);
            }

            ClearAllAgents();
        }

        public void OnEditorUpdate()
        {
            if (!IsRunning || _agentSystem == null || _entityCount == 0 || _simFrame == null) return;

            double now = EditorApplication.timeSinceStartup;
            double delta = now - _lastUpdateTime;
            _lastUpdateTime = now;

            if (delta > 0.1) delta = 0.1;

            _accumulator += delta * SimulationSpeed;

            bool updated = false;
            bool logOnce = (CurrentTick <= 1);
            while (_accumulator >= FIXED_DT)
            {
                _accumulator -= FIXED_DT;
                CurrentTick++;
                _agentSystem.Update(ref _simFrame, _entities, _entityCount, CurrentTick, _dt);
                updated = true;

                if (logOnce)
                {
                    logOnce = false;
                    for (int i = 0; i < _entityCount; i++)
                    {
                        ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[i]);
                        Debug.Log($"[NavMeshSim] Agent #{i}: status={(FPNavAgentStatus)nav.Status}" +
                            $" dest={nav.HasNavDestination} path={nav.HasPath}" +
                            $" tri={nav.CurrentTriangleIndex} pos={nav.Position.ToVector3()}");
                    }
                }
            }

            if (updated)
            {
                UpdateLastOrcaAgent();
                SceneView.RepaintAll();
            }
        }

        public struct AgentRenderData
        {
            public Vector3 position;
            public Vector2 velocity;
            public Vector2 desiredVelocity;
            public float radius;
            public float speed;
            public Vector3 destination;
            public bool hasDestination;
            public bool hasPath;
            public FPNavAgentStatus status;
            public int currentTriangleIndex;
            public int[] corridor;
            public int corridorLength;
        }

        public unsafe AgentRenderData GetAgentRenderData(int index)
        {
            if (index < 0 || index >= _entityCount || _simFrame == null)
                return default;

            ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[index]);

            var rd = new AgentRenderData
            {
                position = nav.Position.ToVector3(),
                velocity = nav.Velocity.ToVector2(),
                desiredVelocity = nav.DesiredVelocity.ToVector2(),
                radius = nav.Radius.ToFloat(),
                speed = nav.CurrentSpeed.ToFloat(),
                destination = nav.Destination.ToVector3(),
                hasDestination = nav.HasNavDestination,
                hasPath = nav.HasPath,
                status = (FPNavAgentStatus)nav.Status,
                currentTriangleIndex = nav.CurrentTriangleIndex,
            };

            if (nav.HasPath && nav.PathIsValid && nav.CorridorLength > 0)
            {
                rd.corridorLength = nav.CorridorLength;
                rd.corridor = new int[nav.CorridorLength];
                fixed (int* src = nav.Corridor)
                {
                    for (int i = 0; i < nav.CorridorLength; i++)
                        rd.corridor[i] = src[i];
                }
            }

            return rd;
        }

        private void UpdateLastOrcaAgent()
        {
            LastOrcaComputedAgentIndex = -1;
            if (_simFrame == null) return;

            for (int i = 0; i < _entityCount; i++)
            {
                ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[i]);
                if (nav.Status == (byte)FPNavAgentStatus.Moving)
                {
                    LastOrcaComputedAgentIndex = i;
                    break;
                }
            }
        }
    }
}
