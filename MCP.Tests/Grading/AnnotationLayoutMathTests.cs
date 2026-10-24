using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class AnnotationLayoutMathTests
    {
        [Test]
        public void SelectSpacedIndices_DropsPointsCloserThanMinSpacing()
        {
            // 兩片樓板共享角點：同一位置的重複點只留第一個。
            var points = new[]
            {
                new Point2D(0, 0),
                new Point2D(1, 0),      // 距第 0 點 1 < 3 → 淘汰
                new Point2D(10, 0),
                new Point2D(10.5, 0.5), // 距第 2 點 0.71 < 3 → 淘汰
                new Point2D(0, 10)
            };
            var kept = AnnotationLayoutMath.SelectSpacedIndices(points, 3.0);
            Assert.That(kept, Is.EqualTo(new[] { 0, 2, 4 }));
        }

        [Test]
        public void AssignSideColumnSlots_SplitsSidesAndSpreadsSlots()
        {
            // 螢幕座標 (R,U)：R>=splitR 進右欄，同欄由上而下均分槽位。
            var points = new[]
            {
                new Point2D(10, 5),    // 右欄，U 最高
                new Point2D(12, -8),   // 右欄，U 最低
                new Point2D(11, 0),    // 右欄，U 中
                new Point2D(-9, 3)     // 左欄（唯一→槽位置中）
            };
            var slots = AnnotationLayoutMath.AssignSideColumnSlots(points, splitR: 0, minU: -10, maxU: 10);

            Assert.That(slots[0].Side, Is.EqualTo(1));
            Assert.That(slots[3].Side, Is.EqualTo(-1));
            Assert.That(slots[0].SlotU, Is.EqualTo(10).Within(1e-9));   // 最高點吃最高槽
            Assert.That(slots[2].SlotU, Is.EqualTo(0).Within(1e-9));    // 中間點吃中槽
            Assert.That(slots[1].SlotU, Is.EqualTo(-10).Within(1e-9));  // 最低點吃最低槽
            Assert.That(slots[3].SlotU, Is.EqualTo(0).Within(1e-9));    // 單點置中
        }

        [Test]
        public void AssignSideColumnSlots_WithHeights_StacksWithoutOverlapEvenWhenForcedOneSide()
        {
            // 六輪修正：labelSide 強制單側時，5 項全擠一邊——均分格位（舊行為）可能小於
            // 項目實際高度而重疊；帶 itemHeights 時改依高度堆疊，相鄰項間隔須 ≥ 各自半高＋間距。
            var points = new[]
            {
                new Point2D(10, 40),
                new Point2D(10, 25),
                new Point2D(10, 10),
                new Point2D(10, -5),
                new Point2D(10, -20),
            };
            var heights = new double[] { 6, 6, 6, 6, 6 }; // 每項 6 個單位高（如 2 行文字）
            // splitR=+∞ 強制全部進左側（側別 -1），narrow 範圍模擬 (b)(c) 半寬格擠壓後可用範圍不足。
            var slots = AnnotationLayoutMath.AssignSideColumnSlots(
                points, double.PositiveInfinity, minU: -10, maxU: 10, itemHeights: heights, spacing: 1.0);

            Assert.That(slots.All(s => s.Side == -1), Is.True);
            var orderedU = slots.Select(s => s.SlotU).OrderByDescending(u => u).ToList();
            for (var i = 0; i < orderedU.Count - 1; i++)
            {
                // 相鄰兩項中心距須 ≥ 半高+半高+間距 = 6+1 = 7（用各自 6 高度＋1 間距）。
                Assert.That(orderedU[i] - orderedU[i + 1], Is.GreaterThanOrEqualTo(7.0 - 1e-9));
            }

            // 5 項×6 高＋4 個間距=34，遠超 [-10,10] 的 20 範圍——允許往下延伸超出 minU，
            // 驗證這不是被硬壓縮回窄範圍（最後一項中心應遠低於 minU）。
            Assert.That(orderedU.Last(), Is.LessThan(-10));
        }

        [Test]
        public void ComputeRadialStaggerOffsets_SameSectorGetsIncreasingOffsets()
        {
            var center = new Point2D(0, 0);
            // 注意：8 扇區的邊界落在 0°、45°…——取樣點要避開邊界（0° 兩側會分屬不同扇區）。
            var points = new[]
            {
                new Point2D(10, 1),     // 約 5.7°，+X 扇區
                new Point2D(9, 2),      // 約 12.5°，同扇區
                new Point2D(0, 10)      // 90°，+Y 扇區
            };
            var offsets = AnnotationLayoutMath.ComputeRadialStaggerOffsets(
                points, center, sectorCount: 8, baseOffset: 2.0, stepOffset: 1.5);

            var sameSector = new[] { offsets[0], offsets[1] }.OrderBy(v => v).ToArray();
            Assert.That(sameSector[0], Is.EqualTo(2.0).Within(1e-9));
            Assert.That(sameSector[1], Is.EqualTo(3.5).Within(1e-9));
            Assert.That(offsets[2], Is.EqualTo(2.0).Within(1e-9)); // 不同扇區不疊 → base
        }
    }
}
