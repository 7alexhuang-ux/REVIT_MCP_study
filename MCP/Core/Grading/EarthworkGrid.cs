using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>方格法計算書的一格：深度正=挖、負=填；null=格中心無地形交集。</summary>
    public sealed class GridCell
    {
        public GridCell(int row, int column, double? depthMeters)
        {
            Row = row;
            Column = column;
            DepthMeters = depthMeters;
        }

        public int Row { get; }
        public int Column { get; }
        public double? DepthMeters { get; }
    }

    public sealed class EarthworkGridResult
    {
        public EarthworkGridResult(double cutCubicMeters, double fillCubicMeters, int sampledCellCount)
        {
            CutCubicMeters = cutCubicMeters;
            FillCubicMeters = fillCubicMeters;
            SampledCellCount = sampledCellCount;
        }

        public double CutCubicMeters { get; }
        public double FillCubicMeters { get; }
        public int SampledCellCount { get; }
    }

    /// <summary>
    /// 方格法土方計算（呈現用）：格中心深度 × 格面積。
    /// 與 Revit 內建 CUT/FILL 的差額即方格離散誤差，由計算書並列揭露；不取代正式報表值。
    /// </summary>
    public static class EarthworkGrid
    {
        public static EarthworkGridResult Compute(
            IReadOnlyList<GridCell> cells,
            double cellAreaSquareMeters)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cellAreaSquareMeters <= 0)
            {
                throw new ArgumentException("格面積必須大於 0。", nameof(cellAreaSquareMeters));
            }

            var cut = 0.0;
            var fill = 0.0;
            var sampled = 0;
            foreach (var cell in cells)
            {
                if (!cell.DepthMeters.HasValue)
                {
                    continue;
                }

                sampled++;
                var depth = cell.DepthMeters.Value;
                if (depth > 0)
                {
                    cut += depth * cellAreaSquareMeters;
                }
                else if (depth < 0)
                {
                    fill += -depth * cellAreaSquareMeters;
                }
            }

            return new EarthworkGridResult(cut, fill, sampled);
        }
    }
}
