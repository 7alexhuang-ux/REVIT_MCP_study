using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>圖紙格位（左上原點、Y 向下；單位由呼叫端決定）。</summary>
    public readonly struct LayoutCell
    {
        public LayoutCell(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
    }

    /// <summary>比較圖紙格局：4 內容列 × N 方案欄＋左側列標籤欄＋底部明細表橫條。</summary>
    public sealed class ComparisonGridLayout
    {
        public IReadOnlyList<LayoutCell> RowLabelCells { get; set; }
        public LayoutCell[][] SchemeCells { get; set; }
        public LayoutCell ScheduleCell { get; set; }
    }

    /// <summary>圖紙排版純數學（零 Revit 相依，NUnit 覆蓋）。</summary>
    public static class SheetLayoutMath
    {
        /// <summary>Revit 常用視圖比例分母，由小到大。</summary>
        public static readonly int[] StandardScales =
            { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000 };

        /// <summary>C-00c 內容列權重：3D／坡度／正剖面／剖面立體。</summary>
        private static readonly double[] ContentRowWeights = { 1.1, 1.1, 1.0, 1.0 };
        private const double ScheduleRowWeight = 0.7;

        /// <summary>把原始比例分母向上取到最近的標準比例（放不下→圖形縮小）。</summary>
        public static int SnapToStandardScale(double rawScale)
        {
            if (double.IsNaN(rawScale) || double.IsInfinity(rawScale) || rawScale <= 0)
            {
                throw new ArgumentException("比例分母必須是正有限數。", nameof(rawScale));
            }

            foreach (var scale in StandardScales)
            {
                if (scale >= rawScale - 1e-9)
                {
                    return scale;
                }
            }

            return StandardScales[StandardScales.Length - 1];
        }

        /// <summary>均分格陣，row-major。</summary>
        public static IReadOnlyList<LayoutCell> ComputeUniformGrid(
            double width, double height, int columns, int rows, double gap)
        {
            if (columns <= 0 || rows <= 0)
            {
                throw new ArgumentException("columns 與 rows 必須大於 0。");
            }

            var cellWidth = (width - (gap * (columns - 1))) / columns;
            var cellHeight = (height - (gap * (rows - 1))) / rows;
            var cells = new List<LayoutCell>(columns * rows);
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    cells.Add(new LayoutCell(
                        column * (cellWidth + gap), row * (cellHeight + gap), cellWidth, cellHeight));
                }
            }

            return cells;
        }

        /// <summary>C-00c 比較圖紙格局：權重列高、左標籤欄、底部明細橫條。</summary>
        public static ComparisonGridLayout ComputeComparisonGrid(
            double width, double height, int schemeCount, double gap, double labelColumnWidth)
        {
            if (schemeCount <= 0)
            {
                throw new ArgumentException("schemeCount 必須大於 0。", nameof(schemeCount));
            }

            var totalRows = ContentRowWeights.Length + 1; // 4 內容列＋明細列
            var weightSum = ScheduleRowWeight;
            foreach (var weight in ContentRowWeights) { weightSum += weight; }
            var usableHeight = height - (gap * (totalRows - 1));
            var unitHeight = usableHeight / weightSum;

            var contentWidth = width - labelColumnWidth - gap;
            var schemeWidth = (contentWidth - (gap * (schemeCount - 1))) / schemeCount;
            var contentX = labelColumnWidth + gap;

            var labels = new List<LayoutCell>(totalRows);
            var schemeCells = new LayoutCell[ContentRowWeights.Length][];
            var y = 0.0;
            for (var row = 0; row < ContentRowWeights.Length; row++)
            {
                var rowHeight = unitHeight * ContentRowWeights[row];
                labels.Add(new LayoutCell(0, y, labelColumnWidth, rowHeight));
                schemeCells[row] = new LayoutCell[schemeCount];
                for (var column = 0; column < schemeCount; column++)
                {
                    schemeCells[row][column] = new LayoutCell(
                        contentX + (column * (schemeWidth + gap)), y, schemeWidth, rowHeight);
                }

                y += rowHeight + gap;
            }

            var scheduleHeight = unitHeight * ScheduleRowWeight;
            labels.Add(new LayoutCell(0, y, labelColumnWidth, scheduleHeight));
            var scheduleCell = new LayoutCell(
                contentX, y, (schemeWidth * schemeCount) + (gap * (schemeCount - 1)), scheduleHeight);

            return new ComparisonGridLayout
            {
                RowLabelCells = labels,
                SchemeCells = schemeCells,
                ScheduleCell = scheduleCell
            };
        }
    }
}
