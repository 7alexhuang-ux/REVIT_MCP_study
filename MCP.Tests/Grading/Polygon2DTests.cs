using System.Collections.Generic;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class Polygon2DTests
    {
        private const double Tolerance = 0.001;

        [Test]
        public void Contains_點位於多邊形內部_回傳真()
        {
            Assert.That(Polygon2D.Contains(Rect(0, 0, 10, 10), new Point2D(5, 5), Tolerance), Is.True);
        }

        [Test]
        public void Contains_點位於多邊形外部_回傳假()
        {
            Assert.That(Polygon2D.Contains(Rect(0, 0, 10, 10), new Point2D(15, 5), Tolerance), Is.False);
        }

        [Test]
        public void Contains_點位於多邊形邊界_回傳真()
        {
            Assert.That(Polygon2D.Contains(Rect(0, 0, 10, 10), new Point2D(10, 5), Tolerance), Is.True);
        }

        [Test]
        public void Overlaps_兩個矩形有實際面積交集_回傳真()
        {
            var a = Rect(0, 0, 10, 10);
            var b = Rect(5, 5, 15, 15);

            Assert.That(Polygon2D.Overlaps(a, b, Tolerance), Is.True);
        }

        [Test]
        public void Overlaps_兩個矩形互不相交_回傳假()
        {
            var a = Rect(0, 0, 10, 10);
            var b = Rect(20, 0, 30, 10);

            Assert.That(Polygon2D.Overlaps(a, b, Tolerance), Is.False);
        }

        [Test]
        public void Overlaps_矩形只有共邊_回傳假()
        {
            var a = Rect(0, 0, 10, 10);
            var b = Rect(10, 0, 20, 10);

            Assert.That(Polygon2D.Overlaps(a, b, Tolerance), Is.False);
        }

        [Test]
        public void Overlaps_相同Footprint有實際面積交集_回傳真()
        {
            var a = Rect(0, 0, 10, 10);
            var b = Rect(0, 0, 10, 10);

            Assert.That(Polygon2D.Overlaps(a, b, Tolerance), Is.True);
        }

        [Test]
        public void Overlaps_矩形貼齊邊界且位於內部_回傳真()
        {
            var outer = Rect(0, 0, 10, 10);
            var inner = Rect(0, 0, 5, 10);

            Assert.That(Polygon2D.Overlaps(outer, inner, Tolerance), Is.True);
        }

        private static readonly Point2D[] UnitSquare =
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
        };

        [Test]
        public void DistanceToBoundary_內外點皆回傳到邊界最短距離()
        {
            Assert.AreEqual(3.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(5, 3)), 1e-9);
            Assert.AreEqual(2.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(12, 5)), 1e-9);
            Assert.AreEqual(5.0, Polygon2D.DistanceToBoundary(UnitSquare, new Point2D(13, 14)), 1e-9);
        }

        [Test]
        public void NearestBoundaryPoint_回傳邊界上最近點()
        {
            var (point, distance) = Polygon2D.NearestBoundaryPoint(UnitSquare, new Point2D(12, 5));
            Assert.AreEqual(10.0, point.X, 1e-9);
            Assert.AreEqual(5.0, point.Y, 1e-9);
            Assert.AreEqual(2.0, distance, 1e-9);
        }

        [Test]
        public void OutwardDirections_正方形四角指向對角外側()
        {
            var directions = Polygon2D.OutwardDirections(UnitSquare);
            Assert.AreEqual(4, directions.Count);
            // 頂點 (0,0)：相鄰邊外法線 (0,-1) 與 (-1,0)，角平分 = (-√2/2, -√2/2)。
            Assert.AreEqual(-System.Math.Sqrt(2) / 2, directions[0].X, 1e-9);
            Assert.AreEqual(-System.Math.Sqrt(2) / 2, directions[0].Y, 1e-9);
            // 頂點 (10,10)：角平分 = (+√2/2, +√2/2)。
            Assert.AreEqual(System.Math.Sqrt(2) / 2, directions[2].X, 1e-9);
            Assert.AreEqual(System.Math.Sqrt(2) / 2, directions[2].Y, 1e-9);
        }

        [Test]
        public void Area_正方形回傳絕對面積且與繞向無關()
        {
            Assert.AreEqual(100.0, Polygon2D.Area(UnitSquare), 1e-9);
            var clockwise = new[]
            {
                new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 0), new Point2D(0, 0)
            };
            Assert.AreEqual(100.0, Polygon2D.Area(clockwise), 1e-9);
        }

        [Test]
        public void OutwardDirections_順時針多邊形結果一致()
        {
            var clockwise = new[]
            {
                new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 0), new Point2D(0, 0)
            };
            var directions = Polygon2D.OutwardDirections(clockwise);
            // clockwise[3] = (0,0)，外向仍應指向 (-,-)。
            Assert.Less(directions[3].X, 0);
            Assert.Less(directions[3].Y, 0);
        }

        private static IReadOnlyList<Point2D> Rect(double minX, double minY, double maxX, double maxY)
        {
            return new[]
            {
                new Point2D(minX, minY),
                new Point2D(maxX, minY),
                new Point2D(maxX, maxY),
                new Point2D(minX, maxY)
            };
        }
    }
}
