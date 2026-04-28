using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.ECS.Tests
{
    // Validates the behavior of the flat heap view (ComponentStorageFlat<T>) obtained via Frame.GetStorage<T>.
    [TestFixture]
    public class ComponentStorageTests
    {
        private const int Capacity = 16;

        private Frame _frame;
        private ILogger _logger;

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

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(Capacity, _logger);
        }

        // ── Add / contains ─────────────────────────────────────────────────────

        [Test]
        public void Add_SetsHasTrue()
        {
            var e = _frame.CreateEntity();
            _frame.Add(e, new HealthComponent { MaxHealth = 42, CurrentHealth = 42 });

            Assert.IsTrue(_frame.GetStorage<HealthComponent>().Has(e.Index));
        }

        [Test]
        public void Has_ReturnsFalseForEmpty()
        {
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(0));
        }

        [Test]
        public void Has_ReturnsFalseForOutOfRange()
        {
            var storage = _frame.GetStorage<HealthComponent>();
            Assert.IsFalse(storage.Has(-1));
            Assert.IsFalse(storage.Has(999));
        }

        [Test]
        public void Add_IncrementsCount()
        {
            var storage = _frame.GetStorage<HealthComponent>();
            Assert.AreEqual(0, storage.Count);

            var e0 = _frame.CreateEntity();
            _frame.Add(e0, default(HealthComponent));
            Assert.AreEqual(1, _frame.GetStorage<HealthComponent>().Count);

            var e5 = new EntityRef(5, 0);
            _frame.GetStorage<HealthComponent>().Add(5, default);
            Assert.AreEqual(2, _frame.GetStorage<HealthComponent>().Count);
        }

        [Test]
        public void Add_DuplicateThrows()
        {
            var e = _frame.CreateEntity();
            _frame.Add(e, default(HealthComponent));

            Assert.Throws<InvalidOperationException>(
                () => _frame.GetStorage<HealthComponent>().Add(e.Index, default));
        }

        // ── Get ──────────────────────────────────────────────────────────────────

        [Test]
        public void Get_ReturnsCorrectValue()
        {
            var e = _frame.CreateEntity(); // index 0
            // Insert directly at index 3
            _frame.GetStorage<HealthComponent>().Add(3, new HealthComponent { MaxHealth = 99, CurrentHealth = 99 });

            ref var comp = ref _frame.GetStorage<HealthComponent>().Get(3);
            Assert.AreEqual(99, comp.MaxHealth);
        }

        [Test]
        public void Get_ReturnsRef_CanModify()
        {
            _frame.GetStorage<HealthComponent>().Add(0, new HealthComponent { MaxHealth = 10, CurrentHealth = 10 });

            ref var comp = ref _frame.GetStorage<HealthComponent>().Get(0);
            comp.MaxHealth = 20;

            Assert.AreEqual(20, _frame.GetStorage<HealthComponent>().Get(0).MaxHealth);
        }

        [Test]
        public void GetReadOnly_ReturnsCorrectValue()
        {
            _frame.GetStorage<HealthComponent>().Add(2, new HealthComponent { MaxHealth = 77, CurrentHealth = 77 });

            ref readonly var comp = ref _frame.GetStorage<HealthComponent>().GetReadOnly(2);
            Assert.AreEqual(77, comp.MaxHealth);
        }

        // ── Remove ────────────────────────────────────────────────────────────────

        [Test]
        public void Remove_SetsHasFalse()
        {
            _frame.GetStorage<HealthComponent>().Add(0, default);
            _frame.GetStorage<HealthComponent>().Remove(0);

            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(0));
        }

        [Test]
        public void Remove_DecrementsCount()
        {
            _frame.GetStorage<HealthComponent>().Add(0, default);
            _frame.GetStorage<HealthComponent>().Add(1, default);
            _frame.GetStorage<HealthComponent>().Remove(0);

            Assert.AreEqual(1, _frame.GetStorage<HealthComponent>().Count);
        }

        [Test]
        public void Remove_NonExistent_IsNoop()
        {
            _frame.GetStorage<HealthComponent>().Remove(5);
            Assert.AreEqual(0, _frame.GetStorage<HealthComponent>().Count);
        }

        [Test]
        public void Remove_SwapRemove_PreservesOtherComponents()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(1, new HealthComponent { MaxHealth = 20 });
            s.Add(2, new HealthComponent { MaxHealth = 30 });

            s.Remove(0);

            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(0));
            Assert.IsTrue (_frame.GetStorage<HealthComponent>().Has(1));
            Assert.IsTrue (_frame.GetStorage<HealthComponent>().Has(2));
            Assert.AreEqual(20, _frame.GetStorage<HealthComponent>().Get(1).MaxHealth);
            Assert.AreEqual(30, _frame.GetStorage<HealthComponent>().Get(2).MaxHealth);
        }

        [Test]
        public void Remove_LastElement_NoSwapNeeded()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(1, new HealthComponent { MaxHealth = 20 });
            s.Remove(1);

            Assert.IsTrue (_frame.GetStorage<HealthComponent>().Has(0));
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(1));
            Assert.AreEqual(10, _frame.GetStorage<HealthComponent>().Get(0).MaxHealth);
        }

        // ── Dense iteration ───────────────────────────────────────────────────────

        [Test]
        public void DenseToSparse_MapsBackToEntityIndex()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(3, new HealthComponent { MaxHealth = 30 });
            s.Add(7, new HealthComponent { MaxHealth = 70 });

            var mapping = s.DenseToSparse;
            Assert.AreEqual(2, mapping.Length);
            Assert.AreEqual(3, mapping[0]);
            Assert.AreEqual(7, mapping[1]);
        }

        [Test]
        public void DenseSpan_AfterSwapRemove_IsCorrect()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(1, new HealthComponent { MaxHealth = 20 });
            s.Add(2, new HealthComponent { MaxHealth = 30 });

            s.Remove(0); // swap: entity 2 moves to dense[0]

            Assert.AreEqual(2, _frame.GetStorage<HealthComponent>().Count);

            var mapping = _frame.GetStorage<HealthComponent>().DenseToSparse;
            Assert.AreEqual(2, mapping[0]);
            Assert.AreEqual(1, mapping[1]);
            Assert.AreEqual(30, _frame.GetStorage<HealthComponent>().Get(2).MaxHealth);
            Assert.AreEqual(20, _frame.GetStorage<HealthComponent>().Get(1).MaxHealth);
        }

        // ── Initialization ────────────────────────────────────────────────────────

        [Test]
        public void Clear_ResetsAllState()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(5, new HealthComponent { MaxHealth = 50 });
            s.Clear();

            Assert.AreEqual(0, _frame.GetStorage<HealthComponent>().Count);
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(0));
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(5));
        }

        [Test]
        public void Clear_ThenAdd_WorksCorrectly()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Clear();
            s.Add(0, new HealthComponent { MaxHealth = 99 });

            Assert.AreEqual(1, _frame.GetStorage<HealthComponent>().Count);
            Assert.AreEqual(99, _frame.GetStorage<HealthComponent>().Get(0).MaxHealth);
        }

        // ── Add after remove (slot reuse) ────────────────────────────────────────

        [Test]
        public void Add_AfterRemove_WorksCorrectly()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Remove(0);
            s.Add(0, new HealthComponent { MaxHealth = 20 });

            Assert.IsTrue(_frame.GetStorage<HealthComponent>().Has(0));
            Assert.AreEqual(20, _frame.GetStorage<HealthComponent>().Get(0).MaxHealth);
            Assert.AreEqual(1, _frame.GetStorage<HealthComponent>().Count);
        }

        [Test]
        public void Add_AtFullCapacity_Throws()
        {
            // capacity=2 Frame
            var smallFrame = new Frame(2, _logger);
            var s = smallFrame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(1, new HealthComponent { MaxHealth = 20 });

            Assert.Throws<ArgumentOutOfRangeException>(
                () => s.Add(2, new HealthComponent { MaxHealth = 30 }));
        }

        [Test]
        public void Remove_MultipleSequential_SwapRemoveChain()
        {
            var s = _frame.GetStorage<HealthComponent>();
            s.Add(0, new HealthComponent { MaxHealth = 10 });
            s.Add(1, new HealthComponent { MaxHealth = 20 });
            s.Add(2, new HealthComponent { MaxHealth = 30 });
            s.Add(3, new HealthComponent { MaxHealth = 40 });

            s.Remove(1);
            Assert.AreEqual(3, _frame.GetStorage<HealthComponent>().Count);
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(1));

            s.Remove(3);
            Assert.AreEqual(2, _frame.GetStorage<HealthComponent>().Count);
            Assert.IsFalse(_frame.GetStorage<HealthComponent>().Has(3));

            s.Remove(0);
            Assert.AreEqual(1, _frame.GetStorage<HealthComponent>().Count);
            Assert.IsTrue(_frame.GetStorage<HealthComponent>().Has(2));
            Assert.AreEqual(30, _frame.GetStorage<HealthComponent>().Get(2).MaxHealth);

            s.Remove(2);
            Assert.AreEqual(0, _frame.GetStorage<HealthComponent>().Count);
        }
    }
}
