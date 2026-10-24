using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class SheetLayoutMathTests
    {
        [TestCase(87.0, 100)]
        [TestCase(100.0, 100)]
        [TestCase(101.0, 150)]
        [TestCase(420.0, 500)]
        [TestCase(0.5, 1)]
        public void SnapToStandardScale_RoundsUp(double raw, int expected)
        {
            Assert.That(SheetLayoutMath.SnapToStandardScale(raw), Is.EqualTo(expected));
        }

        [Test]
        public void ComputeUniformGrid_TwoByTwo_NoGap_SplitsEvenly()
        {
            var cells = SheetLayoutMath.ComputeUniformGrid(100, 80, 2, 2, 0);
            Assert.That(cells, Has.Count.EqualTo(4));
            Assert.That(cells[0].Width, Is.EqualTo(50).Within(1e-9));
            Assert.That(cells[0].Height, Is.EqualTo(40).Within(1e-9));
            Assert.That(cells[3].X, Is.EqualTo(50).Within(1e-9)); // row-major 第 2 列第 2 欄
            Assert.That(cells[3].Y, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void ComputeComparisonGridV2_TitleBandNarrowLabelsAndFitSchedule()
        {
            // 2026-07-10 D6/D7/D12：欄頂標題帶、標籤欄縮窄（(b)(c) 卡上下堆疊＋圖例保留區）、
            // 明細表高度改由呼叫端 fit 內容後以絕對值傳入。
            var layout = SheetLayoutMath.ComputeComparisonGridV2(
                width: 1000, height: 700, schemeCount: 3, gap: 10,
                labelColumnWidth: 60, titleBandHeight: 16, scheduleHeight: 80);

            Assert.That(layout.TitleCells, Has.Count.EqualTo(3));
            Assert.That(layout.TitleCells[0].Y, Is.EqualTo(0).Within(1e-9));
            Assert.That(layout.TitleCells[0].Height, Is.EqualTo(16).Within(1e-9));
            Assert.That(layout.TitleCells[1].X, Is.GreaterThan(layout.TitleCells[0].X));

            // 內容第一帶從標題帶下方開始。
            Assert.That(layout.SchemeCells[0][0].Y, Is.EqualTo(16 + 10).Within(1e-6));

            // 明細表：絕對高度、貼齊版面底、全寬（含標籤欄下方）。
            Assert.That(layout.ScheduleCell.Height, Is.EqualTo(80).Within(1e-6));
            Assert.That(layout.ScheduleCell.Y + layout.ScheduleCell.Height,
                Is.LessThanOrEqualTo(700 + 1e-6));
            Assert.That(layout.ScheduleCell.X, Is.EqualTo(0).Within(1e-9));
            Assert.That(layout.ScheduleCell.Width, Is.EqualTo(1000).Within(1e-6));

            // (b)(c) 標籤卡改上下堆疊、各佔全標籤欄寬。
            Assert.That(layout.RowLabelCells[1].X, Is.EqualTo(0).Within(1e-9));
            Assert.That(layout.RowLabelCells[2].X, Is.EqualTo(0).Within(1e-9));
            Assert.That(layout.RowLabelCells[1].Width, Is.EqualTo(60).Within(1e-6));
            Assert.That(layout.RowLabelCells[2].Y,
                Is.GreaterThanOrEqualTo(layout.RowLabelCells[1].Y + layout.RowLabelCells[1].Height - 1e-6));

            // 圖例保留區：在 (c) 卡下方、限於標籤欄、不越出 (b)(c) 內容帶底。
            Assert.That(layout.LegendCell.X, Is.EqualTo(0).Within(1e-9));
            Assert.That(layout.LegendCell.Width, Is.EqualTo(60).Within(1e-6));
            Assert.That(layout.LegendCell.Y,
                Is.GreaterThanOrEqualTo(layout.RowLabelCells[2].Y + layout.RowLabelCells[2].Height - 1e-6));
            var band1Bottom = layout.SchemeCells[1][0].Y + layout.SchemeCells[1][0].Height;
            Assert.That(layout.LegendCell.Y + layout.LegendCell.Height,
                Is.LessThanOrEqualTo(band1Bottom + 1e-6));

            // 內容帶維持 0.9/0.9/0.72 權重比。
            Assert.That(layout.SchemeCells[0][0].Height / layout.SchemeCells[2][0].Height,
                Is.EqualTo(1.05 / 0.72).Within(1e-6));

            // 表縮小後圖區要真的變大：內容帶總高 > 版面高的一半。
            var contentTotal = layout.SchemeCells[0][0].Height
                + layout.SchemeCells[1][0].Height + layout.SchemeCells[2][0].Height;
            Assert.That(contentTotal, Is.GreaterThan(700 * 0.5));
        }

        [Test]
        public void ComputeFitScheduleHeight_SumsTitleHeaderAndRows()
        {
            // 列高 = 行數×字高×1.9＋上下留白；總高 = 標題帶＋表頭列＋各資料列。
            // 2026-07-11 十七輪：行距係數 1.55→1.9（微軟正黑實測行距），本函式只管版面預留，
            // 實繪列高改由出圖端量測 TextNote 實高決定。
            var height = SheetLayoutMath.ComputeFitScheduleHeight(
                headerLineCount: 4, rowLineCounts: new[] { 2, 2 },
                headerTextHeight: 8, bodyTextHeight: 9, titleBandHeight: 20, rowPadding: 4);
            var expected = 20
                + ((4 * 8 * 1.9) + (2 * 4))
                + 2 * ((2 * 9 * 1.9) + (2 * 4));
            Assert.That(height, Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void ComputeComparisonGrid_ThreeSchemes_PairsSlopeAndSection3DInSameBand()
        {
            var layout = SheetLayoutMath.ComputeComparisonGrid(1000, 700, 3, 10, 60);
            Assert.That(layout.SchemeCells.Length, Is.EqualTo(4));
            Assert.That(layout.SchemeCells[0].Length, Is.EqualTo(3));
            Assert.That(layout.RowLabelCells, Has.Count.EqualTo(4));
            // 明細列橫跨方案欄：寬 = 3 欄 + 2 gap
            var expectedWidth = layout.SchemeCells[0].Sum(c => c.Width) + (2 * 10);
            Assert.That(layout.ScheduleCell.Width, Is.EqualTo(expectedWidth).Within(1e-6));
            // 明細表列要比單一圖面列高，避免比較表字太小。
            Assert.That(layout.ScheduleCell.Height, Is.GreaterThan(layout.SchemeCells[2][0].Height));
            // 坡度分級與剖面 3D 共用同一橫向帶，左右並排。
            Assert.That(layout.SchemeCells[1][0].Y, Is.EqualTo(layout.SchemeCells[3][0].Y).Within(1e-6));
            Assert.That(layout.SchemeCells[1][0].Height, Is.EqualTo(layout.SchemeCells[3][0].Height).Within(1e-6));
            Assert.That(layout.SchemeCells[3][0].X, Is.GreaterThan(layout.SchemeCells[1][0].X));
            Assert.That(layout.SchemeCells[1][0].Width, Is.LessThan(layout.SchemeCells[0][0].Width));
            Assert.That(layout.RowLabelCells[1].Y, Is.EqualTo(layout.RowLabelCells[2].Y).Within(1e-6));
            Assert.That(layout.RowLabelCells[2].X, Is.GreaterThan(layout.RowLabelCells[1].X));
            Assert.That(layout.RowLabelCells[3].Y, Is.GreaterThan(layout.RowLabelCells[1].Y));
            // 3D 列仍略高於剖面列。
            Assert.That(layout.SchemeCells[0][0].Height / layout.SchemeCells[2][0].Height,
                Is.EqualTo(1.05 / 0.72).Within(1e-6));
            // 全部格子都在版面內
            Assert.That(layout.ScheduleCell.Y + layout.ScheduleCell.Height, Is.LessThanOrEqualTo(700 + 1e-6));
        }
    }
}
