namespace xpTURN.Klotho.Deterministic.Navigation
{
    public static unsafe class NavCorridorHelper
    {
        public static void SetCorridor(int* dst, ref int dstLen, int maxLen, int[] src, int srcLen)
        {
            int copyLen = srcLen < maxLen ? srcLen : maxLen;
            for (int i = 0; i < copyLen; i++)
                dst[i] = src[i];
            dstLen = copyLen;
        }

        public static void MergeCorridorStart(
            int* corridor, ref int corridorLength, int maxCorridor,
            int[] visited, int visitedCount)
        {
            if (visitedCount == 0 || corridorLength == 0)
                return;

            int furthestPath = -1;
            int furthestVisited = -1;
            for (int i = corridorLength - 1; i >= 0; i--)
            {
                bool found = false;
                for (int j = visitedCount - 1; j >= 0; j--)
                {
                    if (corridor[i] == visited[j])
                    {
                        furthestPath = i;
                        furthestVisited = j;
                        found = true;
                    }
                }
                if (found)
                    break;
            }

            if (furthestPath == -1 || furthestVisited == -1)
                return;

            int req = visitedCount - furthestVisited;
            int orig = furthestPath + 1 < corridorLength ? furthestPath + 1 : corridorLength;
            int size = corridorLength - orig > 0 ? corridorLength - orig : 0;

            int newLength = req + size;
            if (newLength > maxCorridor)
                size = maxCorridor - req;

            if (size > 0)
            {
                for (int i = size - 1; i >= 0; i--)
                    corridor[req + i] = corridor[orig + i];
            }

            for (int i = 0; i < req && i < maxCorridor; i++)
                corridor[i] = visited[(visitedCount - 1) - i];

            corridorLength = req + size;
        }
    }
}
