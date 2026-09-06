namespace xpTURN.Klotho.Deterministic.Navigation
{
    public static unsafe class NavCorridorHelper
    {
        /// <summary>
        /// Copies at most <paramref name="maxLen"/> triangles into the component's corridor and
        /// returns <b>how many were dropped</b> — the count the caller has to treat as a wiring bug.
        ///
        /// <para>Under one effective cap this cannot happen: the search returns at most the cap and
        /// <paramref name="maxLen"/> is that same cap. A nonzero return therefore means the search
        /// and the storage were built from different caps, and the corridor the agent walks is not
        /// the corridor that was planned. It used to be silent, which made the truncation counter on
        /// the pathfinder agree with the constant that had NOT been changed.</para>
        /// </summary>
        public static int SetCorridor(int* dst, ref int dstLen, int maxLen, int[] src, int srcLen)
        {
            int copyLen = srcLen < maxLen ? srcLen : maxLen;
            for (int i = 0; i < copyLen; i++)
                dst[i] = src[i];
            dstLen = copyLen;
            return srcLen - copyLen;
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
