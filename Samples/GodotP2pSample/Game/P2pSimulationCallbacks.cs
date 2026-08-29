using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace xpTURN.Samples.P2pSample
{
    public class P2pSimulationCallbacks : ISimulationCallbacks, IFPPhysicsProviderSource
    {
        private readonly P2pInputCapture _input;
        private readonly int _stageId;
        private IKlothoEngine _engine;
        private EcsSimulation _simulation;

        // Exposes the live physics world to debug visualizers (FPPhysicsWorldVisualizer / Godot equivalent).
        public IFPPhysicsWorldProvider PhysicsProvider => _simulation?.GetSystem<PhysicsSystem>();

        // stageId is host-chosen and propagated via SimulationConfig (P2pGameNode passes config.StageId in
        // the callbacks factory). Host and guest build the same stage's static geometry → identical BVH.
        public P2pSimulationCallbacks(P2pInputCapture input, int stageId = 0)
        {
            _input = input;
            _stageId = stageId;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            _simulation = simulation;
            var events = new EventSystem();
            simulation.AddSystem(new CommandSystem(),  SystemPhase.PreUpdate);
            simulation.AddSystem(new MovementSystem(), SystemPhase.PreUpdate);
            var physics = new PhysicsSystem(64);
            physics.LoadStaticColliders("", new List<FPStaticCollider> { CreateGroundCollider(_stageId) });
            simulation.AddSystem(physics,              SystemPhase.Update);
            simulation.AddSystem(new RespawnSystem(),  SystemPhase.LateUpdate);
            simulation.AddSystem(new ScoreSystem(),    SystemPhase.LateUpdate);
            simulation.AddSystem(events,               SystemPhase.LateUpdate);
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            _engine = engine;
            var frame = engine.InitFrame;
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
            int maxPlayers = engine.SessionConfig.MaxPlayers;

            for (int i = 0; i < maxPlayers; i++)
            {
                FP64 offsetX = (i == 0) ? -stats.InitialSpawnOffsetX : stats.InitialSpawnOffsetX;
                FPVector3 initialPos = new FPVector3(
                    stats.SpawnPoint.x + offsetX,
                    stats.SpawnPoint.y,
                    stats.SpawnPoint.z);
                FPVector3 halfExt = new FPVector3(stats.PlayerHalfExtent, stats.PlayerHalfExtent, stats.PlayerHalfExtent);

                var entity = frame.CreateEntity();
                frame.Add(entity, new TransformComponent
                {
                    Position = initialPos,
                    Rotation = FP64.Zero,
                    Scale = FPVector3.One,
                });
                frame.Add(entity, new PhysicsBodyComponent
                {
                    RigidBody = FPRigidBody.CreateDynamic(stats.PlayerMass),
                    Collider = FPCollider.FromBox(new FPBoxShape(halfExt, FPVector3.Zero)),
                    ColliderOffset = FPVector3.Zero,
                });
                frame.Add(entity, new PlayerComponent
                {
                    PlayerId = i,
                    Score = 0,
                    LastInputH = FP64.Zero,
                    LastInputV = FP64.Zero,
                });
                frame.Add(entity, new ErrorCorrectionTargetComponent());
            }
        }

        // Ground static collider — box at Y=-0.1 (top face at Y=0). Per-stage geometry: stage 0 keeps the
        // original 10×0.2×10; other stages vary the half-extent so the static BVH fingerprint differs per
        // stage (the multi-stage marker). A real game maps stageId → a baked stage definition (DataAsset).
        static FPStaticCollider CreateGroundCollider(int stageId)
        {
            FP64 halfXZ = stageId == 1 ? FP64.FromInt(3) : FP64.FromInt(5);
            return new FPStaticCollider
            {
                id = 1,
                isTrigger = false,
                restitution = FP64.Zero,
                friction = FP64.FromFloat(0.5f),
                collider = FPCollider.FromBox(new FPBoxShape(
                    halfExtents: new FPVector3(halfXZ, FP64.FromFloat(0.1f), halfXZ),
                    position: new FPVector3(FP64.Zero, FP64.FromFloat(-0.1f), FP64.Zero))),
            };
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            if (_engine == null) return;
            if (playerId != _engine.LocalPlayerId) return;

            var cmd = CommandPool.Get<MoveCommand>();
            cmd.PlayerId = playerId;
            cmd.H = _input.H;
            cmd.V = _input.V;
            sender.Send(cmd);
        }

        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { } // no per-join world state to seed
    }
}
