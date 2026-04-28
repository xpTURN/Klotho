using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.ECS
{
    public sealed class DataAssetRegistry : IDataAssetRegistryBuilder
    {
        private readonly Dictionary<int, IDataAsset> _assets = new Dictionary<int, IDataAsset>();
        private bool _locked;

        public void Register(IDataAsset asset)
        {
            if (_locked)
                throw new InvalidOperationException("DataAssetRegistry is locked after simulation start.");
            if (_assets.ContainsKey(asset.AssetId))
                throw new InvalidOperationException($"Duplicate AssetId: {asset.AssetId}");
            _assets[asset.AssetId] = asset;
        }

        public void RegisterRange(IReadOnlyList<IDataAsset> assets)
        {
            for (int i = 0; i < assets.Count; i++)
                Register(assets[i]);
        }

        private void Lock() => _locked = true;

        IDataAssetRegistry IDataAssetRegistryBuilder.Build()
        {
            Lock();
            return this;
        }

        public T Get<T>(int id) where T : IDataAsset
        {
            if (!_assets.TryGetValue(id, out var asset))
                throw new KeyNotFoundException($"DataAsset not found: id={id}, type={typeof(T).Name}");
            return (T)asset;
        }

        public bool TryGet<T>(int id, out T result) where T : IDataAsset
        {
            if (_assets.TryGetValue(id, out var asset) && asset is T typed)
            {
                result = typed;
                return true;
            }
            result = default;
            return false;
        }

        public T Get<T>(DataAssetRef assetRef) where T : IDataAsset
            => Get<T>(assetRef.Id);

        public bool TryGet<T>(DataAssetRef assetRef, out T result) where T : IDataAsset
            => TryGet(assetRef.Id, out result);
    }
}
