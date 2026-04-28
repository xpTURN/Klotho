namespace xpTURN.Klotho.ECS
{
    public readonly struct DataAssetRef
    {
        public readonly int Id;

        public DataAssetRef(int id) => Id = id;

        public bool IsValid => Id != 0;
        public static readonly DataAssetRef Invalid = default;
    }
}
