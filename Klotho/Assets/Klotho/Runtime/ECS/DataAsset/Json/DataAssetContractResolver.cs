using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Serialization;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class DataAssetContractResolver : DefaultContractResolver
    {
        protected override List<MemberInfo> GetSerializableMembers(Type objectType)
        {
            var members = base.GetSerializableMembers(objectType);

            if (!typeof(IDataAsset).IsAssignableFrom(objectType))
                return members;

            return members.Where(m =>
                m.Name == nameof(IDataAsset.AssetId) ||
                m.GetCustomAttribute<KlothoOrderAttribute>() != null
            ).ToList();
        }
    }
}
