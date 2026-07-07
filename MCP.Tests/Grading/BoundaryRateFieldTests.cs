using System.Collections.Generic;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class BoundaryRateFieldTests
    {
        // 10×10 正方形（CCW）：e0 底(1:2)、e1 右(1:12)、e2 頂(1:2)、e3 左(1:2)。
        private static List<Point2D> Square() => new List<Point2D>
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
        };

        private static BoundaryRateField Field(double blend = 2.0)
            => new BoundaryRateField(Square(), new[] { 2.0, 12.0, 2.0, 2.0 }, blend);

        [Test]
        public void RateAt_MidEdge_FarFromCorners_ReturnsEdgeRate()
        {
            Assert.That(Field().RateAt(new Point2D(5, -1)), Is.EqualTo(2.0).Within(1e-9));   // e0 中段
            Assert.That(Field().RateAt(new Point2D(11, 5)), Is.EqualTo(12.0).Within(1e-9));  // e1 中段
        }

        [Test]
        public void RateAt_NearSharedVertexWithDifferentRates_BlendsTowardAverage()
        {
            // 角點 (10,0)：e0=2、e1=12，角點值＝平均 7。
            // e0 側距角點 0.01：7 + (2-7)*(0.01/2) ≈ 6.975
            var nearOnBottom = Field().RateAt(new Point2D(9.99, -0.001));
            Assert.That(nearOnBottom, Is.EqualTo(7.0 + ((2.0 - 7.0) * (0.01 / 2.0))).Within(1e-3));

            // e1 側距角點 0.01：7 + (12-7)*(0.01/2) ≈ 7.025
            var nearOnRight = Field().RateAt(new Point2D(10.001, 0.01));
            Assert.That(nearOnRight, Is.EqualTo(7.0 + ((12.0 - 7.0) * (0.01 / 2.0))).Within(1e-3));
        }

        [Test]
        public void RateAt_ExactlyAtVertex_ReturnsAverage_RegardlessOfEdgeTieBreak()
        {
            // 角外側取樣，最近點＝角點本身。
            Assert.That(Field().RateAt(new Point2D(10.5, -0.5)), Is.EqualTo(7.0).Within(1e-9));
        }

        [Test]
        public void RateAt_CornerWhereNeighborsShareRate_NoBlend()
        {
            // 角點 (0,10)：e2=2、e3=2 → 不混、恆為 2。
            Assert.That(Field().RateAt(new Point2D(-0.5, 10.5)), Is.EqualTo(2.0).Within(1e-9));
            Assert.That(Field().RateAt(new Point2D(0.01, 10.5)), Is.EqualTo(2.0).Within(1e-9));
        }

        [Test]
        public void RateAt_BeyondBlendLength_ReturnsPureEdgeRate()
        {
            // e1 上距角點 (10,0) 沿邊 3 > blend 2 → 純 12。
            Assert.That(Field().RateAt(new Point2D(10.5, 3)), Is.EqualTo(12.0).Within(1e-9));
        }

        [Test]
        public void RateAt_UniformRates_AlwaysUniform()
        {
            var field = new BoundaryRateField(Square(), new[] { 2.0, 2.0, 2.0, 2.0 }, 2.0);
            Assert.That(field.RateAt(new Point2D(10.5, 0.01)), Is.EqualTo(2.0).Within(1e-12));
            Assert.That(field.RateAt(new Point2D(-3, -3)), Is.EqualTo(2.0).Within(1e-12));
        }

        [Test]
        public void NearestBoundaryPointDetailed_MidEdge_ReturnsEdgeIndexAndT()
        {
            var detailed = Polygon2D.NearestBoundaryPointDetailed(Square(), new Point2D(5, -2));
            Assert.That(detailed.Point.X, Is.EqualTo(5).Within(1e-9));
            Assert.That(detailed.Point.Y, Is.EqualTo(0).Within(1e-9));
            Assert.That(detailed.Distance, Is.EqualTo(2).Within(1e-9));
            Assert.That(detailed.EdgeIndex, Is.EqualTo(0));
            Assert.That(detailed.T, Is.EqualTo(0.5).Within(1e-9));
        }

        [Test]
        public void NearestBoundaryPointDetailed_NearVertex_ReturnsVertexPoint()
        {
            var detailed = Polygon2D.NearestBoundaryPointDetailed(Square(), new Point2D(12, 12));
            Assert.That(detailed.Point.X, Is.EqualTo(10).Within(1e-9));
            Assert.That(detailed.Point.Y, Is.EqualTo(10).Within(1e-9));
            Assert.That(detailed.EdgeIndex, Is.EqualTo(1).Or.EqualTo(2)); // 共點兩邊擇一
        }
    }
}
