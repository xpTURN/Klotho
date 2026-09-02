using System;
using System.Collections.Generic;
using System.IO;

using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

using Brawler;

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    /// <summary>
    /// The baked content every mode of this server needs: per-stage static colliders, per-stage navmesh
    /// and its rebake snapshot, and the DataAsset registry.
    ///
    /// <para>One place, because the verifier and the room server must read the SAME bytes. A verifier that
    /// resolved stage geometry through its own copy could disagree with the server about what stage 2 is,
    /// and the only thing that would notice is the replay's environment fingerprint — a safety net, not a
    /// design. (Before the verifier this block was duplicated between the single-room and multi-room
    /// paths; adding a third copy is what prompted the extraction.)</para>
    ///
    /// <para>Everything here is immutable and loaded once. The navmesh deserialize and the rebake snapshot
    /// are expensive enough that rooms must not pay them: rooms are created on the server loop's receive
    /// stage, so a per-room construction cost is subtracted from every other room's tick budget that cycle.
    /// It is also what the rebake API asks for — rooms sharing a stage call CreateSnapshot once and
    /// construct an FPNavMeshRebakeContext each.</para>
    /// </summary>
    internal sealed class BrawlerStageAssets
    {
        private const int DefaultStageId = 1;

        private readonly Dictionary<int, List<FPStaticCollider>> _colliders;
        private readonly Dictionary<int, FPNavMesh> _navMeshes;
        private readonly Dictionary<int, FPNavMeshRebakeSnapshot> _rebakeSnapshots;

        /// <summary>Every loaded asset, in load order — what ISimulationCallbacks wants.</summary>
        public List<IDataAsset> DataAssets { get; }

        /// <summary>The built registry — what the derived-simulation wiring wants.</summary>
        public IDataAssetRegistry Registry { get; }

        private BrawlerStageAssets(
            Dictionary<int, List<FPStaticCollider>> colliders,
            Dictionary<int, FPNavMesh> navMeshes,
            Dictionary<int, FPNavMeshRebakeSnapshot> rebakeSnapshots,
            List<IDataAsset> dataAssets,
            IDataAssetRegistry registry)
        {
            _colliders = colliders;
            _navMeshes = navMeshes;
            _rebakeSnapshots = rebakeSnapshots;
            DataAssets = dataAssets;
            Registry = registry;
        }

        public static BrawlerStageAssets Load(IKLogger logger)
        {
            string Data(string file) => Path.Combine(AppContext.BaseDirectory, "Data", file);

            // Stage 1 = Stage01.*, stage 2 = Stage02.*; an unmapped or 0 stageId falls back to stage 1
            // (the default stage), so a lobby-selected stage is actually simulated rather than reported.
            var colliders = new Dictionary<int, List<FPStaticCollider>>
            {
                [1] = FPStaticColliderSerializer.Load(Data("Stage01.StaticColliders.bytes")),
                [2] = FPStaticColliderSerializer.Load(Data("Stage02.StaticColliders.bytes")),
            };
            var navBytes = new Dictionary<int, byte[]>
            {
                [1] = File.ReadAllBytes(Data("Stage01.NavMeshData.bytes")),
                [2] = File.ReadAllBytes(Data("Stage02.NavMeshData.bytes")),
            };

            var navMeshes = new Dictionary<int, FPNavMesh>();
            var rebakeSnapshots = new Dictionary<int, FPNavMeshRebakeSnapshot>();
            foreach (var stageNav in navBytes)
            {
                var stageMesh = FPNavMeshSerializer.Deserialize(stageNav.Value);
                navMeshes[stageNav.Key] = stageMesh;
                // A base the rebake refuses leaves this stage without a snapshot — its rooms still run,
                // they just have no building placement. Decided at boot instead of once per room.
                try
                {
                    rebakeSnapshots[stageNav.Key] = BrawlerBuildingShapes.CreateSnapshot(stageMesh, logger);
                }
                catch (Exception e)
                {
                    logger.KWarning($"[BrawlerDedicatedServer] stage {stageNav.Key}: rebake snapshot unavailable — building placement disabled for this stage ({e.Message})");
                }
            }

            var dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(Data("BrawlerAssets.bytes"));
            IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
            registryBuilder.RegisterRange(dataAssets);

            return new BrawlerStageAssets(colliders, navMeshes, rebakeSnapshots, dataAssets, registryBuilder.Build());
        }

        public List<FPStaticCollider> CollidersFor(int stageId)
            => _colliders.TryGetValue(stageId, out var c) ? c : _colliders[DefaultStageId];

        public FPNavMesh NavMeshFor(int stageId) => _navMeshes[NavStageKey(stageId)];

        /// <summary>
        /// Resolved through the SAME key as <see cref="NavMeshFor"/>: the snapshot has to be the one built
        /// from the mesh the room's nav systems query, or the rebake would carve a different base than the
        /// one being pathfound on.
        /// </summary>
        public FPNavMeshRebakeSnapshot RebakeSnapshotFor(int stageId)
            => _rebakeSnapshots.TryGetValue(NavStageKey(stageId), out var s) ? s : null;

        private int NavStageKey(int stageId) => _navMeshes.ContainsKey(stageId) ? stageId : DefaultStageId;
    }
}
