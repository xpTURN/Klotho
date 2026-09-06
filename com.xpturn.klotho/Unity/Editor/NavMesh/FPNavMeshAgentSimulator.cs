using UnityEditor;
using UnityEngine;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Simulates NavMesh agent pathfinding in the editor.
    /// Uses a lightweight Frame + NavAgentComponent.
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
        // Bake inset carried by the loaded mesh boundary. Synced from the asset's recorded
        // BakeAgentRadius on Initialize; edit live (SetObstacleRadiusInset) to override for
        // diagnosis (0 = uncorrected double clearance, reproducing the edge slowdown).
        public float ObstacleRadiusInset = 0f;
        // How far a destination click may be moved to reach ground the agent's plan mask allows.
        // Synced to the mesh's GridCellSize on Initialize, which is the distance the projection
        // ALWAYS covers: its fallback searches the click's cell plus the 8 around it, so anything
        // within one cell of the click is certain to be considered. Larger values still help but
        // depend on where in its cell the click landed, and past ~2.83 cells (the 3x3 block's far
        // corner) there is nothing left to find.
        public float DestinationSnapMaxDist = 1.0f;

        // Why the last destination click was refused, or null. Held here rather than logged
        // because the Godot simulator has no status log, and because the symptom is a lasting
        // state ("it will not move") rather than a one-shot event. NOT per agent: cleared on the
        // next successful destination so a stale reason cannot linger after another agent works.
        public string LastDestinationRefusal;

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

        // Whether an agent's PathFailed predates the mesh currently installed. Set by reading the
        // status AFTER a swap, which is enough: the reseed revives Moving/PathPending/Blocked but
        // NOT PathFailed, and ProcessPathRequest refuses to re-plan a PathFailed agent — so
        // anything still sitting there crossed the swap. Distinguishing that from a genuine
        // "no route" is impossible from the endpoints alone (both leave every endpoint check
        // passing), which is why this is a timestamp rather than another geometry query.
        private bool[] _failurePredatesSwap = new bool[MAX_AGENTS];

        public void Initialize(FPNavMeshVisualizerData data)
        {
            _data = data;
            if (data == null || !data.IsLoaded) return;

            _simFrame = new Frame(MAX_AGENTS, null);

            _agentSystem = new FPNavAgentSystem(
                data.NavMesh, data.Query, data.Pathfinder, data.Funnel, data.Logger);

            _avoidance = new FPNavAvoidance();
            // Load the NavMesh boundary as ORCA static obstacles once (retained on _avoidance across
            // enable/disable toggles), so the ORCA-lines overlay shows wall half-planes, not just
            // agent-agent ones. Set avoidance to load, then honor the EnableAvoidance toggle.
            // LoadNavMeshObstacles applies the asset's recorded bake radius as the obstacle inset;
            // sync the knob so the UI shows the applied value (still editable as an override).
            _agentSystem.SetAvoidance(_avoidance);
            _agentSystem.LoadNavMeshObstacles();
            ObstacleRadiusInset = _avoidance.ObstacleRadiusInset.ToFloat();
            if (!EnableAvoidance)
                _agentSystem.SetAvoidance(null);

            DestinationSnapMaxDist = data.NavMesh.GridCellSize.ToFloat();
            LastDestinationRefusal = null;

            CurrentTick = 0;
            _accumulator = 0;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>
        /// Installs a rebaked mesh WITHOUT rebuilding the simulation — the engine's own swap
        /// protocol, so an editor experiment survives a building being placed.
        ///
        /// <para><see cref="Initialize"/> is the alternative and it is the wrong one here: it
        /// recreates the frame and resets the tick, so every placement would wipe the agents whose
        /// behaviour is the thing being looked at. What this does instead is what a match does —
        /// <c>FPNavAgentInstaller.Swap</c> rebinds the query/pathfinder/funnel, drops the
        /// graph-local obstacle CSR, re-extracts the ORCA obstacles (a carve adds a hole ring, so
        /// that set really does change) and re-collects the agent list; then <c>ReseedAgents</c>
        /// re-queries every agent's triangle index on the new mesh. Skipping the reseed leaves
        /// agents holding indices into the mesh that was just replaced, which is not an exception —
        /// they simply walk somewhere else.</para>
        ///
        /// <para>Returns false when there is nothing to swap into (no simulation built yet); the
        /// caller then has no agents to worry about either.</para>
        /// </summary>
        public bool SwapNavMesh(FPNavMesh newMesh)
        {
            if (_agentSystem == null || _simFrame == null || newMesh == null)
                return false;

            int collected = FPNavAgentInstaller.Swap(
                ref _simFrame, _agentSystem, newMesh, ref _entities);
            _agentSystem.ReseedAgents(ref _simFrame, _entities, collected);
            _entityCount = collected;

            // Anything at PathFailed now crossed the swap (see the field's remarks). Read after the
            // reseed rather than before it, so this needs no index bookkeeping across the
            // re-collection Swap performs.
            for (int i = 0; i < collected; i++)
            {
                ref readonly var nav = ref _simFrame.GetReadOnly<NavAgentComponent>(_entities[i]);
                _failurePredatesSwap[i] = nav.Status == (byte)FPNavAgentStatus.PathFailed;
            }

            // Re-apply the inset the tool is holding. The swap runs LoadNavMeshObstacles again,
            // which re-derives ObstacleRadiusInset from the new mesh's BakeAgentRadius — correct
            // for a game (the asset is what keeps lockstep peers symmetric) and wrong for a
            // diagnosis, because a rebake inherits the base's radius and so every building placed
            // would silently undo the knob mid-experiment. The mirror field IS the bake radius
            // when nobody has touched it, so this is a no-op unless it was overridden.
            _avoidance.ObstacleRadiusInset = FP64.FromFloat(ObstacleRadiusInset);

            // Every placement and removal comes through here, so a refusal the OLD mesh caused
            // must not outlive it — the banner quoted that mesh's cell size and stayed up after
            // the building responsible for it was removed.
            LastDestinationRefusal = null;
            return true;
        }

        /// <summary>
        /// Live knob: writes through to the retained avoidance so toggling the inset (e.g. 0 vs
        /// bake radius) is visible without reloading the mesh.
        /// </summary>
        public void SetObstacleRadiusInset(float v)
        {
            ObstacleRadiusInset = v;
            if (_avoidance != null)
                _avoidance.ObstacleRadiusInset = FP64.FromFloat(v);
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
            // Third parallel array, same slot. A reused slot must not inherit the verdict of the
            // agent that occupied it — the tool would then report a fresh agent's genuine failure
            // as one that predates a swap, and offer an action that changes nothing.
            _failurePredatesSwap[idx] = false;
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
                // The tail slid down here, so its verdict has to slide with it. Leaving this array
                // out of the compaction is what let the moved agent inherit the removed one's.
                _failurePredatesSwap[index] = _failurePredatesSwap[_entityCount];
            }
            // The banner is not per agent, so removing an agent can strand the reason it showed.
            LastDestinationRefusal = null;
        }

        /// <summary>
        /// Sets a destination the agent can actually be given, snapping the click to the nearest
        /// ground its PLAN mask allows. Returns false when no such ground is within
        /// <see cref="DestinationSnapMaxDist"/>, in which case the agent is stopped and
        /// <see cref="LastDestinationRefusal"/> says why.
        ///
        /// <para><b>Why the raw click could not be used</b>: a retained building is ON the mesh and
        /// carries <c>FPNavMeshAreas.BUILDING_MASK</c>, so a click on it produced a destination
        /// that <c>FindPath</c> refuses by mask. The agent then sat at <c>PathFailed</c> with a
        /// destination and no corridor, and the screen said "it has somewhere to go and will not
        /// move" — indistinguishable from being stuck.</para>
        ///
        /// <para>The mask comes from <see cref="FPNavAgentSystem.ResolvePlanMask"/> rather than a
        /// local <c>!= 0 ?</c>, because this tool's whole purpose includes
        /// <see cref="SetAgentAreaMask"/>: the override is normally set here, and a wrong fold of
        /// zero would make every triangle impassable with the same symptom this method fixes.</para>
        ///
        /// <para>Y is resampled at the snapped XZ. Keeping the click's own y would pair a projected
        /// XZ with an unrelated height, which on a multi-floor mesh puts the destination on the
        /// wrong floor.</para>
        /// </summary>
        public bool SetAgentDestination(int index, FPVector3 dest)
        {
            // The contract this method's callers rely on is "false means LastDestinationRefusal
            // says why". These two returned false silently, so a click on a selected-then-removed
            // agent either printed nothing or reprinted an older refusal that named the snap knob
            // for a problem that had nothing to do with it.
            if (index < 0 || index >= _entityCount || _simFrame == null)
            {
                LastDestinationRefusal = _entityCount == 0
                    ? "No agent to give a destination to — place one first"
                    : $"No agent #{index} — the selection is stale (there are {_entityCount})";
                return false;
            }
            if (_data == null || _data.Query == null)
            {
                LastDestinationRefusal = "No navmesh loaded";
                return false;
            }

            ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[index]);
            int areaMask = FPNavAgentSystem.ResolvePlanMask(nav);

            var destXZ = new FPVector2(dest.x, dest.z);
            FP64 maxDist = FP64.FromFloat(DestinationSnapMaxDist);

            FPVector3 target;
            // The ENGINE's endpoint rule, not the y-blind one. FindPassableTriangle ignores height,
            // so on a multi-floor mesh a click on forbidden ground with walkable ground beneath it
            // was accepted as-is and kept the click's own y — the snap below never ran, and
            // FindPath then refused the destination by mask.
            if (_data.Query.FindPassableTriangleForEndpoint(destXZ, dest.y, areaMask) >= 0)
            {
                target = dest;
            }
            else
            {
                FPVector2 snapped = _data.Query.ProjectToPassable(
                    destXZ, maxDist, areaMask, out int triIdx);
                if (triIdx < 0)
                {
                    // Stop rather than leave the previous destination running: the click would
                    // otherwise be refused while the agent kept walking to wherever it was already
                    // headed, which reads as "I clicked and it went somewhere else". Stop also puts
                    // the status at Idle, so a refusal is distinguishable on screen from the
                    // PathFailed this whole change exists to remove.
                    NavAgentComponent.Stop(ref nav);
                    _failurePredatesSwap[index] = false;
                    LastDestinationRefusal =
                        $"No passable ground within snap distance {DestinationSnapMaxDist:F2} "
                        + $"(the search covers one cell ring; ground closer than "
                        + $"{_data.NavMesh.GridCellSize.ToFloat():F2} is always found)";
                    return false;
                }

                FP64 height = _data.Query.SampleHeight(snapped, triIdx);
                target = new FPVector3(snapped.x, height, snapped.y);
            }

            NavAgentComponent.SetDestination(ref nav, target);
            LastDestinationRefusal = null;
            _failurePredatesSwap[index] = false;   // a fresh destination gets a fresh verdict
            return true;
        }

        public void StopAgent(int index)
        {
            if (index < 0 || index >= _entityCount || _simFrame == null) return;

            ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[index]);
            NavAgentComponent.Stop(ref nav);
        }

        /// <summary>
        /// Gives ONE agent its own plan and walk area masks, so a scene can hold agents that
        /// disagree about which ground they may use. Pass 0 for either to mean "no override".
        ///
        /// <para>Per agent rather than a simulator-wide default because the combination worth
        /// looking at is two agents on the same mesh with the same destination and different walk
        /// masks — one enters a retained footprint and the other stops at its edge on the same
        /// tick. A global setting could only produce that by being changed between spawns.</para>
        ///
        /// <para>Goes through <c>NavAgentComponent.SetAreaMask</c>, which also drops the corridor
        /// the old masks planned; the agent replans on the next tick. That is the behaviour to
        /// watch when narrowing a mask under an agent that is already walking.</para>
        /// </summary>
        public void SetAgentAreaMask(int index, int planMask, int walkMask)
        {
            if (index < 0 || index >= _entityCount || _simFrame == null) return;

            ref var nav = ref _simFrame.Get<NavAgentComponent>(_entities[index]);
            NavAgentComponent.SetAreaMask(ref nav, planMask, walkMask);
        }

        public void ClearAllAgents()
        {
            _entityCount = 0;
            IsRunning = false;
            LastDestinationRefusal = null;
            System.Array.Clear(_failurePredatesSwap, 0, _failurePredatesSwap.Length);
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
            // The agent's own masks, so a mixed scene can be told apart in the list. 0 = no
            // override, i.e. FPNavAgentSystem.DEFAULT_AREA_MASK.
            public int planAreaMask;
            public int walkAreaMask;
            // Why PathFailed, judged against the mesh as it is NOW. None unless the status is
            // PathFailed. An enum rather than a string so the window owns the wording and the
            // pattern tests can compare exactly.
            public FPNavPathFailureReason failureReason;
            public int[] corridor;
            public int corridorLength;
        }

        /// <summary>
        /// Why this agent sits at <c>PathFailed</c> — the shared runtime diagnosis, handed the
        /// tool's own swap bookkeeping. See <see cref="FPNavPathFailure.Diagnose"/> for the order
        /// the refusals are walked in and why the START's mask is deliberately absent.
        /// </summary>
        internal FPNavPathFailureReason DiagnosePathFailure(
            in NavAgentComponent nav, bool failurePredatesSwap)
            => FPNavPathFailure.Diagnose(
                nav, _data?.Query, _data?.NavMesh, failurePredatesSwap);

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
                planAreaMask = nav.PlanAreaMaskOverride,
                walkAreaMask = nav.WalkAreaMaskOverride,
                failureReason = DiagnosePathFailure(nav, _failurePredatesSwap[index]),
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
