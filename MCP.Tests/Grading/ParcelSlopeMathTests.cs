using System;
using System.Collections.Generic;
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

        // ────────────────────────────────────────────────
        // 第二期：網格平移、半格比對
        // ────────────────────────────────────────────────

        [Test]
        public void BuildGrid_平移為零時與舊簽章結果完全相同()
        {
            var a = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 25.0, 30.0);
            var b = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 25.0, 30.0, 0.0, 0.0);

            Assert.AreEqual(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Center.X, b[i].Center.X, 0.0);
                Assert.AreEqual(a[i].Center.Y, b[i].Center.Y, 0.0);
            }
        }

        [Test]
        public void BuildGrid_未旋轉時平移半格_每個格心恰為主組格心加半格()
        {
            const double L = 25.0;
            var main = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0);
            var shifted = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0, L / 2, L / 2);

            // 主組內每個格心 + (L/2, L/2) 都應是平移組的某個格心
            foreach (var c in main.Where(m => Math.Abs(m.Center.X - 50) < 30 && Math.Abs(m.Center.Y - 25) < 30))
            {
                var hit = shifted.Any(s =>
                    Math.Abs(s.Center.X - (c.Center.X + L / 2)) < 1e-9
                    && Math.Abs(s.Center.Y - (c.Center.Y + L / 2)) < 1e-9);
                Assert.IsTrue(hit, $"找不到格心 ({c.Center.X + L / 2}, {c.Center.Y + L / 2})");
            }

            // 平移後仍須蓋滿原範圍
            Assert.LessOrEqual(shifted.Min(c => c.Corners.Min(p => p.X)), 0.0);
            Assert.GreaterOrEqual(shifted.Max(c => c.Corners.Max(p => p.X)), 100.0);
            Assert.LessOrEqual(shifted.Min(c => c.Corners.Min(p => p.Y)), 0.0);
            Assert.GreaterOrEqual(shifted.Max(c => c.Corners.Max(p => p.Y)), 50.0);
        }

        [Test]
        public void BuildGrid_旋轉時平移量在網格自身座標軸上_旋轉中心不隨平移移動()
        {
            const double L = 25.0;
            const double angle = 30.0;
            var main = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, angle);
            var shifted = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, angle, L / 2, 0.0);

            var rad = angle * Math.PI / 180.0;
            var dx = L / 2 * Math.Cos(rad); // 沿旋轉後的 X 軸
            var dy = L / 2 * Math.Sin(rad);

            foreach (var c in main.Where(m => Math.Abs(m.Center.X - 50) < 30 && Math.Abs(m.Center.Y - 25) < 30))
            {
                var hit = shifted.Any(s =>
                    Math.Abs(s.Center.X - (c.Center.X + dx)) < 1e-9
                    && Math.Abs(s.Center.Y - (c.Center.Y + dy)) < 1e-9);
                Assert.IsTrue(hit, "旋轉後平移應沿格線方向");
            }
        }

        [Test]
        public void BuildGrid_平移量非有限值丟例外()
        {
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 25.0, 0.0, double.NaN, 0.0));
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.BuildGrid(0, 0, 100, 50, 25.0, 0.0, 0.0, double.PositiveInfinity));
        }

        // ────────────────────────────────────────────────
        // 第二期：基地範圍多邊形裁切
        // ────────────────────────────────────────────────

        private static ParcelPoint[] Square(double x0, double y0, double x1, double y1)
        {
            return new[]
            {
                new ParcelPoint(x0, y0), new ParcelPoint(x1, y0),
                new ParcelPoint(x1, y1), new ParcelPoint(x0, y1),
            };
        }

        private static ParcelPoint[] Poly(params double[] xy)
        {
            var pts = new ParcelPoint[xy.Length / 2];
            for (var i = 0; i < pts.Length; i++)
            {
                pts[i] = new ParcelPoint(xy[2 * i], xy[2 * i + 1]);
            }

            return pts;
        }

        [Test]
        public void AreaInsideBoundary_完全在內_回坵塊整格面積()
        {
            var cell = Square(0, 0, 25, 25);
            var site = Square(-100, -100, 100, 100);
            Assert.AreEqual(625.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_完全在外_回零()
        {
            var cell = Square(0, 0, 25, 25);
            var site = Square(100, 100, 200, 200);
            Assert.AreEqual(0.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 0.0);
        }

        [Test]
        public void AreaInsideBoundary_基地整個落在坵塊內_回基地面積()
        {
            var cell = Square(0, 0, 25, 25);
            var site = Poly(5, 5, 15, 5, 5, 15); // 直角三角形，面積 50
            Assert.AreEqual(50.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_正方格被斜線切成兩半_面積各半()
        {
            var cell = Square(0, 0, 10, 10);

            // 基地邊界沿 x+y=10 斜切：只含左下三角
            var site = Poly(-1, -1, 11, -1, -1, 11);
            Assert.AreEqual(50.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 1e-9);

            // 繞向相反（順時針）結果相同
            var siteCw = site.Reverse().ToArray();
            Assert.AreEqual(50.0, ParcelSlopeMath.AreaInsideBoundary(cell, siteCw), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_坵塊繞向順時針也正確()
        {
            var cell = Square(0, 0, 10, 10).Reverse().ToArray();
            var site = Poly(-1, -1, 11, -1, -1, 11);
            Assert.AreEqual(50.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_凹多邊形_L形基地()
        {
            // L 形：面積 300
            var site = Poly(0, 0, 20, 0, 20, 10, 10, 10, 10, 20, 0, 20);
            Assert.AreEqual(300.0, ParcelSlopeMath.PolygonArea(site), 1e-9);

            // 跨過反折角：x∈[5,15],y∈[5,15] → 下橫臂 10×5=50 + 上直臂 5×5=25
            Assert.AreEqual(75.0, ParcelSlopeMath.AreaInsideBoundary(Square(5, 5, 15, 15), site), 1e-9);

            // 切到反折角：x,y∈[8,18] → 20 + 16
            Assert.AreEqual(36.0, ParcelSlopeMath.AreaInsideBoundary(Square(8, 8, 18, 18), site), 1e-9);

            // 完全落在缺口內
            Assert.AreEqual(0.0, ParcelSlopeMath.AreaInsideBoundary(Square(12, 12, 22, 22), site), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_凹多邊形裁出兩塊分離區域_面積仍正確()
        {
            // U 形：面積 30×20 − 10×10 = 500；坵塊橫跨兩支臂但不碰底部
            var site = Poly(0, 0, 30, 0, 30, 20, 20, 20, 20, 10, 10, 10, 10, 20, 0, 20);
            Assert.AreEqual(500.0, ParcelSlopeMath.PolygonArea(site), 1e-9);

            // y∈[12,18]：左臂 x∈[0,10] + 右臂 x∈[20,30]，各 10×6 → 120
            var cell = Square(0, 12, 30, 18);
            Assert.AreEqual(120.0, ParcelSlopeMath.AreaInsideBoundary(cell, site), 1e-9);
        }

        [Test]
        public void AreaInsideBoundary_旋轉後的坵塊也正確()
        {
            // 以 45° 旋轉的邊長 10 正方形（面積 100），基地為涵蓋其右半邊的大矩形 x>=0
            var cells = ParcelSlopeMath.BuildGrid(-50, -50, 50, 50, 10.0, 45.0);
            var site = Square(0, -1000, 1000, 1000);

            // 所有坵塊落在 x>=0 的面積總和 = 該半平面內網格覆蓋面積，
            // 逐格面積必介於 0 與 100，且完全在內者為 100
            var total = 0.0;
            foreach (var c in cells)
            {
                var a = ParcelSlopeMath.AreaInsideBoundary(c.Corners, site);
                Assert.GreaterOrEqual(a, 0.0);
                Assert.LessOrEqual(a, 100.0 + 1e-9);
                total += a;
            }

            // 網格（旋轉後）以 (0,0) 為中心、半邊 = 一半，面積對稱 → 右半恰為總格數×100 的一半
            Assert.AreEqual(cells.Count * 100.0 / 2.0, total, 1e-6);
        }

        [Test]
        public void PolygonArea_與繞向無關_少於三點為零()
        {
            Assert.AreEqual(100.0, ParcelSlopeMath.PolygonArea(Square(0, 0, 10, 10)), 1e-9);
            Assert.AreEqual(100.0, ParcelSlopeMath.PolygonArea(Square(0, 0, 10, 10).Reverse().ToArray()), 1e-9);
            Assert.AreEqual(0.0, ParcelSlopeMath.PolygonArea(Poly(0, 0, 1, 1)), 0.0);
        }

        // ────────────────────────────────────────────────
        // 第二期：面積加權平均
        // ────────────────────────────────────────────────

        [Test]
        public void WeightedAverageSlope_面積加權_不同於簡單平均()
        {
            // (10×100 + 50×300) / 400 = 40；簡單平均 = 30
            var weighted = ParcelSlopeMath.WeightedAverageSlope(new[] { 10.0, 50.0 }, new[] { 100.0, 300.0 });
            Assert.AreEqual(40.0, weighted, 1e-9);
        }

        [Test]
        public void WeightedAverageSlope_面積全等時等同簡單平均()
        {
            var slopes = new[] { 12.0, 31.5, 60.0, 0.0 };
            var areas = new[] { 625.0, 625.0, 625.0, 625.0 };
            Assert.AreEqual(slopes.Average(), ParcelSlopeMath.WeightedAverageSlope(slopes, areas), 1e-9);
        }

        [Test]
        public void WeightedAverageSlope_無效輸入丟例外()
        {
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.WeightedAverageSlope(new[] { 10.0 }, new[] { 0.0 }));
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.WeightedAverageSlope(new[] { 10.0, 20.0 }, new[] { 5.0 }));
            Assert.Throws<ArgumentException>(
                () => ParcelSlopeMath.WeightedAverageSlope(new[] { 10.0 }, new[] { -1.0 }));
        }

        // ────────────────────────────────────────────────
        // 第二期：輪廓串接與但書概略坡度
        // ────────────────────────────────────────────────

        [Test]
        public void JoinSegmentsToLoops_亂序反向的四條線串成一個封閉輪廓()
        {
            var chains = new IReadOnlyList<ParcelPoint>[]
            {
                new[] { new ParcelPoint(10, 10), new ParcelPoint(0, 10) },   // 反向
                new[] { new ParcelPoint(0, 0), new ParcelPoint(10, 0) },
                new[] { new ParcelPoint(0, 10), new ParcelPoint(0, 0) },
                new[] { new ParcelPoint(10, 0), new ParcelPoint(10, 10) },
            };

            var result = ParcelSlopeMath.JoinSegmentsToLoops(chains, 0.01);

            Assert.AreEqual(1, result.ClosedLoops.Count);
            Assert.AreEqual(0, result.UnclosedChainCount);
            Assert.AreEqual(4, result.ClosedLoops[0].Count);
            Assert.AreEqual(100.0, ParcelSlopeMath.PolygonArea(result.ClosedLoops[0]), 1e-9);
        }

        [Test]
        public void JoinSegmentsToLoops_重複線段被去重()
        {
            // PropertyLine 父元件與子線段常回傳同一條曲線
            var seg = new[] { new ParcelPoint(0, 0), new ParcelPoint(10, 0) };
            var segReversed = new[] { new ParcelPoint(10, 0), new ParcelPoint(0, 0) };
            var chains = new IReadOnlyList<ParcelPoint>[]
            {
                seg, segReversed, seg,
                new[] { new ParcelPoint(10, 0), new ParcelPoint(10, 10) },
                new[] { new ParcelPoint(10, 10), new ParcelPoint(0, 10) },
                new[] { new ParcelPoint(0, 10), new ParcelPoint(0, 0) },
            };

            var result = ParcelSlopeMath.JoinSegmentsToLoops(chains, 0.01);

            Assert.AreEqual(1, result.ClosedLoops.Count);
            Assert.AreEqual(100.0, ParcelSlopeMath.PolygonArea(result.ClosedLoops[0]), 1e-9);
        }

        [Test]
        public void JoinSegmentsToLoops_單一閉合折線與端點容差()
        {
            var closedPolyline = new IReadOnlyList<ParcelPoint>[]
            {
                new[]
                {
                    new ParcelPoint(0, 0), new ParcelPoint(10, 0), new ParcelPoint(10, 10),
                    new ParcelPoint(0, 10), new ParcelPoint(0, 0),
                },
            };
            Assert.AreEqual(1, ParcelSlopeMath.JoinSegmentsToLoops(closedPolyline, 0.01).ClosedLoops.Count);

            // 端點差 5mm：容差 1cm 內相接
            var gap = new IReadOnlyList<ParcelPoint>[]
            {
                new[] { new ParcelPoint(0, 0), new ParcelPoint(10, 0), new ParcelPoint(10, 10) },
                new[] { new ParcelPoint(10.004, 10), new ParcelPoint(0, 10), new ParcelPoint(0.003, 0) },
            };
            Assert.AreEqual(1, ParcelSlopeMath.JoinSegmentsToLoops(gap, 0.01).ClosedLoops.Count);
        }

        [Test]
        public void JoinSegmentsToLoops_未閉合的線計入未閉合數()
        {
            var chains = new IReadOnlyList<ParcelPoint>[]
            {
                new[] { new ParcelPoint(0, 0), new ParcelPoint(10, 0) },
                new[] { new ParcelPoint(10, 0), new ParcelPoint(10, 10) },
            };

            var result = ParcelSlopeMath.JoinSegmentsToLoops(chains, 0.01);

            Assert.AreEqual(0, result.ClosedLoops.Count);
            // 開放的 L 形折線：兩條線都有孤立端點，剔除懸線後全數被丟棄
            Assert.AreEqual(2, result.DanglingChainCount + result.UnclosedChainCount);
        }

        [Test]
        public void JoinSegmentsToLoops_U形輪廓角點多一條短線_剔除懸線後仍得一個封閉輪廓()
        {
            // U 形（面積 500）以逐段線給出，角點 (30,0) 另外多接一條伸出去的短線
            var u = new[]
            {
                new[] { new ParcelPoint(0, 0), new ParcelPoint(30, 0) },
                new[] { new ParcelPoint(30, 0), new ParcelPoint(30, 20) },
                new[] { new ParcelPoint(30, 20), new ParcelPoint(20, 20) },
                new[] { new ParcelPoint(20, 20), new ParcelPoint(20, 10) },
                new[] { new ParcelPoint(20, 10), new ParcelPoint(10, 10) },
                new[] { new ParcelPoint(10, 10), new ParcelPoint(10, 20) },
                new[] { new ParcelPoint(10, 20), new ParcelPoint(0, 20) },
                new[] { new ParcelPoint(0, 20), new ParcelPoint(0, 0) },
            };
            var chains = new List<IReadOnlyList<ParcelPoint>>(u)
            {
                new[] { new ParcelPoint(30, 0), new ParcelPoint(33, -2) }, // 懸線
                new[] { new ParcelPoint(33, -2), new ParcelPoint(34, -2) }, // 懸線尾端再接一小段
            };

            var result = ParcelSlopeMath.JoinSegmentsToLoops(chains, 0.01);

            Assert.AreEqual(1, result.ClosedLoops.Count);
            Assert.AreEqual(500.0, ParcelSlopeMath.PolygonArea(result.ClosedLoops[0]), 1e-9);
            Assert.AreEqual(2, result.DanglingChainCount, "反覆剔除：先剔外端短線，再剔內側短線");
            Assert.AreEqual(0, result.UnclosedChainCount);
        }

        [Test]
        public void JoinSegmentsToLoops_沒有懸線時懸線數為零()
        {
            var chains = new IReadOnlyList<ParcelPoint>[]
            {
                new[] { new ParcelPoint(0, 0), new ParcelPoint(10, 0), new ParcelPoint(10, 10), new ParcelPoint(0, 10), new ParcelPoint(0, 0) },
            };
            var result = ParcelSlopeMath.JoinSegmentsToLoops(chains, 0.01);
            Assert.AreEqual(1, result.ClosedLoops.Count);
            Assert.AreEqual(0, result.DanglingChainCount);
        }

        [Test]
        public void RoughSlopeBetweenExtremes_長等高線_取最高程點與最低程點間的最短水平距離()
        {
            // 最高線 z=100 沿 y=0 從 x=0 到 x=1000；最低線 z=90 沿 y=50 從 x=0 到 x=1000。
            // 最短水平距離 = 50（兩線平行間距），坡度 = 10/50 = 20%。
            // 若只取「第一個遇到的極值點」，可能配成 (0,0)→(1000,50) 之類，距離約 1000 → 約 1%，嚴重低估。
            var pts = new List<ParcelPoint3>();
            for (var x = 0; x <= 1000; x += 100)
            {
                pts.Add(new ParcelPoint3(x, 0, 100));
            }

            for (var x = 1000; x >= 0; x -= 100)
            {
                pts.Add(new ParcelPoint3(x, 50, 90));
            }

            pts.Add(new ParcelPoint3(500, 25, 95)); // 中間等高線不影響

            var rough = ParcelSlopeMath.RoughSlopeBetweenExtremes(pts);

            Assert.IsNotNull(rough);
            Assert.AreEqual(50.0, rough.HorizontalDistanceMeters, 1e-9);
            Assert.AreEqual(20.0, rough.SlopePercent, 1e-9);
            Assert.AreEqual(100.0, rough.Highest.Z);
            Assert.AreEqual(90.0, rough.Lowest.Z);
        }

        // ────────────────────────────────────────────────
        // 平移量取正餘數
        // ────────────────────────────────────────────────

        [Test]
        public void NormalizeOffset_取正餘數落在0到L之間()
        {
            Assert.AreEqual(0.0, ParcelSlopeMath.NormalizeOffset(0.0, 25.0), 0.0);
            Assert.AreEqual(12.5, ParcelSlopeMath.NormalizeOffset(12.5, 25.0), 1e-12);
            Assert.AreEqual(12.5, ParcelSlopeMath.NormalizeOffset(-12.5, 25.0), 1e-12);
            Assert.AreEqual(10.0, ParcelSlopeMath.NormalizeOffset(60.0, 25.0), 1e-12);
            Assert.AreEqual(15.0, ParcelSlopeMath.NormalizeOffset(-60.0, 25.0), 1e-12);
            Assert.AreEqual(0.0, ParcelSlopeMath.NormalizeOffset(25.0, 25.0), 1e-12);
            Assert.AreEqual(0.0, ParcelSlopeMath.NormalizeOffset(-25.0, 25.0), 1e-12);

            foreach (var o in new[] { -1e-20, -3.3, 7.7, 1234.5, -987.6 })
            {
                var r = ParcelSlopeMath.NormalizeOffset(o, 10.0);
                Assert.GreaterOrEqual(r, 0.0);
                Assert.Less(r, 10.0);
            }

            Assert.Throws<ArgumentException>(() => ParcelSlopeMath.NormalizeOffset(double.NaN, 25.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParcelSlopeMath.NormalizeOffset(1.0, 0.0));
        }

        [Test]
        public void BuildGrid_超大或負的平移量_等同取餘後的平移_格數不暴增()
        {
            const double L = 25.0;
            var baseline = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0, 12.5, 12.5);
            var big = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0, 12.5 + 100 * L, 12.5 - 40 * L);
            var neg = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0, -12.5, -12.5);

            Assert.AreEqual(baseline.Count, big.Count, "取餘後格數必須相同，不因平移量大而暴增");
            Assert.AreEqual(baseline.Count, neg.Count);
            for (var i = 0; i < baseline.Count; i++)
            {
                Assert.AreEqual(baseline[i].Center.X, big[i].Center.X, 1e-6);
                Assert.AreEqual(baseline[i].Center.Y, big[i].Center.Y, 1e-6);
                Assert.AreEqual(baseline[i].Center.X, neg[i].Center.X, 1e-6);
                Assert.AreEqual(baseline[i].Center.Y, neg[i].Center.Y, 1e-6);
            }

            // 平移整數格 = 沒平移
            var none = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0);
            var whole = ParcelSlopeMath.BuildGrid(0, 0, 100, 50, L, 0.0, 3 * L, -2 * L);
            Assert.AreEqual(none.Count, whole.Count);
            for (var i = 0; i < none.Count; i++)
            {
                Assert.AreEqual(none[i].Center.X, whole[i].Center.X, 1e-6);
                Assert.AreEqual(none[i].Center.Y, whole[i].Center.Y, 1e-6);
            }
        }

        [Test]
        public void RoughSlopeBetweenExtremes_高差除水平距離()
        {
            // 高差 10、水平距離 50 → 20%
            var rough = ParcelSlopeMath.RoughSlopeBetweenExtremes(new[]
            {
                new ParcelPoint3(0, 0, 10), new ParcelPoint3(30, 40, 20), new ParcelPoint3(5, 5, 15),
            });
            Assert.IsNotNull(rough);
            Assert.AreEqual(20.0, rough.SlopePercent, 1e-9);
            Assert.AreEqual(10.0, rough.ElevationDifferenceMeters, 1e-9);
            Assert.AreEqual(50.0, rough.HorizontalDistanceMeters, 1e-9);

            // 低於 15% 的情形（供但書提示）：高差 10、距離 100 → 10%
            var low = ParcelSlopeMath.RoughSlopeBetweenExtremes(new[]
            {
                new ParcelPoint3(0, 0, 0), new ParcelPoint3(100, 0, 10),
            });
            Assert.AreEqual(10.0, low.SlopePercent, 1e-9);
        }

        [Test]
        public void RoughSlopeBetweenExtremes_無高程資訊或空集合回null()
        {
            Assert.IsNull(ParcelSlopeMath.RoughSlopeBetweenExtremes(new[]
            {
                new ParcelPoint3(0, 0, 5), new ParcelPoint3(50, 0, 5),
            }));
            Assert.IsNull(ParcelSlopeMath.RoughSlopeBetweenExtremes(new ParcelPoint3[0]));
            Assert.IsNull(ParcelSlopeMath.RoughSlopeBetweenExtremes(null));
        }

        [Test]
        public void PointInPolygon_凹多邊形()
        {
            var site = Poly(0, 0, 20, 0, 20, 10, 10, 10, 10, 20, 0, 20);
            Assert.IsTrue(ParcelSlopeMath.PointInPolygon(site, 5, 15));
            Assert.IsTrue(ParcelSlopeMath.PointInPolygon(site, 15, 5));
            Assert.IsFalse(ParcelSlopeMath.PointInPolygon(site, 15, 15), "缺口內");
            Assert.IsFalse(ParcelSlopeMath.PointInPolygon(site, -1, 5));
        }
    }
}
