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
        public int SchemaVersion { get; set; } = 6;
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

        /// <summary>鬆實方三本帳（v2 新增）；未提供係數時為 null。</summary>
        public EarthworkLedger Ledger { get; set; }

        /// <summary>方案鎖定 3D 視圖的元素 ID（v3 新增；create_grading_scheme_view 回寫）。</summary>
        public long? SchemeViewId { get; set; }

        /// <summary>方案截圖檔路徑（v3 新增；create_grading_scheme_view 回寫）。</summary>
        public string ScreenshotPath { get; set; }

        /// <summary>本次生效的邊坡率政策快照（v4 新增；V3-S2 逐邊坡率，可重現/可比較）。</summary>
        public IReadOnlyList<FloorEdgePolicySnapshot> EdgePolicies { get; set; }

        /// <summary>
        /// 方案建立當下的控制樓板幾何快照（v5 新增）。輪廓以公尺、專案內部原點座標儲存，
        /// 讓舊方案比較圖不受後續編輯同一片樓板的影響。
        /// </summary>
        public IReadOnlyList<FloorGeometrySnapshot> FloorSnapshots { get; set; }

        /// <summary>「產生分析圖」最後一次執行時間戳（v6 新增）；null＝尚未產生過分析圖。方案總覽的完成度狀態依此欄判斷。</summary>
        public string AnalysisGeneratedAt { get; set; }

        /// <summary>「產生分析圖」建立的挖填熱區視圖 ID（v6 新增）。</summary>
        public long? AnalysisCutfillViewId { get; set; }

        /// <summary>「產生分析圖」建立的坡度熱區視圖 ID（v6 新增）。</summary>
        public long? AnalysisSlopeViewId { get; set; }

        /// <summary>「產生分析圖」建立的方案專屬正剖面視圖 ID（v6 新增）。</summary>
        public long? AnalysisSectionViewId { get; set; }

        /// <summary>「產生分析圖」建立的方案專屬剖面立體視圖 ID（v6 新增）。</summary>
        public long? AnalysisSectionIsoViewId { get; set; }
    }

    /// <summary>方案建立當下的單一控制樓板幾何快照（登記簿用；公尺）。</summary>
    public sealed class FloorGeometrySnapshot
    {
        public long FloorId { get; set; }
        public IReadOnlyList<FloorSnapshotPoint> OuterLoopMeters { get; set; }
        public double BottomZMinMeters { get; set; }
        public double BottomZMaxMeters { get; set; }
    }

    /// <summary>樓板輪廓快照點（公尺，專案內部原點座標）。</summary>
    public sealed class FloorSnapshotPoint
    {
        public double XMeters { get; set; }
        public double YMeters { get; set; }
    }

    /// <summary>比較圖共同聚焦範圍（公尺，專案內部原點座標）。</summary>
    public readonly struct GradingComparisonFocusBounds
    {
        private GradingComparisonFocusBounds(
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ)
        {
            MinXMeters = minX;
            MinYMeters = minY;
            MinZMeters = minZ;
            MaxXMeters = maxX;
            MaxYMeters = maxY;
            MaxZMeters = maxZ;
        }

        public double MinXMeters { get; }
        public double MinYMeters { get; }
        public double MinZMeters { get; }
        public double MaxXMeters { get; }
        public double MaxYMeters { get; }
        public double MaxZMeters { get; }
        public double CenterXMeters => (MinXMeters + MaxXMeters) / 2.0;
        public double CenterYMeters => (MinYMeters + MaxYMeters) / 2.0;
        public double CenterZMeters => (MinZMeters + MaxZMeters) / 2.0;
        public double WidthMeters => MaxXMeters - MinXMeters;
        public double DepthMeters => MaxYMeters - MinYMeters;
        public double HeightMeters => MaxZMeters - MinZMeters;

        public static GradingComparisonFocusBounds FromFloorSnapshots(
            IEnumerable<FloorGeometrySnapshot> snapshots,
            double horizontalPaddingMeters,
            double minimumPlanSpanMeters,
            double verticalPaddingMeters,
            double minimumHeightMeters)
        {
            if (snapshots == null)
            {
                throw new ArgumentNullException(nameof(snapshots));
            }

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var minZ = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var maxZ = double.MinValue;
            var pointCount = 0;
            foreach (var snapshot in snapshots)
            {
                if (snapshot?.OuterLoopMeters == null)
                {
                    continue;
                }

                minZ = Math.Min(minZ, snapshot.BottomZMinMeters);
                maxZ = Math.Max(maxZ, snapshot.BottomZMaxMeters);
                foreach (var point in snapshot.OuterLoopMeters)
                {
                    if (point == null)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, point.XMeters);
                    minY = Math.Min(minY, point.YMeters);
                    maxX = Math.Max(maxX, point.XMeters);
                    maxY = Math.Max(maxY, point.YMeters);
                    pointCount++;
                }
            }

            if (pointCount == 0)
            {
                throw new ArgumentException("至少需要一個樓板輪廓快照點。", nameof(snapshots));
            }

            minX -= horizontalPaddingMeters;
            minY -= horizontalPaddingMeters;
            maxX += horizontalPaddingMeters;
            maxY += horizontalPaddingMeters;
            minZ -= verticalPaddingMeters;
            maxZ += verticalPaddingMeters;
            ExpandSpan(ref minX, ref maxX, minimumPlanSpanMeters);
            ExpandSpan(ref minY, ref maxY, minimumPlanSpanMeters);
            ExpandSpan(ref minZ, ref maxZ, minimumHeightMeters);
            return new GradingComparisonFocusBounds(minX, minY, minZ, maxX, maxY, maxZ);
        }

        private static void ExpandSpan(ref double min, ref double max, double minimumSpan)
        {
            if (minimumSpan <= 0 || max - min >= minimumSpan)
            {
                return;
            }

            var center = (min + max) / 2.0;
            var half = minimumSpan / 2.0;
            min = center - half;
            max = center + half;
        }
    }

    /// <summary>單一樓板的邊坡率政策快照（登記簿用；公尺）。</summary>
    public sealed class FloorEdgePolicySnapshot
    {
        public long FloorId { get; set; }
        public IReadOnlyList<EdgePolicySnapshot> Policies { get; set; }
    }

    /// <summary>單條邊政策快照：坡率與邊長（公尺）。</summary>
    public sealed class EdgePolicySnapshot
    {
        public string SlopeRatio { get; set; }
        public double LengthMeters { get; set; }
    }

    /// <summary>單一控制樓板的登記指標。</summary>
    public sealed class FloorMetric
    {
        public long FloorId { get; set; }
        public double AreaSquareMeters { get; set; }
        public double BottomZMinMeters { get; set; }
        public double BottomZMaxMeters { get; set; }

        /// <summary>樓板「自標高偏移」（公尺，v3 新增）；方案還原的寫回依據。</summary>
        public double? HeightOffsetMeters { get; set; }
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
