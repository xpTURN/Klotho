using System.Collections.Generic;

namespace xpTURN.Klotho.ECS
{
    public interface IDataAssetRegistryBuilder : IDataAssetRegistry
    {
        void Register(IDataAsset asset);
        void RegisterRange(IReadOnlyList<IDataAsset> assets);
        IDataAssetRegistry Build();
    }
}
