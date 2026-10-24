using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>C-00c V2 格局（2026-07-10 D6/D7/D12）：欄頂標題帶＋窄標籤欄＋絕對高度明細表。</summary>
    public sealed class ComparisonGridLayoutV2
    {
        public IReadOnlyList<LayoutCell> TitleCells { get; set; }
        public IReadOnlyList<LayoutCell> RowLabelCells { get; set; }
        public LayoutCell[][] SchemeCells { get; set; }
        public LayoutCell LegendCell { get; set; }
        public LayoutCell ScheduleCell { get; set; }
    }

    /// <summary>圖紙排版純數學（零 Revit 相依，NUnit 覆蓋）。</summary>
    public static class SheetLayoutMath
    {
        /// <summary>Revit 常用視圖比例分母，由小到大。</summary>
        public static readonly int[] StandardScales =
            { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000 };

        /// <summary>C-00c 內容帶權重：3D／坡度+剖面3D／剖面。
        /// 2026-07-10 三輪：(a) 是高度受限的主圖再加高、(b)(c) 帶寬度受限有垂直閒置故縮。</summary>
        private static readonly double[] ContentBandWeights = { 1.05, 0.75, 0.72 };
        private const double ScheduleRowWeight = 2.05;

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

            var totalRows = ContentBandWeights.Length + 1; // 3 內容帶＋明細列
            var weightSum = ScheduleRowWeight;
            foreach (var weight in ContentBandWeights) { weightSum += weight; }
            var usableHeight = height - (gap * (totalRows - 1));
            var unitHeight = usableHeight / weightSum;

            var contentWidth = width - labelColumnWidth - gap;
            var schemeWidth = (contentWidth - (gap * (schemeCount - 1))) / schemeCount;
            var contentX = labelColumnWidth + gap;

            var labels = new List<LayoutCell>(4);
            var schemeCells = new LayoutCell[4][];
            var y = 0.0;
            var bandHeights = ContentBandWeights.Select(weight => unitHeight * weight).ToArray();

            // (a) 3D：整欄寬
            labels.Add(new LayoutCell(0, y, labelColumnWidth, bandHeights[0]));
            schemeCells[0] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                schemeCells[0][column] = new LayoutCell(
                    contentX + (column * (schemeWidth + gap)), y, schemeWidth, bandHeights[0]);
            }

            y += bandHeights[0] + gap;

            // (b) 坡度分級 與 (c) 剖面3D：同一內容帶左右並排。
            var splitGap = gap * 0.55;
            var halfWidth = (schemeWidth - splitGap) / 2.0;
            var labelHalfWidth = (labelColumnWidth - splitGap) / 2.0;
            labels.Add(new LayoutCell(0, y, labelHalfWidth, bandHeights[1]));
            labels.Add(new LayoutCell(labelHalfWidth + splitGap, y, labelHalfWidth, bandHeights[1]));
            schemeCells[1] = new LayoutCell[schemeCount];
            schemeCells[3] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                var x = contentX + (column * (schemeWidth + gap));
                schemeCells[1][column] = new LayoutCell(x, y, halfWidth, bandHeights[1]);
                schemeCells[3][column] = new LayoutCell(x + halfWidth + splitGap, y, halfWidth, bandHeights[1]);
            }

            y += bandHeights[1] + gap;

            // (d) 剖面：整欄寬
            labels.Add(new LayoutCell(0, y, labelColumnWidth, bandHeights[2]));
            schemeCells[2] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                schemeCells[2][column] = new LayoutCell(
                    contentX + (column * (schemeWidth + gap)), y, schemeWidth, bandHeights[2]);
            }

            y += bandHeights[2] + gap;

            var scheduleHeight = unitHeight * ScheduleRowWeight;
            var scheduleCell = new LayoutCell(
                contentX, y, (schemeWidth * schemeCount) + (gap * (schemeCount - 1)), scheduleHeight);

            return new ComparisonGridLayout
            {
                RowLabelCells = labels,
                SchemeCells = schemeCells,
                ScheduleCell = scheduleCell
            };
        }

        /// <summary>
        /// C-00c V2 格局（2026-07-10 使用者裁決 D6/D7/D12）：
        /// 頂部標題帶（每欄方案名稱）→ 三內容帶（權重 0.9/0.9/0.72）→ 底部明細表（絕對高度，
        /// 由呼叫端以 ComputeFitScheduleHeight 先 fit 內容）。標籤欄縮窄：(b)(c) 卡上下堆疊、
        /// 其下保留坡度圖例區；明細表滿版寬（含標籤欄下方）。
        /// </summary>
        public static ComparisonGridLayoutV2 ComputeComparisonGridV2(
            double width, double height, int schemeCount, double gap,
            double labelColumnWidth, double titleBandHeight, double scheduleHeight)
        {
            if (schemeCount <= 0)
            {
                throw new ArgumentException("schemeCount 必須大於 0。", nameof(schemeCount));
            }

            // 直欄：標題帶、三內容帶、明細表 → 帶間 4 個 gap。
            var usableHeight = height - titleBandHeight - scheduleHeight - (gap * 4);
            if (usableHeight <= 0)
            {
                throw new ArgumentException("版面高度不足以容納標題帶與明細表。", nameof(height));
            }

            var weightSum = ContentBandWeights.Sum();
            var unitHeight = usableHeight / weightSum;
            var bandHeights = ContentBandWeights.Select(weight => unitHeight * weight).ToArray();

            var contentWidth = width - labelColumnWidth - gap;
            var schemeWidth = (contentWidth - (gap * (schemeCount - 1))) / schemeCount;
            var contentX = labelColumnWidth + gap;

            var titles = new List<LayoutCell>(schemeCount);
            for (var column = 0; column < schemeCount; column++)
            {
                titles.Add(new LayoutCell(
                    contentX + (column * (schemeWidth + gap)), 0, schemeWidth, titleBandHeight));
            }

            var labels = new List<LayoutCell>(4);
            var schemeCells = new LayoutCell[4][];
            var y = titleBandHeight + gap;

            // (a) 3D：整欄寬。
            labels.Add(new LayoutCell(0, y, labelColumnWidth, bandHeights[0]));
            schemeCells[0] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                schemeCells[0][column] = new LayoutCell(
                    contentX + (column * (schemeWidth + gap)), y, schemeWidth, bandHeights[0]);
            }

            y += bandHeights[0] + gap;

            // (b) 坡度與 (c) 剖立體：內容左右並排；標籤卡上下堆疊（窄標籤欄容不下並排字）。
            var splitGap = gap * 0.55;
            var halfWidth = (schemeWidth - splitGap) / 2.0;
            var cardHeight = Math.Min(bandHeights[1] * 0.18, titleBandHeight * 1.4);
            labels.Add(new LayoutCell(0, y, labelColumnWidth, cardHeight));
            labels.Add(new LayoutCell(0, y + cardHeight + (splitGap * 0.5), labelColumnWidth, cardHeight));
            var legendTop = y + (2 * cardHeight) + splitGap;
            var legendCell = new LayoutCell(
                0, legendTop, labelColumnWidth, Math.Max(0, y + bandHeights[1] - legendTop));
            schemeCells[1] = new LayoutCell[schemeCount];
            schemeCells[3] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                var x = contentX + (column * (schemeWidth + gap));
                schemeCells[1][column] = new LayoutCell(x, y, halfWidth, bandHeights[1]);
                schemeCells[3][column] = new LayoutCell(x + halfWidth + splitGap, y, halfWidth, bandHeights[1]);
            }

            y += bandHeights[1] + gap;

            // (d) 剖面：整欄寬。
            labels.Add(new LayoutCell(0, y, labelColumnWidth, bandHeights[2]));
            schemeCells[2] = new LayoutCell[schemeCount];
            for (var column = 0; column < schemeCount; column++)
            {
                schemeCells[2][column] = new LayoutCell(
                    contentX + (column * (schemeWidth + gap)), y, schemeWidth, bandHeights[2]);
            }

            y += bandHeights[2] + gap;

            return new ComparisonGridLayoutV2
            {
                TitleCells = titles,
                RowLabelCells = labels,
                SchemeCells = schemeCells,
                LegendCell = legendCell,
                ScheduleCell = new LayoutCell(0, y, width, scheduleHeight)
            };
        }

        /// <summary>
        /// 明細表 fit-to-content 高度（2026-07-10 D7）：列高＝行數×字高×1.9＋上下留白，
        /// 總高＝標題帶＋表頭列＋各資料列。單位與呼叫端一致（純數字）。
        /// 2026-07-11 十七輪：1.55→1.9——1.55 是理想行距，微軟正黑實際行距約 1.9×字高，
        /// 估短導致表格帶預留不足、實繪列高（改為量測 TextNote 實高）超出預留區。
        /// 本函式只負責「版面預留」，實際列高由 DrawComparisonTableOnSheet 量測而得。
        /// </summary>
        public static double ComputeFitScheduleHeight(
            int headerLineCount, IReadOnlyList<int> rowLineCounts,
            double headerTextHeight, double bodyTextHeight,
            double titleBandHeight, double rowPadding)
        {
            if (rowLineCounts == null)
            {
                throw new ArgumentNullException(nameof(rowLineCounts));
            }

            var total = titleBandHeight
                + (Math.Max(1, headerLineCount) * headerTextHeight * 1.9) + (2 * rowPadding);
            foreach (var lines in rowLineCounts)
            {
                total += (Math.Max(1, lines) * bodyTextHeight * 1.9) + (2 * rowPadding);
            }

            return total;
        }
    }
}
