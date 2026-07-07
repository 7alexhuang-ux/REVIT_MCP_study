using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 邊界坡率場（純幾何）：任一取樣點 → 最近邊界位置 → 該處生效的放坡坡率（RunPerRise）。
    /// 逐邊坡率不同時，於角點附近沿邊弧長混坡：角點值＝兩鄰邊平均，
    /// 距角點 blendLength 內自角點值線性回到本邊值——確保坡率場沿邊界連續、放坡面不裂。
    /// 各邊坡率一致時恆回傳該值（與單一坡率行為完全一致）。
    /// </summary>
    public sealed class BoundaryRateField
    {
        private readonly IReadOnlyList<Point2D> _polygon;
        private readonly IReadOnlyList<double> _edgeRates;
        private readonly double _blendLength;
        private readonly double[] _edgeLengths;

        public BoundaryRateField(
            IReadOnlyList<Point2D> polygon,
            IReadOnlyList<double> edgeRates,
            double blendLength)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            if (edgeRates == null || edgeRates.Count != polygon.Count)
            {
                throw new ArgumentException("edgeRates 數量必須等於多邊形邊數。", nameof(edgeRates));
            }

            if (blendLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blendLength), "混坡長度必須大於 0。");
            }

            _polygon = polygon;
            _edgeRates = edgeRates;
            _blendLength = blendLength;
            _edgeLengths = new double[polygon.Count];
            for (var i = 0; i < polygon.Count; i++)
            {
                var start = polygon[i];
                var end = polygon[(i + 1) % polygon.Count];
                _edgeLengths[i] = Math.Sqrt(
                    ((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
            }
        }

        /// <summary>取樣點（任意位置）處生效的坡率：最近邊坡率＋角點混坡。</summary>
        public double RateAt(Point2D sample)
        {
            var (_, _, edgeIndex, t) = Polygon2D.NearestBoundaryPointDetailed(_polygon, sample);
            return RateAtEdgePosition(edgeIndex, t);
        }

        /// <summary>邊 index、參數 t 處生效的坡率（含角點混坡）。</summary>
        public double RateAtEdgePosition(int edgeIndex, double t)
        {
            var count = _polygon.Count;
            var rate = _edgeRates[edgeIndex];
            var edgeLength = _edgeLengths[edgeIndex];
            if (edgeLength <= 1e-12)
            {
                return rate;
            }

            var previousRate = _edgeRates[(edgeIndex - 1 + count) % count];
            var nextRate = _edgeRates[(edgeIndex + 1) % count];
            var arcFromStart = t * edgeLength;
            var arcFromEnd = (1 - t) * edgeLength;
            var blendZone = Math.Min(_blendLength, edgeLength / 2.0);

            if (arcFromStart < blendZone && Math.Abs(previousRate - rate) > 1e-12)
            {
                var vertexAverage = (previousRate + rate) / 2.0;
                return vertexAverage + ((rate - vertexAverage) * (arcFromStart / blendZone));
            }

            if (arcFromEnd < blendZone && Math.Abs(nextRate - rate) > 1e-12)
            {
                var vertexAverage = (nextRate + rate) / 2.0;
                return vertexAverage + ((rate - vertexAverage) * (arcFromEnd / blendZone));
            }

            return rate;
        }
    }
}
