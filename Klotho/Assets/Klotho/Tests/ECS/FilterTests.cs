using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class FilterTests
    {
        private Frame _frame;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(16, _logger);
        }

        // --- Filter<T1> ---

        [Test]
        public void Filter1_IteratesAllEntitiesWithComponent()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e2, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            // e1 has no HealthComponent

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.Exists(e => e.Index == e0.Index));
            Assert.IsTrue(results.Exists(e => e.Index == e2.Index));
        }

        [Test]
        public void Filter1_EmptyResult_WhenNoComponents()
        {
            _frame.CreateEntity();
            _frame.CreateEntity();

            var filter = _frame.Filter<HealthComponent>();
            Assert.IsFalse(filter.Next(out _));
        }

        [Test]
        public void Filter1_SkipsDeadEntities()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });

            _frame.DestroyEntity(e0);

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(e1.Index, results[0].Index);
        }

        // --- Filter<T1, T2> ---

        [Test]
        public void Filter2_ReturnsIntersection()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();

            // e0: Health + Owner
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });

            // e1: Health only
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });

            // e2: Owner only
            _frame.Add(e2, new OwnerComponent { OwnerId = 2 });

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(e0.Index, results[0].Index);
        }

        [Test]
        public void Filter2_EmptyIntersection()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e1, new OwnerComponent { OwnerId = 1 });

            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            Assert.IsFalse(filter.Next(out _));
        }

        [Test]
        public void Filter2_CanAccessComponentsViaFrame()
        {
            var e0 = _frame.CreateEntity();
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 42 });

            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            Assert.IsTrue(filter.Next(out var entity));

            ref var health = ref _frame.Get<HealthComponent>(entity);
            ref var owner = ref _frame.Get<OwnerComponent>(entity);

            Assert.AreEqual(75, health.CurrentHealth);
            Assert.AreEqual(42, owner.OwnerId);
        }

        [Test]
        public void Filter2_ModifyComponentDuringIteration()
        {
            var e0 = _frame.CreateEntity();
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });

            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            while (filter.Next(out var entity))
            {
                ref var health = ref _frame.Get<HealthComponent>(entity);
                health.CurrentHealth -= 10;
            }

            Assert.AreEqual(90, _frame.Get<HealthComponent>(e0).CurrentHealth);
        }

        // --- Filter<T1, T2, T3> ---

        [Test]
        public void Filter3_ReturnsTripleIntersection()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();

            // e0: Health + Owner + Combat
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });
            _frame.Add(e0, new CombatComponent { AttackDamage = 10, AttackRange = 2 });

            // e1: Health + Owner (no Combat)
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            _frame.Add(e1, new OwnerComponent { OwnerId = 2 });

            // e2: Health + Combat (no Owner)
            _frame.Add(e2, new HealthComponent { MaxHealth = 150, CurrentHealth = 150 });
            _frame.Add(e2, new CombatComponent { AttackDamage = 5, AttackRange = 1 });

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent, CombatComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(e0.Index, results[0].Index);
        }

        [Test]
        public void Filter3_EmptyResult()
        {
            var e0 = _frame.CreateEntity();
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });
            // No CombatComponent

            var filter = _frame.Filter<HealthComponent, OwnerComponent, CombatComponent>();
            Assert.IsFalse(filter.Next(out _));
        }

        // --- Filter returns a valid EntityRef ---

        [Test]
        public void Filter_ReturnsEntityRefWithCorrectVersion()
        {
            var e0 = _frame.CreateEntity();
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var filter = _frame.Filter<HealthComponent>();
            Assert.IsTrue(filter.Next(out var entity));
            Assert.AreEqual(e0.Index, entity.Index);
            Assert.AreEqual(e0.Version, entity.Version);
            Assert.IsTrue(_frame.Entities.IsAlive(entity));
        }

        // --- Multiple entities in filter ---

        [Test]
        public void Filter2_MultipleMatchingEntities()
        {
            var entities = new List<EntityRef>();
            for (int i = 0; i < 5; i++)
            {
                var e = _frame.CreateEntity();
                _frame.Add(e, new HealthComponent { MaxHealth = (i + 1) * 10, CurrentHealth = (i + 1) * 10 });
                _frame.Add(e, new OwnerComponent { OwnerId = i });
                entities.Add(e);
            }

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(5, results.Count);
        }

        // --- Skip dead entities in Filter2/Filter3 ---

        [Test]
        public void Filter2_SkipsDeadEntities()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            _frame.Add(e1, new OwnerComponent { OwnerId = 2 });
            _frame.Add(e2, new HealthComponent { MaxHealth = 300, CurrentHealth = 300 });
            _frame.Add(e2, new OwnerComponent { OwnerId = 3 });

            _frame.DestroyEntity(e1);

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.Exists(e => e.Index == e0.Index));
            Assert.IsTrue(results.Exists(e => e.Index == e2.Index));
        }

        [Test]
        public void Filter3_SkipsDeadEntities()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e0, new OwnerComponent { OwnerId = 1 });
            _frame.Add(e0, new CombatComponent { AttackDamage = 10, AttackRange = 2 });
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            _frame.Add(e1, new OwnerComponent { OwnerId = 2 });
            _frame.Add(e1, new CombatComponent { AttackDamage = 15, AttackRange = 3 });

            _frame.DestroyEntity(e0);

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent, CombatComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(e1.Index, results[0].Index);
        }

        // --- Minimum storage optimization ---

        [Test]
        public void Filter2_IteratesSmallestStorage()
        {
            // Create 10 entities with HealthComponent; only 2 have both Health+Owner
            for (int i = 0; i < 10; i++)
            {
                var e = _frame.CreateEntity();
                _frame.Add(e, new HealthComponent { MaxHealth = (i + 1) * 10, CurrentHealth = (i + 1) * 10 });
                if (i < 2)
                    _frame.Add(e, new OwnerComponent { OwnerId = i });
            }

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(2, results.Count);
        }

        [Test]
        public void Filter3_PicksSmallestStorage()
        {
            // Health 5, Owner 4, Combat 2 — Filter3 should iterate from the smallest set (Combat)
            for (int i = 0; i < 5; i++)
            {
                var e = _frame.CreateEntity();
                _frame.Add(e, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
                if (i < 4)
                    _frame.Add(e, new OwnerComponent { OwnerId = i });
                if (i < 2)
                    _frame.Add(e, new CombatComponent { AttackDamage = 10, AttackRange = 2 });
            }

            var results = new List<EntityRef>();
            var filter = _frame.Filter<HealthComponent, OwnerComponent, CombatComponent>();
            while (filter.Next(out var entity))
            {
                results.Add(entity);
            }

            Assert.AreEqual(2, results.Count);
        }

        [Test]
        public void Filter_NextAfterExhaustion_ReturnsFalse()
        {
            var e0 = _frame.CreateEntity();
            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var filter = _frame.Filter<HealthComponent>();
            Assert.IsTrue(filter.Next(out _));
            Assert.IsFalse(filter.Next(out _));
            // Call again after exhaustion — still false
            Assert.IsFalse(filter.Next(out var entity));
            Assert.AreEqual(EntityRef.None, entity);
        }
    }
}
