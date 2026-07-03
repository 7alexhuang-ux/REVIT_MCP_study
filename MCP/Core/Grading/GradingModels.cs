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

        public void Validate()
        {
            if (ToposolidId <= 0) throw new ArgumentException("地形 ID 必須大於 0。");
            if (FloorIds == null || FloorIds.Count == 0) throw new ArgumentException("至少一片樓板才能執行整地。");
            if (FloorIds.Any(id => id <= 0)) throw new ArgumentException("樓板 ID 必須大於 0。");
            if (!string.Equals(TargetFace, "bottom", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("目前僅支援樓板底面 bottom。");
            if (UpdateExisting)
                throw new ArgumentException("目前尚未支援 updateExisting=true。");
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

    public sealed class FloorFootprint
    {
        public long FloorId { get; set; }
        public IReadOnlyList<Point2D> OuterLoop { get; set; }
        public Func<double, double, double> BottomElevationAt { get; set; }
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
