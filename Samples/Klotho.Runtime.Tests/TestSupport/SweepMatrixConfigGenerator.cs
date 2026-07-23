using System.Collections.Generic;
using System.Text;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Generates faultinjectionconfig.json content and SimulationConfig overrides for each
    /// cell of the disconnect-length × QuorumMissDropTicks sweep matrix.
    /// </summary>
    internal static class SweepMatrixConfigGenerator
    {
        // Sweep dimensions.
        public static readonly float[] DisconnectDurations = { 5f, 15f, 30f, 60f };
        public static readonly int[] QuorumMissDropTicksValues = { 10, 20, 30, 40, 60 };

        public readonly struct Cell
        {
            public readonly float DisconnectDurationSec;
            public readonly int QuorumMissDropTicks;
            public readonly int CellIndex;

            public Cell(float disconnectDurationSec, int quorumMissDropTicks, int cellIndex)
            {
                DisconnectDurationSec = disconnectDurationSec;
                QuorumMissDropTicks = quorumMissDropTicks;
                CellIndex = cellIndex;
            }

            public override string ToString() =>
                $"cell[{CellIndex}] disconnect={DisconnectDurationSec:F0}s N={QuorumMissDropTicks}";
        }

        /// <summary>
        /// Enumerates all 20 cells (4 disconnect durations × 5 QuorumMissDropTicks values).
        /// </summary>
        public static IEnumerable<Cell> AllCells()
        {
            int index = 0;
            foreach (float dur in DisconnectDurations)
                foreach (int n in QuorumMissDropTicksValues)
                    yield return new Cell(dur, n, index++);
        }

        /// <summary>
        /// Generates the faultinjectionconfig.json content for the given cell.
        /// atSec: match-anchor offset at which the disconnect fires.
        /// targetPlayerId: null disconnects all non-host peers.
        /// </summary>
        public static string GenerateJson(Cell cell, float atSec = 10f, int? targetPlayerId = null)
        {
            string playerIdValue = targetPlayerId.HasValue
                ? targetPlayerId.Value.ToString()
                : "null";

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("    \"EmulatedDisconnectSchedule\": [");
            sb.AppendLine("        {");
            sb.AppendLine($"            \"atSec\": {atSec:F1},");
            sb.AppendLine($"            \"durationSec\": {cell.DisconnectDurationSec:F1},");
            sb.AppendLine($"            \"playerId\": {playerIdValue}");
            sb.AppendLine("        }");
            sb.AppendLine("    ]");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
