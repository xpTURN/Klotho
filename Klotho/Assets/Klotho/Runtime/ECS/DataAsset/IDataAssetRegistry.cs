namespace xpTURN.Klotho.ECS
{
    public interface IDataAssetRegistry
    {
        T Get<T>(int id) where T : IDataAsset;
        bool TryGet<T>(int id, out T result) where T : IDataAsset;
        T Get<T>(DataAssetRef assetRef) where T : IDataAsset;
        bool TryGet<T>(DataAssetRef assetRef, out T result) where T : IDataAsset;
    }
}
