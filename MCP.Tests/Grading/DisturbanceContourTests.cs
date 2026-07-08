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
    }
}
