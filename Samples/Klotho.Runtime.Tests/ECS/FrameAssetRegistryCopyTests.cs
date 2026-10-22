using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// Frames handed out for render-time reads resolve the same data assets as the live frame.
    ///
    /// <para>Ring slots (<c>VerifiedFrame</c>, <c>PredictedPreviousFrame</c>, <c>TryGetFrameAtTick</c>)
    /// and the PreviousUpdatePredicted copy are built without a registry and filled only through
    /// <see cref="Frame.CopyFrom"/>, so the registry travels with the copy. Without it an asset lookup
    /// on those frames throws <c>KeyNotFoundException</c> while the same lookup on <c>PredictedFrame</c>
    /// succeeds — and every sample reads assets off <c>PredictedFrame</c> only, so nothing else
    /// catches it.</para>
    ///
    /// <para>The copy runs in both directions (save into the ring, restore out of it), so the last test
    /// pins that a rollback leaves the live frame on the registry it started with.</para>
    /// </summary>
    [TestFixture]
    public class FrameAssetRegistryCopyTests
    {
        private const int MaxEntities = 16;
        private const int ProbeAssetId = 1;

        private sealed class ProbeAsset : IDataAsset
        {
            public int AssetId => ProbeAssetId;
        }

        [Test]
        public void CopyFrom_TargetResolvesSourceAssets()
        {
            var asset = new ProbeAsset();
            var registry = new DataAssetRegistry();
            registry.Register(asset);
            var source = new Frame(MaxEntities, null, registry);
            var target = new Frame(MaxEntities, null);

            target.CopyFrom(source);

            Assert.AreSame(registry, target.AssetRegistry);
            Assert.AreSame(asset, target.GetAsset<ProbeAsset>(ProbeAssetId));
        }

        [Test]
        public void SnapshotFrame_ResolvesLiveAssets()
        {
            var asset = new ProbeAsset();
            var registry = new DataAssetRegistry();
            registry.Register(asset);
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50, assetRegistry: registry);
            sim.Initialize();

            sim.SaveSnapshot();

            Assert.IsTrue(sim.TryGetSnapshotFrame(sim.Frame.Tick, out var slot));
            Assert.AreSame(asset, slot.GetAsset<ProbeAsset>(ProbeAssetId));
        }

        [Test]
        public void Rollback_LiveFrameKeepsItsRegistry()
        {
            var registry = new DataAssetRegistry();
            registry.Register(new ProbeAsset());
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50, assetRegistry: registry);
            sim.Initialize();
            int savedTick = sim.Frame.Tick;

            sim.SaveSnapshot();
            sim.Tick(new List<ICommand>());
            sim.Rollback(savedTick);

            Assert.AreEqual(savedTick, sim.Frame.Tick);
            Assert.AreSame(registry, sim.Frame.AssetRegistry);
        }
    }
}
