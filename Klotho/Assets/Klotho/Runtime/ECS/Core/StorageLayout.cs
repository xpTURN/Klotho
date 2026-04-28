namespace xpTURN.Klotho.ECS
{
    public readonly struct StorageLayout
    {
        public readonly int TypeId;
        public readonly int Capacity;
        public readonly int CountOffset;
        public readonly int SparseOffset;
        public readonly int DenseOffset;
        public readonly int ComponentsOffset;
        public readonly int ComponentSize;
        public readonly int TotalSize;

        public StorageLayout(int typeId, int capacity,
                             int countOffset, int sparseOffset,
                             int denseOffset, int componentsOffset,
                             int componentSize, int totalSize)
        {
            TypeId = typeId;
            Capacity = capacity;
            CountOffset = countOffset;
            SparseOffset = sparseOffset;
            DenseOffset = denseOffset;
            ComponentsOffset = componentsOffset;
            ComponentSize = componentSize;
            TotalSize = totalSize;
        }
    }
}
