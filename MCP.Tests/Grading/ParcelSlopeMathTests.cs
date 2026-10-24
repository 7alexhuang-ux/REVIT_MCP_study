using System;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// 坵塊法計算測試。法源與方法定義見 domain/parcel-slope-analysis.md。
    /// 這些測試鎖定的是<b>法定行為</b>，不是實作細節——改動前先回查法條原文。
    /// </summary>
    [TestFixture]
    public class ParcelSlopeMathTests
    {
        /// <summary>
        /// 第 261 條原文明訂「π：圓周率（3.14）」。送審計算書必須能被審查單位
        /// 以 3.14 逐格手算複核，故此值鎖死，不得改用 Math.PI。
        /// </summary>
        [Test]
        public void LegalPi_必須恰為法條明訂的3點14_不得用MathPI()
        {
            Assert.AreEqual(3.14, ParcelSlopeMath.LegalPi, 0.0);
            Assert.AreNotEqual(Math.PI, ParcelSlopeMath.LegalPi);
        }

        /// <summary>
        /// S(%) = n·π·h / (8·L) × 100，可手算複核：
        /// n=10, h=1m, L=25m → 10 × 3.14 × 1 / (8 × 25) × 100 = 15.7%
        /// </summary>
        [Test]
        public void SlopePercent_手算複核值()
        {
            Assert.AreEqual(15.7, ParcelSlopeMath.SlopePercent(10, 1.0, 25.0), 1e-9);

            // n=20, h=5m, L=25m → 20 × 3.14 × 5 / 200 × 100 = 157%
            Assert.AreEqual(157.0, ParcelSlopeMath.SlopePercent(20, 5.0, 25.0), 1e-9);

            // 交點數 0 → 平坦
            Assert.AreEqual(0.0, ParcelSlopeMath.SlopePercent(0, 1.0, 25.0), 1e-9);
        }

        /// <summary>
        /// 🔴 本 SOP 最容易出錯之處：L 是<b>單邊</b>邊長，不是四邊總長。
        /// 若誤用 4L，坡度會低估為正確值的 1/4。此測試把兩者的差距釘死。
        /// </summary>
        [Test]
        public void SlopePercent_分母L為單邊邊長_誤用四邊總長會低估為四分之一()
        {
            var correct = ParcelSlopeMath.SlopePercent(10, 1.0, 25.0);

            // 模擬「誤把 L 當成四邊總長 4×25=100」的錯誤實作結果
            var wrongIfUsedPerimeter = 10 * ParcelSlopeMath.LegalPi * 1.0 / (8.0 * 100.0) * 100.0;

            Assert.AreEqual(correct / 4.0, wrongIfUsedPerimeter, 1e-9,
                "L 必須是單邊邊長；用四邊總長會讓坡度低估為 1/4。");
        }

        [Test]
        public void SlopePercent_坵塊邊長超過法定上限25公尺_必須丟例外()
        {
            Assert.DoesNotThrow(() => ParcelSlopeMath.SlopePercent(10, 1.0, 25.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParcelSlopeMath.SlopePercent(10, 1.0, 25.0001));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParcelSlopeMath.SlopePercent(10, 1.0, 30.0));
        }

        [Test]
        public void SlopePercent_等高線間距與交點數的無效輸入()
        {
            // h 必須由使用者提供且為正數，不得預設
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.SlopePercent(10, 0.0, 25.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.SlopePercent(10, -1.0, 25.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.SlopePercent(10, double.NaN, 25.0));

            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.SlopePercent(-1, 1.0, 25.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.SlopePercent(10, 1.0, 0.0));
        }

        /// <summary>
        /// §262：≤30 無限制；超過 30 且未逾 55 → 限空地；超過 55 → 不得計入法定空地。
        /// 「未逾」含 55，故邊界右閉。
        /// </summary>
        [Test]
        public void Classify_邊界採右閉_對齊262條用語()
        {
            Assert.AreEqual(ParcelRestriction.None, ParcelSlopeMath.Classify(0.0));
            Assert.AreEqual(ParcelRestriction.None, ParcelSlopeMath.Classify(29.999));
            Assert.AreEqual(ParcelRestriction.None, ParcelSlopeMath.Classify(30.0), "恰 30% 不算「超過」30%");

            Assert.AreEqual(ParcelRestriction.OpenSpaceOnly, ParcelSlopeMath.Classify(30.001));
            Assert.AreEqual(ParcelRestriction.OpenSpaceOnly, ParcelSlopeMath.Classify(55.0), "「未逾 55」含 55");

            Assert.AreEqual(ParcelRestriction.ExcludedFromOpenSpace, ParcelSlopeMath.Classify(55.001));
            Assert.AreEqual(ParcelRestriction.ExcludedFromOpenSpace, ParcelSlopeMath.Classify(200.0));
        }

        [Test]
        public void Classify_非有限值丟例外()
        {
            Assert.Throws<ArgumentException>(() => ParcelSlopeMath.Classify(double.NaN));
            Assert.Throws<ArgumentException>(() => ParcelSlopeMath.Classify(double.PositiveInfinity));
        }

        /// <summary>
        /// 「加強保育地」以地質條件界定，與坡度無關，任何標示都不得出現此詞。
        /// 同理不得輸出「不得開發建築」這類終局結論（§262 需先劃均質區並判但書，
        /// 見 domain/parcel-slope-analysis.md §4）。
        /// </summary>
        [Test]
        public void RestrictionLabel_不得出現加強保育地或終局開發結論()
        {
            foreach (ParcelRestriction r in Enum.GetValues(typeof(ParcelRestriction)))
            {
                var label = ParcelSlopeMath.RestrictionLabel(r);
                Assert.IsFalse(label.Contains("加強保育地"), $"{r} 不得標為加強保育地");
                Assert.IsFalse(label.Contains("不得開發"), $"{r} 不得輸出終局開發結論");
            }

            Assert.AreEqual(string.Empty, ParcelSlopeMath.RestrictionLabel(ParcelRestriction.None));
            Assert.AreEqual("限空地／不得配置建築物",
                ParcelSlopeMath.RestrictionLabel(ParcelRestriction.OpenSpaceOnly));
            Assert.AreEqual("不得計入法定空地",
                ParcelSlopeMath.RestrictionLabel(ParcelRestriction.ExcludedFromOpenSpace));
        }

        [Test]
        public void BuildGrid_未旋轉時為正方格且涵蓋整個範圍()
        {
            var cells = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 25.0, 0.0);

            Assert.Greater(cells.Count, 0);

            foreach (var cell in cells)
            {
                Assert.AreEqual(4, cell.Corners.Count);

                // 每邊長都應等於 cellSize（正方格）
                for (var i = 0; i < 4; i++)
                {
                    var a = cell.Corners[i];
                    var b = cell.Corners[(i + 1) % 4];
                    var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    Assert.AreEqual(25.0, len, 1e-9, "坵塊必須是正方格");
                }
            }

            // 網格必須蓋過原範圍
            Assert.LessOrEqual(cells.Min(c => c.Corners.Min(p => p.X)), 0.0);
            Assert.GreaterOrEqual(cells.Max(c => c.Corners.Max(p => p.X)), 100.0);
            Assert.LessOrEqual(cells.Min(c => c.Corners.Min(p => p.Y)), 0.0);
            Assert.GreaterOrEqual(cells.Max(c => c.Corners.Max(p => p.Y)), 50.0);
        }

        /// <summary>
        /// 旋轉中心固定為範圍中心（domain §3.4），旋轉後網格中心不得偏移，
        /// 否則整個網格會飛離基地。
        /// </summary>
        [Test]
        public void BuildGrid_旋轉後仍以範圍中心為中心且維持正方格()
        {
            const double minX = 0, minY = 0, maxX = 100, maxY = 50;
            var expectedCx = (minX + maxX) / 2.0;
            var expectedCy = (minY + maxY) / 2.0;

            foreach (var angle in new[] { 0.0, 30.0, 45.0, 90.0, -17.5 })
            {
                var cells = ParcelSlopeMath.BuildGrid(minX, minY, maxX, maxY, 25.0, angle);

                var cx = cells.Average(c => c.Center.X);
                var cy = cells.Average(c => c.Center.Y);
                Assert.AreEqual(expectedCx, cx, 1e-6, $"旋轉 {angle}° 後網格中心不應偏移");
                Assert.AreEqual(expectedCy, cy, 1e-6, $"旋轉 {angle}° 後網格中心不應偏移");

                // 旋轉是剛體變換，邊長必須保持
                var cell = cells[0];
                var a = cell.Corners[0];
                var b = cell.Corners[1];
                var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                Assert.AreEqual(25.0, len, 1e-9, $"旋轉 {angle}° 後仍須為正方格");
            }
        }

        [Test]
        public void BuildGrid_邊長超過法定上限或範圍無效時丟例外()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 26.0, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 0.0, 0.0));
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.BuildGrid(0, 0, 0, 50, 25.0, 0.0));
        }
    }
}
