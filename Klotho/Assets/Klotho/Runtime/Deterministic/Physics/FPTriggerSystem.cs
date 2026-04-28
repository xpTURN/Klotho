using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Detects Enter/Stay/Exit events for trigger colliders.
    /// </summary>
    public struct FPTriggerSystem
    {
        List<(int, int)> _prevPairs;
        List<(int, int)> _enterBuffer;
        List<(int, int)> _stayBuffer;
        List<(int, int)> _exitBuffer;

        static readonly Comparison<(int, int)> PairComparer = (a, b) =>
        {
            int c = a.Item1.CompareTo(b.Item1);
            return c != 0 ? c : a.Item2.CompareTo(b.Item2);
        };

        public FPTriggerSystem(bool _)
        {
            _prevPairs = new List<(int, int)>();
            _enterBuffer = new List<(int, int)>();
            _stayBuffer = new List<(int, int)>();
            _exitBuffer = new List<(int, int)>();
        }

        public void ProcessCallbacks(
            List<(int, int)> currPairs,
            Action<int, int> onEnter,
            Action<int, int> onStay,
            Action<int, int> onExit)
        {
            ClassifyPairs(_prevPairs, currPairs, _enterBuffer, _stayBuffer, _exitBuffer);

            if (onEnter != null)
            {
                for (int i = 0; i < _enterBuffer.Count; i++)
                    onEnter(_enterBuffer[i].Item1, _enterBuffer[i].Item2);
            }

            if (onStay != null)
            {
                for (int i = 0; i < _stayBuffer.Count; i++)
                    onStay(_stayBuffer[i].Item1, _stayBuffer[i].Item2);
            }

            if (onExit != null)
            {
                for (int i = 0; i < _exitBuffer.Count; i++)
                    onExit(_exitBuffer[i].Item1, _exitBuffer[i].Item2);
            }

            _prevPairs.Clear();
            for (int i = 0; i < currPairs.Count; i++)
                _prevPairs.Add(currPairs[i]);
        }

        static void ClassifyPairs(
            List<(int, int)> prev,
            List<(int, int)> curr,
            List<(int, int)> enter,
            List<(int, int)> stay,
            List<(int, int)> exit)
        {
            enter.Clear();
            stay.Clear();
            exit.Clear();

            int i = 0, j = 0;
            while (i < prev.Count && j < curr.Count)
            {
                int cmp = PairComparer(prev[i], curr[j]);
                if (cmp < 0)
                {
                    exit.Add(prev[i]);
                    i++;
                }
                else if (cmp > 0)
                {
                    enter.Add(curr[j]);
                    j++;
                }
                else
                {
                    stay.Add(curr[j]);
                    i++;
                    j++;
                }
            }
            while (i < prev.Count)
            {
                exit.Add(prev[i]);
                i++;
            }
            while (j < curr.Count)
            {
                enter.Add(curr[j]);
                j++;
            }
        }

        public int GetSerializedSize() => 4 + _prevPairs.Count * 8;

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(_prevPairs.Count);
            for (int i = 0; i < _prevPairs.Count; i++)
            {
                writer.WriteInt32(_prevPairs[i].Item1);
                writer.WriteInt32(_prevPairs[i].Item2);
            }
        }

        public void Deserialize(ref SpanReader reader)
        {
            _prevPairs.Clear();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int a = reader.ReadInt32();
                int b = reader.ReadInt32();
                _prevPairs.Add((a, b));
            }
        }

        public void Clear()
        {
            _prevPairs.Clear();
        }
    }
}
