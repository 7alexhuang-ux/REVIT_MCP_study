using System;
using System.Collections.Generic;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class SlopedPlaneMathTests
    {
        private static List<Point2D> Square() => new List<Point2D>
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
        };

        [Test]
        public void VertexRises_LowEdgeAtBottom_RisesByPerpendicularDistanceOverN()
        {
            var rises = SlopedPlaneMath.VertexRises(
                Square(), new Point2D(0, 0), new Point2D(10, 0), 12.0);
            Assert.That(rises[0], Is.EqualTo(0).Within(1e-9));
            Assert.That(rises[1], Is.EqualTo(0).Within(1e-9));
            Assert.That(rises[2], Is.EqualTo(10.0 / 12.0).Within(1e-9));
            Assert.That(rises[3], Is.EqualTo(10.0 / 12.0).Within(1e-9));
        }

        [Test]
        public void VertexRises_InteriorSideAutoDetected_WhenEdgeReversed()
        {
            // 低側邊方向顛倒不影響結果（內側由頂點群自動判定）。
            var rises = SlopedPlaneMath.VertexRises(
                Square(), new Point2D(10, 0), new Point2D(0, 0), 12.0);
            Assert.That(rises[2], Is.EqualTo(10.0 / 12.0).Within(1e-9));
        }

        [Test]
        public void VertexRises_VertexSlightlyOutside_ClampsToZero()
        {
            var vertices = new List<Point2D>
            {
                new Point2D(0, -0.5), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
            };
            var rises = SlopedPlaneMath.VertexRises(
                vertices, new Point2D(0, 0), new Point2D(10, 0), 12.0);
            Assert.That(rises[0], Is.EqualTo(0).Within(1e-9)); // 低側邊外側 → 0，不下切
        }

        [Test]
        public void VertexRises_InvalidInputs_Throw()
        {
            Assert.Throws<ArgumentException>(() => SlopedPlaneMath.VertexRises(
                Square(), new Point2D(3, 3), new Point2D(3, 3), 12.0)); // 零長度邊
            Assert.Throws<ArgumentOutOfRangeException>(() => SlopedPlaneMath.VertexRises(
                Square(), new Point2D(0, 0), new Point2D(10, 0), 0)); // n<=0
        }
    }
}
