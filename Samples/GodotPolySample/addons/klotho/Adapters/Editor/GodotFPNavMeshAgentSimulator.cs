// Editor-side NavMesh agent simulation. The core sim (Frame / FPNavAgentSystem / NavAgentComponent
// / FPNavAvoidance) is engine-agnostic; this wraps it with an editor fixed-step tick (delta-driven),
// GD diagnostics, and Godot.Vector3/Vector2 render data.
#if TOOLS
using global::Godot;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Godot
{
    internal unsafe class GodotFPNavMeshAgentSimulator
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
        // Diagnostic knob: multi-floor traversal threshold. Raising it lets the agent
        // cross steep single ramp triangles whose centerY differs by more than the default 2.0.
        public float MultiFloorYThreshold = 2.0f;
        // How far a destination click may be moved to reach ground the agent's plan mask allows.
        // Synced to the mesh's GridCellSize on Initialize, which is the distance the projection
        // ALWAYS covers: its fallback searches the click's cell plus the 8 around it, so anything
        // within one cell of the click is certain to be considered. Larger values still help but
        // depend on where in its cell the click landed, and past ~2.83 cells (the 3x3 block's far
        // corner) there is nothing left to find.
        public float DestinationSnapMaxDist = 1.0f;

        // Why the last destination click was refused, or null. Held as state rather than pushed to
        // GD: the symptom is a lasting condition ("it will not move"), and Unity's simulator shows
        // the same field, so both engines refuse in the same shape. NOT per agent: cleared on the
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
        private GodotFPNavMeshVisualizerData _data;
        private double _accumulator;
        private readonly FP64 _dt = FP64.FromDouble(1.0 / 60.0);
        private const double FIXED_DT = 1.0 / 60.0;

        // Remember initial positions (for reset)
        private Vector3[] _initialPositions = new Vector3[MAX_AGENTS];

        // Whether an agent's PathFailed predates the mesh currently installed. Read after a swap,
        // which is enough: the reseed revives Moving/PathPending/Blocked but NOT PathFailed, and
        // the replan refuses to touch a PathFailed agent — so anything still there crossed the
        // swap. The endpoints alone cannot tell that from a genuine "no route".
        private bool[] _failurePredatesSwap = new bool[MAX_AGENTS];

        public void Initialize(GodotFPNavMeshVisualizerData data)
        {
            _data = data;
            if (data == null || !data.IsLoaded) return;

            _simFrame = new Frame(MAX_AGENTS, null);
            _agentSystem = new FPNavAgentSystem(
                data.NavMesh, data.Query, data.Pathfinder, data.Funnel, null);
            _agentSystem.MultiFloorYThreshold = FP64.FromFloat(MultiFloorYThreshold);

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

        public void SetMultiFloorYThreshold(float v)
        {
            MultiFloorYThreshold = v;
            if (_agentSystem != null)
                _agentSystem.MultiFloorYThreshold = FP64.FromFloat(v);
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

        /// <summary>
        /// Sets a destination the agent can actually be given, snapping the click to the nearest
        /// ground its PLAN mask allows. Returns false when no such ground is within
        /// <see cref="DestinationSnapMaxDist"/>, in which case the agent is stopped and
        /// <see cref="LastDestinationRefusal"/> says why.
        ///
        /// <para><b>Why the raw click could not be used</b>: a retained building is ON the mesh and
        /// carries <c>FPNavMeshAreas.BUILDING_MASK</c>, so a click on it produced a destination
        /// that <c>FindPath</c> refuses by mask. The agent then sat at <c>PathFailed</c> with a
        /// destination and no corridor — indistinguishable on screen from being stuck.</para>
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
        /// the old masks planned; the agent replans on the next tick.</para>
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
            _agentSystem.SetAvoidance(EnableAvoidance ? _avoidance : null);
            IsRunning = true;
            _accumulator = 0;
        }

        public void Pause() => IsRunning = false;

        /// <summary>
        /// Installs a rebaked mesh WITHOUT rebuilding the simulation — the engine's own protocol,
        /// so an editor experiment survives a building being placed. Mirrors the Unity simulator.
        ///
        /// <para><c>Initialize</c> is the alternative and it is the wrong one: it recreates the
        /// frame and resets the tick, wiping the agents whose behaviour is the thing being looked
        /// at. <c>FPNavAgentInstaller.Swap</c> rebinds the query trio, drops the graph-local
        /// obstacle CSR, re-extracts the ORCA obstacles (a carve adds a hole ring, so that set
        /// really does change) and re-collects the agents; <c>ReseedAgents</c> then re-queries
        /// every agent's triangle index on the new mesh. Skip the reseed and agents keep indices
        /// into the mesh that was just replaced — not an exception, they simply walk elsewhere.</para>
        /// </summary>
        public bool SwapNavMesh(FPNavMesh newMesh)
        {
            if (_agentSystem == null || _simFrame == null || newMesh == null)
                return false;

            int collected = FPNavAgentInstaller.Swap(
                ref _simFrame, _agentSystem, newMesh, ref _entities);
            _agentSystem.ReseedAgents(ref _simFrame, _entities, collected);
            _entityCount = collected;

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

        public void Step()
        {
            if (_agentSystem == null || _entityCount == 0 || _simFrame == null) return;
            _agentSystem.SetAvoidance(EnableAvoidance ? _avoidance : null);

            CurrentTick++;
            _agentSystem.Update(ref _simFrame, _entities, _entityCount, CurrentTick, _dt);
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

        /// <summary>
        /// Advances the fixed-step accumulator by the editor frame delta.
        /// Returns true if at least one simulation tick ran (caller should refresh the overlay).
        /// </summary>
        public bool OnEditorUpdate(double delta)
        {
            if (!IsRunning || _agentSystem == null || _entityCount == 0 || _simFrame == null) return false;

            if (delta > 0.1) delta = 0.1;
            _accumulator += delta * SimulationSpeed;

            bool updated = false;
            while (_accumulator >= FIXED_DT)
            {
                _accumulator -= FIXED_DT;
                CurrentTick++;
                _agentSystem.Update(ref _simFrame, _entities, _entityCount, CurrentTick, _dt);
                updated = true;
            }

            if (updated)
                UpdateLastOrcaAgent();
            return updated;
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
            // PathFailed. An enum, not a string: the dock owns the wording.
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

        public AgentRenderData GetAgentRenderData(int index)
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
                failureReason = DiagnosePathFailure(nav, _failurePredatesSwap[index]),
                walkAreaMask = nav.WalkAreaMaskOverride,
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
#endif
