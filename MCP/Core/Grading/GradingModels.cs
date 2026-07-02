using System;
using System.Collections.Generic;
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

        public void Validate()
        {
            if (ToposolidId <= 0) throw new ArgumentException("地形 ID 必須大於 0。");
            if (FloorIds == null || FloorIds.Count == 0) throw new ArgumentException("至少一片樓板才能執行整地。");
            if (FloorIds.Any(id => id <= 0)) throw new ArgumentException("樓板 ID 必須大於 0。");
            if (!string.Equals(Mode, "footprint_only", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("本次試跑僅支援 footprint_only。");
            if (!string.Equals(TargetFace, "bottom", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("本次試跑僅支援樓板底面 bottom。");
            if (UpdateExisting)
                throw new ArgumentException("本次試跑尚未支援 updateExisting=true。");
        }
    }

    public sealed class FloorFootprint
    {
        public long FloorId { get; set; }
        public IReadOnlyList<Point2D> OuterLoop { get; set; }
        public Func<double, double, double> BottomElevationAt { get; set; }
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
