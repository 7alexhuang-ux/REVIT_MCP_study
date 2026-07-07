using System.Collections.Generic;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class EdgeSlopePoliciesTests
    {
        // 10×10 正方形（CCW）：e0 底、e1 右、e2 頂、e3 左。
        private static List<Point2D> Square() => new List<Point2D>
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
        };

        [Test]
        public void Match_PolicyOnBottomEdge_AssignsRateToEdge0Only()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, 0, 1, 0, 10, 12.0)
            };
            var (rates, unmatched) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(rates, Is.EqualTo(new[] { 12.0, 2.0, 2.0, 2.0 }));
            Assert.That(unmatched, Is.Empty);
        }

        [Test]
        public void Match_ReversedDirection_StillMatches()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, 0, -1, 0, 10, 12.0)
            };
            var (rates, _) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(rates[0], Is.EqualTo(12.0));
        }

        [Test]
        public void Match_MidpointSlightlyOff_WithinTolerance_Matches()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, -0.5, 1, 0, 10, 12.0)
            };
            var (rates, unmatched) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(rates[0], Is.EqualTo(12.0));
            Assert.That(unmatched, Is.Empty);
        }

        [Test]
        public void Match_MidpointTooFar_Unmatched()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, 4, 1, 0, 10, 12.0) // 距最近邊 4 > 容差上限 3.28（≈1m）
            };
            var (rates, unmatched) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(rates, Is.EqualTo(new[] { 2.0, 2.0, 2.0, 2.0 }));
            Assert.That(unmatched, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void Match_LengthMismatch_Unmatched()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, 0, 1, 0, 5, 12.0) // 邊長 10、政策記 5 → 差 50% > 20%
            };
            var (_, unmatched) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(unmatched, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void Match_TwoPoliciesOnDifferentEdges_BothMatch()
        {
            var policies = new List<EdgeSlopePolicy>
            {
                new EdgeSlopePolicy(5, 0, 1, 0, 10, 12.0),   // e0 底
                new EdgeSlopePolicy(10, 5, 0, 1, 10, 15.0)   // e1 右
            };
            var (rates, unmatched) = EdgeSlopePolicies.MatchEdgeRates(Square(), policies, 2.0);
            Assert.That(rates, Is.EqualTo(new[] { 12.0, 15.0, 2.0, 2.0 }));
            Assert.That(unmatched, Is.Empty);
        }

        [Test]
        public void Fingerprint_FromSegment_MidDirLength()
        {
            var fp = EdgeSlopePolicies.FingerprintFromSegment(
                new Point2D(0, 0), new Point2D(10, 0), 12.0);
            Assert.That(fp.MidX, Is.EqualTo(5).Within(1e-9));
            Assert.That(fp.MidY, Is.EqualTo(0).Within(1e-9));
            Assert.That(fp.Length, Is.EqualTo(10).Within(1e-9));
            Assert.That(fp.RunPerRise, Is.EqualTo(12.0));
        }
    }
}
