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
        public void ComputeComparisonGrid_ThreeSchemes_HasFourRowsAndScheduleStrip()
        {
            var layout = SheetLayoutMath.ComputeComparisonGrid(1000, 700, 3, 10, 60);
            Assert.That(layout.SchemeCells.Length, Is.EqualTo(4));
            Assert.That(layout.SchemeCells[0].Length, Is.EqualTo(3));
            Assert.That(layout.RowLabelCells, Has.Count.EqualTo(5));
            // 明細列橫跨方案欄：寬 = 3 欄 + 2 gap
            var expectedWidth = layout.SchemeCells[0].Sum(c => c.Width) + (2 * 10);
            Assert.That(layout.ScheduleCell.Width, Is.EqualTo(expectedWidth).Within(1e-6));
            // 列權重：前兩列高 = 1.1 / 1.0 倍於第三列
            Assert.That(layout.SchemeCells[0][0].Height / layout.SchemeCells[2][0].Height,
                Is.EqualTo(1.1).Within(1e-6));
            // 全部格子都在版面內
            Assert.That(layout.ScheduleCell.Y + layout.ScheduleCell.Height, Is.LessThanOrEqualTo(700 + 1e-6));
        }
    }
}
