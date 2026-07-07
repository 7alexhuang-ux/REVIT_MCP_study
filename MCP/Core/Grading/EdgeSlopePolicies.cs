using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 邊坡率政策：一條樓板邊界邊的幾何指紋＋放坡坡率。
    /// 長度單位由呼叫端統一（Revit 內部英呎）；方向為單位向量、正反視為同一條邊。
    /// </summary>
    public readonly struct EdgeSlopePolicy
    {
        public EdgeSlopePolicy(double midX, double midY, double dirX, double dirY, double length, double runPerRise)
        {
            MidX = midX; MidY = midY; DirX = dirX; DirY = dirY;
            Length = length; RunPerRise = runPerRise;
        }

        public double MidX { get; }
        public double MidY { get; }
        public double DirX { get; }
        public double DirY { get; }
        public double Length { get; }
        public double RunPerRise { get; }
    }

    /// <summary>
    /// 邊坡率政策的純幾何：指紋生成與「政策 → 多邊形邊」匹配。
    /// 匹配容差：中點距 ≤ min(邊長/2, MidToleranceCap)、方向 |dot| ≥ cos(10°)、長度差 ≤ 20%。
    /// </summary>
    public static class EdgeSlopePolicies
    {
        /// <summary>中點距離容差上限（與長度同單位；呼叫端用英呎時＝約 1 公尺）。</summary>
        public const double MidToleranceCap = 3.28;

        private const double DirectionDotMin = 0.9848; // cos(10°)
        private const double LengthRatioTolerance = 0.2;

        public static EdgeSlopePolicy FingerprintFromSegment(Point2D start, Point2D end, double runPerRise)
        {
            var length = Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
            if (length <= 1e-9)
            {
                throw new ArgumentException("邊長為零，無法生成指紋。");
            }

            return new EdgeSlopePolicy(
                (start.X + end.X) / 2.0,
                (start.Y + end.Y) / 2.0,
                (end.X - start.X) / length,
                (end.Y - start.Y) / length,
                length,
                runPerRise);
        }

        /// <summary>
        /// 把政策匹配到多邊形的邊：回傳逐邊坡率（無政策的邊＝預設坡率）與配不到的政策 index 清單。
        /// </summary>
        public static (IReadOnlyList<double> EdgeRates, IReadOnlyList<int> UnmatchedPolicyIndices) MatchEdgeRates(
            IReadOnlyList<Point2D> polygon,
            IReadOnlyList<EdgeSlopePolicy> policies,
            double defaultRunPerRise)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            var rates = new double[polygon.Count];
            for (var i = 0; i < rates.Length; i++) { rates[i] = defaultRunPerRise; }
            var unmatched = new List<int>();
            if (policies == null || policies.Count == 0)
            {
                return (rates, unmatched);
            }

            for (var policyIndex = 0; policyIndex < policies.Count; policyIndex++)
            {
                var policy = policies[policyIndex];
                var bestEdge = -1;
                var bestMidDistance = double.MaxValue;
                for (var edgeIndex = 0; edgeIndex < polygon.Count; edgeIndex++)
                {
                    var start = polygon[edgeIndex];
                    var end = polygon[(edgeIndex + 1) % polygon.Count];
                    var edgeLength = Math.Sqrt(
                        ((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
                    if (edgeLength <= 1e-9)
                    {
                        continue;
                    }

                    // 長度差
                    var lengthRatio = Math.Abs(edgeLength - policy.Length)
                        / Math.Max(edgeLength, policy.Length);
                    if (lengthRatio > LengthRatioTolerance)
                    {
                        continue;
                    }

                    // 方向平行（正反皆可）
                    var dirX = (end.X - start.X) / edgeLength;
                    var dirY = (end.Y - start.Y) / edgeLength;
                    var dot = Math.Abs((dirX * policy.DirX) + (dirY * policy.DirY));
                    if (dot < DirectionDotMin)
                    {
                        continue;
                    }

                    // 中點距離
                    var midX = (start.X + end.X) / 2.0;
                    var midY = (start.Y + end.Y) / 2.0;
                    var midDistance = Math.Sqrt(
                        ((midX - policy.MidX) * (midX - policy.MidX))
                        + ((midY - policy.MidY) * (midY - policy.MidY)));
                    var tolerance = Math.Min(edgeLength / 2.0, MidToleranceCap);
                    if (midDistance > tolerance)
                    {
                        continue;
                    }

                    if (midDistance < bestMidDistance)
                    {
                        bestMidDistance = midDistance;
                        bestEdge = edgeIndex;
                    }
                }

                if (bestEdge >= 0)
                {
                    rates[bestEdge] = policy.RunPerRise;
                }
                else
                {
                    unmatched.Add(policyIndex);
                }
            }

            return (rates, unmatched);
        }
    }
}
