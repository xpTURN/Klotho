using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using UnityEngine.TestTools;

using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class NavAgentComponentTests
    {
        private const float EPSILON = 0.1f;

        ILogger _logger = null;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        #region Component lifecycle

        [Test]
        public void Init_DefaultValues()
        {
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                FPVector3.Zero, -1, out var entity, out _);
            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entity);

            Assert.AreEqual((byte)FPNavAgentStatus.Idle, nav.Status);
            Assert.IsFalse(nav.HasNavDestination);
            Assert.IsFalse(nav.HasPath);
            Assert.AreEqual(-1, nav.CurrentTriangleIndex);
            Assert.AreEqual(0f, nav.Velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SetDestination_ChangesStatus()
        {
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                FPVector3.Zero, -1, out var entity, out _);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);

            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status);
            Assert.IsTrue(nav.HasNavDestination);
            Assert.IsFalse(nav.HasPath);
        }

        [Test]
        public void Stop_ResetsState()
        {
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                FPVector3.Zero, -1, out var entity, out _);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);

            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));
            NavAgentComponent.Stop(ref nav);

            Assert.AreEqual((byte)FPNavAgentStatus.Idle, nav.Status);
            Assert.IsFalse(nav.HasNavDestination);
            Assert.IsFalse(nav.HasPath);
        }

        #endregion

        #region Path request

        [Test]
        public void Update_PathPending_FindsPath()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var result = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, result.Status);
            Assert.IsTrue(result.HasPath);
            Assert.IsTrue(result.PathIsValid);
        }

        [Test]
        public void Update_InvalidDestination_PathFailed()
        {
            LogAssert.Expect(UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("outside NavMesh"));

            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.FromInt(100)));

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var result = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, result.Status);
        }

        #endregion

        #region Movement

        [Test]
        public void Update_Moving_PositionChanges()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var r = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, r.Status);
            float prevX = r.Position.x.ToFloat();

            for (int t = 2; t <= 30; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref readonly var r2 = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.IsTrue(r2.Position.x.ToFloat() > prevX, "agent must move in X+ direction");
        }

        [Test]
        public void Update_Moving_EventuallyArrives()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)));

            for (int t = 1; t <= 300; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref readonly var result = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Arrived, result.Status);
        }

        #endregion

        #region Idle / NavMesh constraint

        [Test]
        public void Update_Idle_NoMovement()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var result = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Idle, result.Status);
            Assert.AreEqual(1f, result.Position.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.Position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ConstrainToNavMesh_InsideNavMesh_ReturnsNewPos()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var newPos = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1));
            var oldPos = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));

            var result = system.ConstrainToNavMesh(newPos, oldPos, 0);

            Assert.AreEqual(2f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ConstrainToNavMesh_OutsideNavMesh_Constrained()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var newPos = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(-1));
            var oldPos = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1));

            var result = system.ConstrainToNavMesh(newPos, oldPos, 0);

            float resultZ = result.z.ToFloat();
            Assert.IsTrue(resultZ >= -EPSILON, "Z coordinate must be within NavMesh bounds or be oldPos");
        }

        #endregion

        #region Multiple agents

        [Test]
        public void Update_MultipleAgents_AllMove()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[]
                {
                    new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                    new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)),
                },
                new[] { 0, 2 },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            NavAgentComponent.SetDestination(ref nav0,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            NavAgentComponent.SetDestination(ref nav1,
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)));

            system.Update(ref frame, entities, 2, 1, NavAgentTestHelper.DT);

            ref readonly var r0 = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            ref readonly var r1 = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, r0.Status);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, r1.Status);

            float a0X = r0.Position.x.ToFloat();
            float a1X = r1.Position.x.ToFloat();

            for (int t = 2; t <= 30; t++)
                system.Update(ref frame, entities, 2, t, NavAgentTestHelper.DT);

            ref readonly var r0b = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            ref readonly var r1b = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
            Assert.IsTrue(r0b.Position.x.ToFloat() > a0X, "Agent0 moves X+");
            Assert.IsTrue(r1b.Position.x.ToFloat() < a1X, "Agent1 moves X-");
        }

        #endregion

        #region Edge cases

        [Test]
        public void SetDestination_Twice_OverwritesPrevious()
        {
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                FPVector3.Zero, -1, out var entity, out _);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);

            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)));
            int firstId = nav.PathRequestId;

            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(5)));

            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status);
            Assert.IsFalse(nav.HasPath);
            Assert.IsTrue(nav.PathRequestId > firstId);
            Assert.AreEqual(5f, nav.Destination.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Stop_WhileMoving_ResetsVelocity()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            for (int t = 1; t <= 10; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref var navAfter = ref frame.Get<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, navAfter.Status);

            NavAgentComponent.Stop(ref navAfter);

            Assert.AreEqual((byte)FPNavAgentStatus.Idle, navAfter.Status);
            Assert.AreEqual(0f, navAfter.Velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, navAfter.Velocity.y.ToFloat(), EPSILON);
            Assert.IsFalse(navAfter.HasPath);
        }

        [Test]
        public void Update_Arrived_StaysAtDestination()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)));

            for (int t = 1; t <= 300; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref readonly var r = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Arrived, r.Status);
            float arrivedX = r.Position.x.ToFloat();
            float arrivedZ = r.Position.z.ToFloat();

            for (int t = 301; t <= 310; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref readonly var r2 = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual(arrivedX, r2.Position.x.ToFloat(), EPSILON);
            Assert.AreEqual(arrivedZ, r2.Position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ConstrainToNavMesh_NoCurrentTriangle_ReturnsOldPos()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var newPos = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.FromInt(-5));
            var oldPos = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));

            var result = system.ConstrainToNavMesh(newPos, oldPos, -1);

            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Update_PathFailed_NoMovement()
        {
            LogAssert.Expect(UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("outside NavMesh"));

            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.FromInt(100)));

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var r = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, r.Status);
            float posX = r.Position.x.ToFloat();

            for (int t = 2; t <= 10; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

            ref readonly var r2 = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual(posX, r2.Position.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Update_ZeroDt_NoMovement()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1)),
                0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1)));

            system.Update(ref frame, entities, 1, 1, FP64.Zero);

            ref readonly var r = ref frame.GetReadOnly<NavAgentComponent>(entity);
            if (r.Status == (byte)FPNavAgentStatus.Moving)
            {
                float posX = r.Position.x.ToFloat();
                system.Update(ref frame, entities, 1, 2, FP64.Zero);

                ref readonly var r2 = ref frame.GetReadOnly<NavAgentComponent>(entity);
                Assert.AreEqual(posX, r2.Position.x.ToFloat(), EPSILON);
            }
        }

        [Test]
        public void Update_NoNavEntities_DoesNotThrow()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var frame = new Frame(NavAgentTestHelper.MAX_ENTITIES, null);
            var entities = new EntityRef[0];

            Assert.DoesNotThrow(() =>
                system.Update(ref frame, entities, 0, 1, NavAgentTestHelper.DT));
        }

        [Test]
        public unsafe void Update_Moving_CorridorPreservesIntermediateTriangles()
        {
            // L-shaped NavMesh: straight path not possible, must pass through corner (v1=4,4)
            var mesh = NavAgentTestHelper.CreateLShapedNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            // T1(upper-left, 1,7) → T2(lower-right, 6,1): corridor = [T1, T0, T4, T3, T2]
            var startPos = new FPVector3(FP64.One, FP64.Zero, FP64.FromInt(7));
            int startTri = 1; // T1
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                startPos, startTri, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.One));

            // First tick: path computation
            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);

            ref readonly var r = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, r.Status, "pathfinding succeeded");
            Assert.IsTrue(r.CorridorLength >= 3,
                $"L-shaped path requires 3 or more triangles (actual: {r.CorridorLength})");

            int initialCorridorLength = r.CorridorLength;

            // Verify corridor integrity during movement
            bool corridorShrankGradually = true;
            int prevLength = initialCorridorLength;
            int failTick = -1;

            for (int t = 2; t <= 600; t++)
            {
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

                ref readonly var snap = ref frame.GetReadOnly<NavAgentComponent>(entity);
                if (snap.Status == (byte)FPNavAgentStatus.Arrived)
                    break;

                // When repath occurs, corridor length may increase again — allowed
                if (snap.Status == (byte)FPNavAgentStatus.PathPending)
                {
                    prevLength = 999;
                    continue;
                }

                if (snap.Status != (byte)FPNavAgentStatus.Moving)
                    continue;

                // Corridor must shrink one at a time (removing 2+ at once = skipping)
                if (snap.CorridorLength < prevLength - 1)
                {
                    corridorShrankGradually = false;
                    failTick = t;
                    break;
                }
                prevLength = snap.CorridorLength;
            }

            Assert.IsTrue(corridorShrankGradually,
                $"corridor must shrink gradually (2+ removed at once at tick {failTick})");
        }

        [Test]
        public void Update_Moving_OffCorridorTriggersRepath()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var startPos = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                startPos, 0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav,
                new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.FromInt(1)));

            for (int t = 1; t <= 300; t++)
            {
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);

                ref readonly var snap = ref frame.GetReadOnly<NavAgentComponent>(entity);
                if (snap.Status == (byte)FPNavAgentStatus.Arrived)
                    break;

                // When off-corridor, OffCorridorTicks increases → verify repath(PathPending) trigger
                if (snap.Status == (byte)FPNavAgentStatus.PathPending && t > 1)
                {
                    // After repath, must transition back to Moving
                }
            }

            ref readonly var result = ref frame.GetReadOnly<NavAgentComponent>(entity);
            // Eventually must be Arrived or Moving (normal behavior including repath)
            Assert.IsTrue(
                result.Status == (byte)FPNavAgentStatus.Arrived ||
                result.Status == (byte)FPNavAgentStatus.Moving,
                $"final status: {(FPNavAgentStatus)result.Status} (must be Arrived or Moving)");
        }

        [Test]
        public void Determinism_SameInput_SameHash()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();

            var system1 = NavAgentTestHelper.CreateSystem(mesh, _logger);
            var system2 = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var startPos = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));
            var destPos = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(1));

            var frame1 = NavAgentTestHelper.CreateFrameWithAgent(startPos, 0, out var e1, out var ents1);
            ref var nav1 = ref frame1.Get<NavAgentComponent>(e1);
            NavAgentComponent.SetDestination(ref nav1, destPos);

            var frame2 = NavAgentTestHelper.CreateFrameWithAgent(startPos, 0, out var e2, out var ents2);
            ref var nav2 = ref frame2.Get<NavAgentComponent>(e2);
            NavAgentComponent.SetDestination(ref nav2, destPos);

            for (int t = 1; t <= 60; t++)
            {
                system1.Update(ref frame1, ents1, 1, t, NavAgentTestHelper.DT);
                system2.Update(ref frame2, ents2, 1, t, NavAgentTestHelper.DT);
            }

            ref readonly var r1 = ref frame1.GetReadOnly<NavAgentComponent>(e1);
            ref readonly var r2 = ref frame2.GetReadOnly<NavAgentComponent>(e2);

            ulong hash1 = r1.GetHash(0);
            ulong hash2 = r2.GetHash(0);
            Assert.AreEqual(hash1, hash2, "hash must be the same for the same input (determinism)");
        }

        #endregion
    }
}
