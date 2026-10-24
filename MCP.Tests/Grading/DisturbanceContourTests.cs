using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// DisturbanceContour：擾動遮罩 → 封閉邊界環萃取。
    /// 實機教訓（2026-07-08）：範圍線用「逐板重建坡腳環」會多環交疊、跳過處連弦、飄出地形，
    /// 使用者裁決範圍線必須是「一條封閉、不自交的完整曲線」→ 改為量測結果：
    /// 比對原/設計地形取得擾動遮罩，萃取其邊界環（多板聯集、地形邊界、上限截止全自動正確）。
    /// </summary>
    [TestFixture]
    public class DisturbanceContourTests
    {
        [Test]
        public void Extract_EmptyMask_NoLoops()
        {
            var mask = new bool[3, 3];
            Assert.That(DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0), Is.Empty);
        }

        [Test]
        public void Extract_SingleNode_UnitSquareAroundNode()
        {
            var mask = new bool[1, 1];
            mask[0, 0] = true;
            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 10.0, 20.0, 2.0);

            Assert.That(loops, Has.Count.EqualTo(1));
            var loop = loops[0];
            Assert.That(loop, Has.Count.EqualTo(4)); // 共線合併後只剩四個角
            // 節點 (10,20)、步距 2 → 邊界為節點外擴半步（±1）
            var xs = loop.Select(p => p.X).OrderBy(v => v).ToList();
            var ys = loop.Select(p => p.Y).OrderBy(v => v).ToList();
            Assert.That(xs.First(), Is.EqualTo(9.0).Within(1e-9));
            Assert.That(xs.Last(), Is.EqualTo(11.0).Within(1e-9));
            Assert.That(ys.First(), Is.EqualTo(19.0).Within(1e-9));
            Assert.That(ys.Last(), Is.EqualTo(21.0).Within(1e-9));
        }

        [Test]
        public void Extract_RectangularBlock_SingleRectangleLoop()
        {
            var mask = new bool[4, 2]; // 4x2 全填
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 2; y++)
            {
                mask[x, y] = true;
            }

            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(1));
            Assert.That(loops[0], Has.Count.EqualTo(4)); // 共線合併 → 矩形四角
        }

        [Test]
        public void Extract_LShape_SingleLoopSixCorners()
        {
            // ██
            // █
            var mask = new bool[2, 2];
            mask[0, 0] = true;
            mask[0, 1] = true;
            mask[1, 1] = true;
            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(1));
            Assert.That(loops[0], Has.Count.EqualTo(6)); // L 形六個角
        }

        [Test]
        public void Simplify_LShape_PreservesConcaveFootprint()
        {
            // ██
            // █
            // 回歸：範圍線不可用凸包外包成矩形，否則會和真實擾動範圍明顯錯位。
            var mask = new bool[2, 2];
            mask[0, 0] = true;
            mask[0, 1] = true;
            mask[1, 1] = true;

            var loop = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0).Single();
            var simplified = DisturbanceContour.SimplifyLoop(loop, 0.35);

            var minX = simplified.Min(point => point.X);
            var maxX = simplified.Max(point => point.X);
            var minY = simplified.Min(point => point.Y);
            var maxY = simplified.Max(point => point.Y);
            var boundingArea = (maxX - minX) * (maxY - minY);

            Assert.That(Polygon2D.Area(simplified), Is.LessThan(boundingArea));
            Assert.That(simplified.Count, Is.GreaterThanOrEqualTo(5));
        }

        [Test]
        public void Extract_DonutMask_OuterAndHoleLoops()
        {
            var mask = new bool[3, 3];
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
            {
                mask[x, y] = x != 1 || y != 1; // 3x3 挖中心
            }

            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(2)); // 外環＋內孔環
            var cornerCounts = loops.Select(loop => loop.Count).OrderBy(c => c).ToList();
            Assert.That(cornerCounts, Is.EqualTo(new[] { 4, 4 })); // 兩個都是矩形
        }

        [Test]
        public void Extract_TwoSeparateNodes_TwoLoops()
        {
            var mask = new bool[3, 1];
            mask[0, 0] = true;
            mask[2, 0] = true;
            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(2));
        }

        [Test]
        public void Extract_DiagonalTouchingNodes_SeparateLoops()
        {
            // 對角相觸（棋盤角）不可黏成一條自交環——左轉優先解歧。
            var mask = new bool[2, 2];
            mask[0, 0] = true;
            mask[1, 1] = true;
            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(2));
            Assert.That(loops.All(loop => loop.Count == 4), Is.True);
        }

        [Test]
        public void Simplify_Staircase_CollapsesToDiagonalWithinTolerance()
        {
            // 1m 階梯斜線 → 容差 0.7 步距時應簡化為近似對角直線（大幅減點）。
            var staircase = new[]
            {
                new Point2D(0, 0), new Point2D(1, 0), new Point2D(1, 1), new Point2D(2, 1),
                new Point2D(2, 2), new Point2D(3, 2), new Point2D(3, 3), new Point2D(0, 3),
            };
            var simplified = DisturbanceContour.SimplifyLoop(staircase, 0.8);
            Assert.That(simplified.Count, Is.LessThan(staircase.Length));
            // 起訖與遠端角點仍保留在容差內：驗每個原始點到簡化折線距離 ≤ 容差
            foreach (var point in staircase)
            {
                var minDistance = double.MaxValue;
                for (var i = 0; i < simplified.Count; i++)
                {
                    var a = simplified[i];
                    var b = simplified[(i + 1) % simplified.Count];
                    minDistance = System.Math.Min(minDistance, DistancePointToSegment(point, a, b));
                }

                Assert.That(minDistance, Is.LessThanOrEqualTo(0.8 + 1e-9));
            }
        }

        private static double DistancePointToSegment(Point2D p, Point2D a, Point2D b)
        {
            var abX = b.X - a.X;
            var abY = b.Y - a.Y;
            var lengthSquared = (abX * abX) + (abY * abY);
            if (lengthSquared < 1e-12)
            {
                var dx = p.X - a.X;
                var dy = p.Y - a.Y;
                return System.Math.Sqrt((dx * dx) + (dy * dy));
            }

            var t = System.Math.Max(0, System.Math.Min(1,
                (((p.X - a.X) * abX) + ((p.Y - a.Y) * abY)) / lengthSquared));
            var px = a.X + (t * abX);
            var py = a.Y + (t * abY);
            var ddx = p.X - px;
            var ddy = p.Y - py;
            return System.Math.Sqrt((ddx * ddx) + (ddy * ddy));
        }

        // ── ExtractIsoContourLoops（D4：等值線內插，消滅範圍線 1m 階梯鋸齒）──

        [Test]
        public void IsoContour_DiagonalField_FollowsAnalyticLineWithoutStaircase()
        {
            // field[ix,iy] = 6 - (|ix-5|+|iy-5|)（以 (5,5) 為頂點的 L1 錐狀場，分段線性）、iso=2
            // → 擾動區 = |dx|+|dy| < 4 的菱形，四個角落分別落在 (1,5)(9,5)(5,1)(5,9)，
            // 與陣列邊界（0、10）都留 1 步距緣，不觸邊界、不受 NaN 退中點影響。
            // 菱形四邊在各自象限內都是仿射函式，雙線性內插對仿射函式精確無誤差 → 交點應恰落在
            // 解析菱形邊上（距離 0），同時菱形邊本身即為非軸向線段，驗證消滅階梯。
            const int size = 11;
            var center = new Point2D(5, 5);
            var field = new double[size, size];
            for (var ix = 0; ix < size; ix++)
            for (var iy = 0; iy < size; iy++)
            {
                field[ix, iy] = 6.0 - (System.Math.Abs(ix - center.X) + System.Math.Abs(iy - center.Y));
            }

            var loops = DisturbanceContour.ExtractIsoContourLoops(field, 2.0, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(1));

            var hasDiagonalSegment = false;
            foreach (var loop in loops)
            {
                foreach (var point in loop)
                {
                    var dx = point.X - center.X;
                    var dy = point.Y - center.Y;
                    // 到菱形四邊 (±dx±dy=4) 中最近一邊的歐氏距離。
                    var candidates = new[] { dx + dy - 4, dx + dy + 4, dx - dy - 4, dx - dy + 4 };
                    var nearest = candidates.Select(System.Math.Abs).Min() / System.Math.Sqrt(2);
                    Assert.That(nearest, Is.LessThan(0.1),
                        $"頂點 ({point.X},{point.Y}) 偏離解析菱形邊過遠。");
                }

                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    if (System.Math.Abs(a.X - b.X) > 1e-9 && System.Math.Abs(a.Y - b.Y) > 1e-9)
                    {
                        hasDiagonalSegment = true;
                    }
                }
            }

            Assert.That(hasDiagonalSegment, Is.True, "應存在 dx≠0 且 dy≠0 的斜向線段，證明未退化為水平/垂直階梯。");
        }

        [Test]
        public void IsoContour_RadialField_ApproximatesCircle()
        {
            // field = R - dist(node, center)，iso=0 → 「擾動＝field>0」即 dist<R，環頂點應貼近半徑 R 的圓。
            const int size = 9;
            const double radius = 3.5;
            var center = new Point2D(4, 4);
            var field = new double[size, size];
            for (var ix = 0; ix < size; ix++)
            for (var iy = 0; iy < size; iy++)
            {
                var dx = ix - center.X;
                var dy = iy - center.Y;
                field[ix, iy] = radius - System.Math.Sqrt((dx * dx) + (dy * dy));
            }

            var loops = DisturbanceContour.ExtractIsoContourLoops(field, 0, 0, 0, 1.0);
            Assert.That(loops, Has.Count.EqualTo(1));

            foreach (var point in loops[0])
            {
                var dx = point.X - center.X;
                var dy = point.Y - center.Y;
                var actualRadius = System.Math.Sqrt((dx * dx) + (dy * dy));
                Assert.That(System.Math.Abs(actualRadius - radius), Is.LessThan(0.15),
                    $"頂點 ({point.X},{point.Y}) 半徑 {actualRadius} 偏離 R={radius} 過遠。");
            }
        }

        [Test]
        public void IsoContour_AllBelowThreshold_ReturnsEmpty()
        {
            var field = new double[3, 3];
            var loops = DisturbanceContour.ExtractIsoContourLoops(field, 10.0, 0, 0, 1.0);
            Assert.That(loops, Is.Empty);
        }

        [Test]
        public void IsoContour_NaNRegion_TreatedAsUndisturbed()
        {
            // 4x4 全擾動，僅右上角節點設 NaN（未取樣）→ 環應繞過該角形成缺口而非進入，但仍需閉合成單一環。
            // 對照組改用「同一 ExtractIsoContourLoops、但該節點維持真實值」的結果（同一套角落斜切慣例），
            // 純粹隔離 NaN 缺口本身造成的面積差，避免與 ExtractBoundaryLoops 的直角外擴慣例混淆比較。
            const int size = 4;
            var fieldWithNaN = new double[size, size];
            var fieldWithoutNaN = new double[size, size];
            for (var ix = 0; ix < size; ix++)
            for (var iy = 0; iy < size; iy++)
            {
                fieldWithNaN[ix, iy] = 10.0;
                fieldWithoutNaN[ix, iy] = 10.0;
            }

            fieldWithNaN[size - 1, size - 1] = double.NaN;

            var notchedLoops = DisturbanceContour.ExtractIsoContourLoops(fieldWithNaN, 1.0, 0, 0, 1.0);
            Assert.That(notchedLoops, Has.Count.EqualTo(1), "NaN 缺口不應打斷環的封閉性。");

            var fullLoops = DisturbanceContour.ExtractIsoContourLoops(fieldWithoutNaN, 1.0, 0, 0, 1.0);
            Assert.That(fullLoops, Has.Count.EqualTo(1));

            var notchedArea = Polygon2D.Area(notchedLoops[0]);
            var fullArea = Polygon2D.Area(fullLoops[0]);

            Assert.That(notchedArea, Is.LessThan(fullArea), "NaN 角應被排除、環面積應小於無缺口對照環。");
            Assert.That(notchedArea, Is.GreaterThan(fullArea * 0.9),
                "缺口只應是單一節點附近的小面積，不應大幅侵蝕環（>10%）。");
        }

        [Test]
        public void IsoContour_FilledRectangle_MatchesBoundaryLoopsOrientation()
        {
            // 與 ExtractBoundaryLoops 對同尺寸全填場比較：方向規約必須相同（signed area 同號）。
            // 注意：面積不要求逐位元相等——marching squares 在陣列邊界外角以 NaN 退中點會斜切
            // 45 度小三角（單一虛擬角僅一個真實角擾動），與 ExtractBoundaryLoops 的直角外擴方形
            // 在角點附近略有差異，屬設計內（貼地內插的自然結果），不是本測試要驗的東西。
            const int countX = 4;
            const int countY = 2;
            var field = new double[countX, countY];
            for (var ix = 0; ix < countX; ix++)
            for (var iy = 0; iy < countY; iy++)
            {
                field[ix, iy] = 10.0;
            }

            var isoLoops = DisturbanceContour.ExtractIsoContourLoops(field, 1.0, 0, 0, 1.0);
            Assert.That(isoLoops, Has.Count.EqualTo(1));

            var mask = new bool[countX, countY];
            for (var ix = 0; ix < countX; ix++)
            for (var iy = 0; iy < countY; iy++)
            {
                mask[ix, iy] = true;
            }

            var boundaryLoops = DisturbanceContour.ExtractBoundaryLoops(mask, 0, 0, 1.0);
            Assert.That(boundaryLoops, Has.Count.EqualTo(1));

            var isoSignedArea = SignedArea(isoLoops[0]);
            var boundarySignedArea = SignedArea(boundaryLoops[0]);
            Assert.That(System.Math.Sign(isoSignedArea), Is.EqualTo(System.Math.Sign(boundarySignedArea)),
                "外環方向規約必須與 ExtractBoundaryLoops 相同（擾動區在左側／CCW 正號）。");
            Assert.That(System.Math.Sign(isoSignedArea), Is.EqualTo(1), "單一凸擾動區外環應為正號（CCW）。");

            // 面積應在同一量級（角落斜切損失有界，遠小於整體面積）。
            Assert.That(Polygon2D.Area(isoLoops[0]),
                Is.EqualTo(Polygon2D.Area(boundaryLoops[0])).Within(1.0));
        }

        [Test]
        public void SmoothClosedLoop_DampsAlternatingNoiseKeepsCentroid()
        {
            // 直線上 ±0.3 交錯抖動（模擬門檻附近三角化雜訊的等值線抖動）：
            // 平滑後殘餘抖幅應大減，且質心不漂移（循環視窗平均的守恆性質）。
            var noisy = new System.Collections.Generic.List<Point2D>();
            for (var i = 0; i < 40; i++)
            {
                var along = i < 20 ? i : 39 - i;            // 去回兩趟構成封閉環
                var side = i < 20 ? 0.0 : 3.0;              // 環寬 3
                noisy.Add(new Point2D(along, side + ((i % 2 == 0) ? 0.3 : -0.3)));
            }

            var smoothed = DisturbanceContour.SmoothClosedLoop(noisy, 2);
            Assert.That(smoothed.Count, Is.EqualTo(noisy.Count));

            // 只量遠離轉角的直段（環端點被兩側平均拉向中線是預期行為，不列入抖幅）。
            double MaxWiggle(System.Collections.Generic.IReadOnlyList<Point2D> loop)
            {
                var max = 0.0;
                foreach (var index in new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
                    25, 26, 27, 28, 29, 30, 31, 32, 33, 34 })
                {
                    var target = index < 20 ? 0.0 : 3.0;
                    max = System.Math.Max(max, System.Math.Abs(loop[index].Y - target));
                }

                return max;
            }

            Assert.That(MaxWiggle(smoothed), Is.LessThan(MaxWiggle(noisy) * 0.5));
            Assert.That(smoothed.Average(p => p.X), Is.EqualTo(noisy.Average(p => p.X)).Within(1e-9));
        }

        private static double SignedArea(System.Collections.Generic.IReadOnlyList<Point2D> polygon)
        {
            var doubled = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var next = polygon[(i + 1) % polygon.Count];
                doubled += (current.X * next.Y) - (next.X * current.Y);
            }

            return doubled / 2.0;
        }
    }
}
