using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Systems.Tests
{
    [TestFixture]
    public class CombatSystemTests
    {
        private const int MaxEntities = 32;
        private const int DeltaTimeMs = 50;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory configuration (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        private Frame CreateFrame()
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.DeltaTimeMs = DeltaTimeMs;
            return frame;
        }

        private EntityRef CreateAttacker(Frame frame, int ownerId, FPVector3 position, int attackDamage, int attackRange)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = position, Scale = FPVector3.One });
            frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            frame.Add(entity, new CombatComponent { AttackDamage = attackDamage, AttackRange = attackRange });
            return entity;
        }

        private EntityRef CreateTarget(Frame frame, int ownerId, FPVector3 position, int maxHealth)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = position, Scale = FPVector3.One });
            frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            frame.Add(entity, new HealthComponent { MaxHealth = maxHealth, CurrentHealth = maxHealth });
            return entity;
        }

        [Test]
        public void AttackInRange_ReducesTargetHealth()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 10, attackRange: 5);
            var target = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var health = ref frame.GetReadOnly<HealthComponent>(target);
            Assert.AreEqual(90, health.CurrentHealth, "Target should take 10 damage");
        }

        [Test]
        public void AttackOutOfRange_NoHealthReduction()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 10, attackRange: 5);
            var target = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var health = ref frame.GetReadOnly<HealthComponent>(target);
            Assert.AreEqual(100, health.CurrentHealth, "Out-of-range target should not take damage");
        }

        [Test]
        public void SameOwner_NoFriendlyFire()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 10, attackRange: 5);
            var ally = CreateTarget(frame, ownerId: 1, position: new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var health = ref frame.GetReadOnly<HealthComponent>(ally);
            Assert.AreEqual(100, health.CurrentHealth, "Friendly units should not take damage");
        }

        [Test]
        public void HealthFloor_NotBelowZero()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 200, attackRange: 5);
            var target = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var health = ref frame.GetReadOnly<HealthComponent>(target);
            Assert.AreEqual(0, health.CurrentHealth, "Health should not go below 0");
        }

        [Test]
        public void MultipleTargets_AllInRange_AllTakeDamage()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 10, attackRange: 10);
            var target1 = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), maxHealth: 100);
            var target2 = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var h1 = ref frame.GetReadOnly<HealthComponent>(target1);
            ref readonly var h2 = ref frame.GetReadOnly<HealthComponent>(target2);
            Assert.AreEqual(90, h1.CurrentHealth, "Target1 should take damage");
            Assert.AreEqual(90, h2.CurrentHealth, "Target2 should take damage");
        }

        [Test]
        public void MultipleTargets_MixedRange_OnlyInRangeTakeDamage()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 15, attackRange: 5);
            var inRange = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero), maxHealth: 100);
            var outOfRange = CreateTarget(frame, ownerId: 2, position: new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero), maxHealth: 100);

            var system = new CombatSystem();
            system.Update(ref frame);

            ref readonly var hIn = ref frame.GetReadOnly<HealthComponent>(inRange);
            ref readonly var hOut = ref frame.GetReadOnly<HealthComponent>(outOfRange);
            Assert.AreEqual(85, hIn.CurrentHealth, "In-range target should take damage");
            Assert.AreEqual(100, hOut.CurrentHealth, "Out-of-range target should not take damage");
        }

        [Test]
        public void Determinism_SameInput_SameHash()
        {
            var system = new CombatSystem();

            var frame1 = CreateFrame();
            var frame2 = CreateFrame();

            CreateAttacker(frame1, 1, FPVector3.Zero, 10, 5);
            CreateTarget(frame1, 2, new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), 100);

            CreateAttacker(frame2, 1, FPVector3.Zero, 10, 5);
            CreateTarget(frame2, 2, new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero), 100);

            for (int i = 0; i < 10; i++)
            {
                system.Update(ref frame1);
                system.Update(ref frame2);
            }

            Assert.AreEqual(frame1.CalculateHash(), frame2.CalculateHash(),
                "Identical frames should produce same hash");
        }

        [Test]
        public void NoHealthComponent_NotATarget()
        {
            var frame = CreateFrame();
            var attacker = CreateAttacker(frame, ownerId: 1, position: FPVector3.Zero, attackDamage: 10, attackRange: 5);

            // Entity without HealthComponent
            var nonTarget = frame.CreateEntity();
            frame.Add(nonTarget, new TransformComponent { Position = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero), Scale = FPVector3.One });
            frame.Add(nonTarget, new OwnerComponent { OwnerId = 2 });

            var system = new CombatSystem();
            Assert.DoesNotThrow(() => system.Update(ref frame), "Should not throw for entities without HealthComponent");
        }
    }
}
