using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RevitMCP.Core.Grading
{
    public struct Point2D
    {
        public Point2D(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
    }

    public sealed class GradingRequest
    {
        public long ToposolidId { get; set; }
        public IReadOnlyList<long> FloorIds { get; set; }
        public string Mode { get; set; }
        public string TargetFace { get; set; }
        public bool AllowPhaseSetup { get; set; }
        public bool UpdateExisting { get; set; }
        public double? OffsetDistanceMeters { get; set; }
        public string SlopeRatio { get; set; }
        public double? MaxExtensionMeters { get; set; }
        public double? LooseFactor { get; set; }
        public double? CompactionFactor { get; set; }

        public void Validate()
        {
            if (ToposolidId <= 0) throw new ArgumentException("地形 ID 必須大於 0。");
            if (FloorIds == null || FloorIds.Count == 0) throw new ArgumentException("至少一片樓板才能執行整地。");
            if (FloorIds.Any(id => id <= 0)) throw new ArgumentException("樓板 ID 必須大於 0。");
            if (!string.Equals(TargetFace, "bottom", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("目前僅支援樓板底面 bottom。");
            if (UpdateExisting)
                throw new ArgumentException("目前尚未支援 updateExisting=true。");
            if (LooseFactor.HasValue || CompactionFactor.HasValue)
            {
                if (!(LooseFactor > 0) || !(CompactionFactor > 0))
                {
                    throw new ArgumentException(
                        "鬆實方係數必須成對提供且大於 0（looseFactor 與 compactionFactor）。");
                }
            }

            TransitionSettings.FromRequest(this);
        }
    }

    public enum GradingMode
    {
        FootprintOnly,
        OffsetTransition,
        SlopeTransition
    }

    /// <summary>整地邊界銜接設定；由 GradingRequest 驗證並轉出。</summary>
    public sealed class TransitionSettings
    {
        public const double DefaultMaxExtensionMeters = 20.0;

        private TransitionSettings() { }

        public GradingMode Mode { get; private set; }
        public double OffsetDistanceMeters { get; private set; }
        public double RunPerRise { get; private set; }
        public double MaxExtensionMeters { get; private set; }

        public static TransitionSettings FromRequest(GradingRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var mode = ParseMode(request.Mode);
            switch (mode)
            {
                case GradingMode.FootprintOnly:
                    RejectParameter(request.OffsetDistanceMeters.HasValue, "offsetDistance", "footprint_only");
                    RejectParameter(!string.IsNullOrWhiteSpace(request.SlopeRatio), "slopeRatio", "footprint_only");
                    RejectParameter(request.MaxExtensionMeters.HasValue, "maxExtension", "footprint_only");
                    return new TransitionSettings { Mode = mode };
                case GradingMode.OffsetTransition:
                    if (!(request.OffsetDistanceMeters > 0))
                        throw new ArgumentException("offset_transition 模式必須提供大於 0 的 offsetDistance（公尺）。");
                    RejectParameter(!string.IsNullOrWhiteSpace(request.SlopeRatio), "slopeRatio", "offset_transition");
                    RejectParameter(request.MaxExtensionMeters.HasValue, "maxExtension", "offset_transition");
                    return new TransitionSettings
                    {
                        Mode = mode,
                        OffsetDistanceMeters = request.OffsetDistanceMeters.Value
                    };
                case GradingMode.SlopeTransition:
                    RejectParameter(request.OffsetDistanceMeters.HasValue, "offsetDistance", "slope_transition");
                    var runPerRise = ParseSlopeRatio(request.SlopeRatio);
                    var maxExtension = request.MaxExtensionMeters ?? DefaultMaxExtensionMeters;
                    if (!(maxExtension > 0))
                        throw new ArgumentException("maxExtension 必須大於 0（公尺）。");
                    return new TransitionSettings
                    {
                        Mode = mode,
                        RunPerRise = runPerRise,
                        MaxExtensionMeters = maxExtension
                    };
                default:
                    throw new ArgumentException($"不支援的整地 mode：{request.Mode}。");
            }
        }

        public static double ParseSlopeRatio(string slopeRatio)
        {
            if (string.IsNullOrWhiteSpace(slopeRatio))
                throw new ArgumentException("slope_transition 模式必須提供 slopeRatio，格式 1:n（例如 1:12）。");
            var parts = slopeRatio.Split(':');
            if (parts.Length == 2
                && parts[0].Trim() == "1"
                && double.TryParse(
                    parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var run)
                && run > 0)
            {
                return run;
            }

            throw new ArgumentException("slopeRatio 格式必須為 1:n 且 n 大於 0（例如 1:12）。");
        }

        private static GradingMode ParseMode(string mode)
        {
            if (string.Equals(mode, "footprint_only", StringComparison.OrdinalIgnoreCase))
                return GradingMode.FootprintOnly;
            if (string.Equals(mode, "offset_transition", StringComparison.OrdinalIgnoreCase))
                return GradingMode.OffsetTransition;
            if (string.Equals(mode, "slope_transition", StringComparison.OrdinalIgnoreCase))
                return GradingMode.SlopeTransition;
            throw new ArgumentException(
                $"不支援的整地 mode：{mode}；可用值為 footprint_only、offset_transition、slope_transition。");
        }

        private static void RejectParameter(bool provided, string parameterName, string mode)
        {
            if (provided)
                throw new ArgumentException($"{mode} 模式不接受 {parameterName} 參數。");
        }
    }

    /// <summary>
    /// 樓板的一個「朝下面」投影區域：外框、內部開口（洞）與板底高程函式。
    /// 一片樓板可有多個朝下面（斜板、改過形狀的板、板底有階差）：最大面放在本物件，
    /// 其餘面放在 <see cref="ExtraRegions"/>；整地時以 <see cref="SplitRegions"/> 攤平成一面一個 footprint。
    /// 洞不屬於該樓板——洞內的點不受該板控制；洞邊界上的點仍屬該板（板緣）。
    /// </summary>
    public sealed class FloorFootprint
    {
        public long FloorId { get; set; }
        public IReadOnlyList<Point2D> OuterLoop { get; set; }

        /// <summary>內部開口（洞）的環；null 或空＝無洞。</summary>
        public IReadOnlyList<IReadOnlyList<Point2D>> Holes { get; set; }

        public Func<double, double, double> BottomElevationAt { get; set; }

        /// <summary>板的邊坡率政策（V3-S2；無政策＝null/空，放坡時全邊用預設坡率）。</summary>
        public IReadOnlyList<EdgeSlopePolicy> EdgePolicies { get; set; }

        /// <summary>同一片樓板的其他朝下面（各為單一區域、無 ExtraRegions）；null 或空＝只有一個面。</summary>
        public IReadOnlyList<FloorFootprint> ExtraRegions { get; set; }

        private static readonly IReadOnlyList<IReadOnlyList<Point2D>> NoHoles = new IReadOnlyList<Point2D>[0];

        public IReadOnlyList<IReadOnlyList<Point2D>> HoleLoops => Holes ?? NoHoles;

        /// <summary>外框＋全部洞的環（外框在前）。</summary>
        public IEnumerable<IReadOnlyList<Point2D>> AllLoops()
        {
            yield return OuterLoop;
            foreach (var hole in HoleLoops)
            {
                yield return hole;
            }
        }

        /// <summary>攤平：本面（不含 ExtraRegions）＋其他面，各自是單一區域的 footprint。</summary>
        public IReadOnlyList<FloorFootprint> SplitRegions()
        {
            var result = new List<FloorFootprint>
            {
                new FloorFootprint
                {
                    FloorId = FloorId,
                    OuterLoop = OuterLoop,
                    Holes = Holes,
                    BottomElevationAt = BottomElevationAt,
                    EdgePolicies = EdgePolicies
                }
            };
            if (ExtraRegions != null)
            {
                foreach (var extra in ExtraRegions)
                {
                    result.AddRange(extra.SplitRegions());
                }
            }

            return result;
        }

        /// <summary>點是否落在「本面」的外框內且不在洞的內部（洞邊界與外框邊界都算板上）。不含 ExtraRegions。</summary>
        public bool CoversOwnRegion(Point2D point, double tolerance)
        {
            if (!Polygon2D.Contains(OuterLoop, point, tolerance))
            {
                return false;
            }

            foreach (var hole in HoleLoops)
            {
                if (Polygon2D.Contains(hole, point, tolerance)
                    && Polygon2D.DistanceToBoundary(hole, point) > Math.Abs(tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>點是否被本樓板任一朝下面覆蓋（含 ExtraRegions）。</summary>
        public bool CoversXY(Point2D point, double tolerance)
        {
            if (CoversOwnRegion(point, tolerance))
            {
                return true;
            }

            if (ExtraRegions != null)
            {
                foreach (var extra in ExtraRegions)
                {
                    if (extra.CoversXY(point, tolerance))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>本樓板在該點正下方的最低板底高程（所有覆蓋該點的朝下面取最低）；無面覆蓋則回 false。</summary>
        public bool TryLowestBottomZ(Point2D point, double tolerance, out double z)
        {
            z = 0;
            var found = false;
            if (CoversOwnRegion(point, tolerance) && BottomElevationAt != null)
            {
                var value = BottomElevationAt(point.X, point.Y);
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    // 點在板投影內卻取不到板底：不得靜默略過（會讓該點掉進銜接帶、驗收又跳過，結果錯卻回報成功）。
                    throw new InvalidOperationException(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "樓板 ID {0} 在投影內 ({1:F3}, {2:F3}) 英呎處取不到板底高程，已中止整地。",
                        FloorId, point.X, point.Y));
                }

                z = value;
                found = true;
            }

            if (ExtraRegions != null)
            {
                foreach (var extra in ExtraRegions)
                {
                    if (extra.TryLowestBottomZ(point, tolerance, out var extraZ) && (!found || extraZ < z))
                    {
                        z = extraZ;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>本面最近邊界（外框或洞）；OnHole 表示最近邊界在洞上。不含 ExtraRegions。</summary>
        public (Point2D Point, double Distance, bool OnHole) NearestBoundary(Point2D sample)
        {
            var (bestPoint, bestDistance) = Polygon2D.NearestBoundaryPoint(OuterLoop, sample);
            var onHole = false;
            foreach (var hole in HoleLoops)
            {
                if (hole == null || hole.Count < 2)
                {
                    continue;
                }

                var (holePoint, holeDistance) = Polygon2D.NearestBoundaryPoint(hole, sample);
                if (holeDistance < bestDistance)
                {
                    bestPoint = holePoint;
                    bestDistance = holeDistance;
                    onHole = true;
                }
            }

            return (bestPoint, bestDistance, onHole);
        }

        /// <summary>
        /// 全部朝下面外框（聯集外緣）上離 sample 最近的邊：各面外框的邊，略過兩面共用的內部邊
        /// （邊中點同時落在另一面內）。RegionIndex 對應 <see cref="SplitRegions"/> 順序（0＝主面）；
        /// 找不到（無任何邊）時 RegionIndex＝-1。單一朝下面時等同外框最近邊。
        /// </summary>
        public (int RegionIndex, int EdgeIndex, Point2D Start, Point2D End, double Distance) NearestOutlineEdge(
            Point2D sample,
            double tolerance)
        {
            var regions = SplitRegions();
            var best = (RegionIndex: -1, EdgeIndex: -1, Start: default(Point2D), End: default(Point2D),
                Distance: double.MaxValue);
            for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                var loop = regions[regionIndex].OuterLoop;
                for (var edgeIndex = 0; edgeIndex < loop.Count; edgeIndex++)
                {
                    var start = loop[edgeIndex];
                    var end = loop[(edgeIndex + 1) % loop.Count];
                    if (regions.Count > 1)
                    {
                        var middle = new Point2D((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
                        var shared = false;
                        for (var other = 0; other < regions.Count && !shared; other++)
                        {
                            shared = other != regionIndex && regions[other].CoversOwnRegion(middle, tolerance);
                        }

                        if (shared)
                        {
                            continue;
                        }
                    }

                    var edgeX = end.X - start.X;
                    var edgeY = end.Y - start.Y;
                    var lengthSquared = (edgeX * edgeX) + (edgeY * edgeY);
                    var t = lengthSquared > 0
                        ? Math.Max(0, Math.Min(1, (((sample.X - start.X) * edgeX) + ((sample.Y - start.Y) * edgeY)) / lengthSquared))
                        : 0.0;
                    var dx = sample.X - (start.X + (t * edgeX));
                    var dy = sample.Y - (start.Y + (t * edgeY));
                    var distance = Math.Sqrt((dx * dx) + (dy * dy));
                    if (distance < best.Distance)
                    {
                        best = (regionIndex, edgeIndex, start, end, distance);
                    }
                }
            }

            return best;
        }

        /// <summary>本面面積＝外框面積減洞面積。不含 ExtraRegions。</summary>
        public double OwnRegionArea()
        {
            var area = Polygon2D.Area(OuterLoop);
            foreach (var hole in HoleLoops)
            {
                if (hole != null && hole.Count >= 3)
                {
                    area -= Polygon2D.Area(hole);
                }
            }

            return Math.Max(area, 0);
        }
    }

    /// <summary>多片樓板的覆蓋查詢（純函式）。</summary>
    public static class FootprintCoverage
    {
        /// <summary>
        /// 點正下方所有樓板板底中最低（最深）者；洞內的點不受該板控制。
        /// 多片樓板重疊時取最低板底，不視為衝突。無任何樓板覆蓋則回 false。
        /// </summary>
        public static bool TryLowestBottomZ(
            IEnumerable<FloorFootprint> footprints,
            Point2D point,
            double tolerance,
            out double z)
        {
            z = 0;
            var found = false;
            foreach (var footprint in footprints)
            {
                if (footprint.TryLowestBottomZ(point, tolerance, out var candidate)
                    && (!found || candidate < z))
                {
                    z = candidate;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// 平面（過 origin、法向量 normal）在 (x,y) 處的 Z；斜板、改過形狀的板的各個平面面共用。
        /// 法向量 Z 分量為零（垂直面）無法求解，回 NaN。
        /// </summary>
        public static double PlaneZ(
            double originX, double originY, double originZ,
            double normalX, double normalY, double normalZ,
            double x, double y)
        {
            if (Math.Abs(normalZ) < 1e-12)
            {
                return double.NaN;
            }

            return originZ - (((normalX * (x - originX)) + (normalY * (y - originY))) / normalZ);
        }

        /// <summary>
        /// 同 <see cref="TryLowestBottomZ"/>，另回傳給出最低板底的那個 footprint。
        /// footprints 須已攤平成單一區域（不含 ExtraRegions，見 <see cref="FloorFootprint.SplitRegions"/>）。
        /// </summary>
        public static bool TryLowestBottomRegion(
            IReadOnlyList<FloorFootprint> footprints,
            Point2D point,
            double tolerance,
            out double z,
            out FloorFootprint source)
        {
            z = 0;
            source = null;
            foreach (var footprint in footprints)
            {
                if (footprint.TryLowestBottomZ(point, tolerance, out var candidate)
                    && (source == null || candidate < z))
                {
                    z = candidate;
                    source = footprint;
                }
            }

            return source != null;
        }

        /// <summary>
        /// 階差環：相鄰或重疊的板底不同高時，較高板底的一側在共邊旁需要一圈緊貼的控制點，
        /// 否則地形會從共邊（取較低板底）斜到較高板內下一個頂點，挖出楔形、表面驗收失敗。
        /// 對每個外框／洞緣頂點 v，沿角平分線往兩側各偏 inset，若偏移點 q 的最低板底所屬區域，
        /// 延伸到 v 時比 v 的最低板底高出 elevationTolerance 以上，就在 q 放點（目標高程由一般頂點分類取得，
        /// 即 q 處最低板底）。用「延伸到 v 的同一平面」比較，故單一斜板邊界不會被誤判為階差。
        /// 回傳環（沿該環頂點順序；null＝該頂點不需要），呼叫端把非 null 點加進地形並刻相鄰點間的摺線。
        /// footprints 須已攤平成單一區域。
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<Point2D?>> BuildStepRings(
            IReadOnlyList<FloorFootprint> footprints,
            double inset,
            double xyTolerance,
            double elevationTolerance)
        {
            var rings = new List<IReadOnlyList<Point2D?>>();
            foreach (var footprint in footprints)
            {
                foreach (var loop in footprint.AllLoops())
                {
                    if (loop == null || loop.Count < 3)
                    {
                        continue;
                    }

                    var directions = Polygon2D.OutwardDirections(loop);
                    foreach (var side in new[] { 1.0, -1.0 })
                    {
                        var ring = new Point2D?[loop.Count];
                        var any = false;
                        for (var index = 0; index < loop.Count; index++)
                        {
                            var vertex = loop[index];
                            if (!TryLowestBottomRegion(footprints, vertex, xyTolerance, out var vertexZ, out _))
                            {
                                continue;
                            }

                            var q = new Point2D(
                                vertex.X + (side * inset * directions[index].X),
                                vertex.Y + (side * inset * directions[index].Y));
                            if (!TryLowestBottomRegion(footprints, q, xyTolerance, out _, out var source))
                            {
                                continue;
                            }

                            var extended = source.BottomElevationAt(vertex.X, vertex.Y);
                            if (double.IsNaN(extended) || extended - vertexZ <= elevationTolerance)
                            {
                                continue;
                            }

                            ring[index] = q;
                            any = true;
                        }

                        if (any)
                        {
                            rings.Add(ring);
                        }
                    }
                }
            }

            return rings;
        }

        public static bool AnyCovers(IEnumerable<FloorFootprint> footprints, Point2D point, double tolerance)
        {
            foreach (var footprint in footprints)
            {
                if (footprint.CoversXY(point, tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 兩個單一區域是否「投影重疊且板底高程不同」（以 a 的外框頂點取樣 b 的覆蓋，反向亦同）。
        /// 僅供自動納入鄰接樓板時判斷要不要納入；整地主流程重疊一律取最低板底，不再用此判斷報錯。
        /// </summary>
        public static bool HasElevationConflict(
            FloorFootprint first,
            FloorFootprint second,
            double xyTolerance,
            double elevationTolerance)
        {
            if (!Polygon2D.Overlaps(first.OuterLoop, second.OuterLoop, xyTolerance))
            {
                return false;
            }

            return SamplesConflict(first, second, xyTolerance, elevationTolerance)
                || SamplesConflict(second, first, xyTolerance, elevationTolerance);
        }

        private static bool SamplesConflict(
            FloorFootprint sampled,
            FloorFootprint other,
            double xyTolerance,
            double elevationTolerance)
        {
            foreach (var point in sampled.OuterLoop)
            {
                if (!other.CoversOwnRegion(point, xyTolerance))
                {
                    continue;
                }

                if (Math.Abs(sampled.BottomElevationAt(point.X, point.Y)
                    - other.BottomElevationAt(point.X, point.Y)) > elevationTolerance)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>整地執行的單一階段耗時。</summary>
    public sealed class GradingStage
    {
        public GradingStage(string name, long elapsedMilliseconds)
        {
            Name = name;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public string Name { get; }
        public long ElapsedMilliseconds { get; }
    }

    /// <summary>整地執行的分段計時，供效能記錄與逐步優化使用。</summary>
    public sealed class GradingTimeline
    {
        private readonly List<GradingStage> _stages = new List<GradingStage>();

        public IReadOnlyList<GradingStage> Stages => _stages;

        public IDisposable Measure(string name)
        {
            return new StageScope(this, name);
        }

        public void Record(string name, long elapsedMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("階段名稱不可空白。", nameof(name));
            }

            _stages.Add(new GradingStage(name, elapsedMilliseconds < 0 ? 0 : elapsedMilliseconds));
        }

        private sealed class StageScope : IDisposable
        {
            private readonly GradingTimeline _timeline;
            private readonly string _name;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private bool _disposed;

            public StageScope(GradingTimeline timeline, string name)
            {
                _timeline = timeline;
                _name = name;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timeline.Record(_name, _stopwatch.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>ApplyGrading 的執行結果指標（Revit 內部單位英呎，由命令層轉換）。</summary>
    public sealed class GradingOutcome
    {
        public int ModifiedPointCount { get; set; }
        public double MaxCutDepthFeet { get; set; }
        public double MaxFillHeightFeet { get; set; }
        public double DisturbedAreaSquareFeet { get; set; }
        public bool DisturbedAreaIsApproximate { get; set; }
    }

    public sealed class GradingResult
    {
        public long OriginalToposolidId { get; set; }
        public long DesignToposolidId { get; set; }
        public IReadOnlyList<long> FloorIds { get; set; }
        public double CutCubicMeters { get; set; }
        public double FillCubicMeters { get; set; }
        public double NetCubicMeters => FillCubicMeters - CutCubicMeters;
        public int ModifiedPointCount { get; set; }
        public string AssociationId { get; set; }
        public IReadOnlyList<string> Warnings { get; set; }
    }
}
