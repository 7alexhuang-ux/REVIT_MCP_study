using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 方案登記簿的一筆記錄。序列化為 JSON 存入設計地形的 Extensible Storage；
    /// 結構演進以 SchemaVersion 表達，ES schema 本身只有 AssociationId 與 SchemeJson 兩欄。
    /// 數值一律使用者單位：公尺、平方公尺、立方公尺。
    /// </summary>
    public sealed class GradingSchemeRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public string AssociationId { get; set; }
        public string SchemeName { get; set; }
        public string Timestamp { get; set; }
        public string DocumentTitle { get; set; }
        public string Mode { get; set; }
        public double? OffsetDistanceMeters { get; set; }
        public string SlopeRatio { get; set; }
        public double? MaxExtensionMeters { get; set; }
        public long OriginalToposolidId { get; set; }
        public long DesignToposolidId { get; set; }
        public IReadOnlyList<long> FloorIds { get; set; }
        public double CutCubicMeters { get; set; }
        public double FillCubicMeters { get; set; }
        public double NetCubicMeters { get; set; }
        public double MaxCutDepthMeters { get; set; }
        public double MaxFillHeightMeters { get; set; }
        public double DisturbedAreaSquareMeters { get; set; }
        public bool DisturbedAreaIsApproximate { get; set; }
        public IReadOnlyList<FloorMetric> FloorMetrics { get; set; }
        public IReadOnlyList<string> Warnings { get; set; }
        public string ElevationBasis { get; set; }
    }

    /// <summary>單一控制樓板的登記指標。</summary>
    public sealed class FloorMetric
    {
        public long FloorId { get; set; }
        public double AreaSquareMeters { get; set; }
        public double BottomZMinMeters { get; set; }
        public double BottomZMaxMeters { get; set; }
    }

    /// <summary>方案幾何指標的純計算。</summary>
    public static class SchemeMetrics
    {
        /// <summary>最大挖深：原地形高於目標面的最大差值；無挖方回 0。</summary>
        public static double MaxCutDepth(IEnumerable<(double OriginalZ, double TargetZ)> pairs)
        {
            if (pairs == null) throw new ArgumentNullException(nameof(pairs));
            var max = 0.0;
            foreach (var (originalZ, targetZ) in pairs)
            {
                var depth = originalZ - targetZ;
                if (depth > max) max = depth;
            }

            return max;
        }

        /// <summary>最大填高：目標面高於原地形的最大差值；無填方回 0。</summary>
        public static double MaxFillHeight(IEnumerable<(double OriginalZ, double TargetZ)> pairs)
        {
            if (pairs == null) throw new ArgumentNullException(nameof(pairs));
            var max = 0.0;
            foreach (var (originalZ, targetZ) in pairs)
            {
                var height = targetZ - originalZ;
                if (height > max) max = height;
            }

            return max;
        }
    }
}
