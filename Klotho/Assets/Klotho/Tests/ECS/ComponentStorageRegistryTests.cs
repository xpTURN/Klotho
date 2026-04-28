using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.ECS.Tests
{
    // Registration Lifecycle test
    // Unregistered component — UnregisteredComponent is defined in FrameTests.cs
    [TestFixture]
    public class ComponentStorageRegistryTests
    {
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
            ComponentStorageRegistry.ResetForTesting();
        }

        // (1) After EnsureLayoutComputed(256) succeeds, _typeToId.Count > 0
        [Test]
        public void EnsureLayoutComputed_Success_TypeCountGreaterThanZero()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);

            Assert.Greater(ComponentStorageRegistry.RegisteredTypeCount, 0);
        }

        // (2) Register<NewType> after Freeze -> InvalidOperationException (both Editor and Release)
        [Test]
        public void Register_AfterFreeze_Throws()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);
            Assert.IsTrue(ComponentStorageRegistry.IsFrozen);

            Assert.Throws<InvalidOperationException>(
                () => ComponentStorageRegistry.Register<UnregisteredComponent>(9999));
        }

        // (3) Editor/Test/Debug build: EnsureLayoutComputed with different maxEntities after Freeze -> succeeds after ResetForRecompute
        //     Release build: InvalidOperationException
        [Test]
        public void EnsureLayoutComputed_DifferentMaxEntities_AfterFreeze_EditorSucceedsReleaseThrows()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);
            Assert.IsTrue(ComponentStorageRegistry.IsFrozen);

#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEBUG
            // Editor/Test/Debug: automatic ResetForRecompute -> succeeds
            Assert.DoesNotThrow(() => ComponentStorageRegistry.EnsureLayoutComputed(512));
#else
            // Release: throw
            Assert.Throws<InvalidOperationException>(
                () => ComponentStorageRegistry.EnsureLayoutComputed(512));
#endif
        }

        // (4) After ResetForTesting(), re-running EnsureLayoutComputed with the same maxEntities succeeds
        [Test]
        public void EnsureLayoutComputed_AfterResetForTesting_Succeeds()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);
            ComponentStorageRegistry.ResetForTesting();

            Assert.DoesNotThrow(() => ComponentStorageRegistry.EnsureLayoutComputed(256));
            Assert.IsTrue(ComponentStorageRegistry.IsFrozen);
        }

        // (5) After Freeze, IsFrozen == true && RegisteredTypeCount > 0
        [Test]
        public void AfterFreeze_IsFrozenTrue_AndRegisteredTypeCountGreaterThanZero()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);

            Assert.IsTrue(ComponentStorageRegistry.IsFrozen);
            Assert.Greater(ComponentStorageRegistry.RegisteredTypeCount, 0);
        }

        // (6) Frame.Add<UnregisteredComponent> with an unregistered type -> InvalidOperationException
        [Test]
        public void Frame_Add_UnregisteredComponent_Throws()
        {
            var frame = new Frame(16, _logger);
            var entity = frame.CreateEntity();

            var ex = Assert.Throws<InvalidOperationException>(
                () => frame.Add(entity, new UnregisteredComponent { Dummy = 1 }));
            StringAssert.Contains("UnregisteredComponent", ex.Message);
            StringAssert.Contains("not registered", ex.Message);
        }
    }
}
