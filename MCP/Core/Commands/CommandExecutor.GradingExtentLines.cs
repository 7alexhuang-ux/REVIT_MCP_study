#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 整地範圍線：在方案視圖建模型線環標示「動過地的範圍」。
        /// footprint 只畫樓板投影邊界（即擾動邊）；offset/slope 另畫外圈放坡外緣（坡腳線，
        /// 圓角，與 5A 控制環一致的距離場坡腳）。線繪於水平面呈現平面擾動範圍；面積以登記簿為準。
        /// </summary>
        private object CreateGradingExtentLines(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var view = ResolveSchemeView(doc, parameters, record);
            var mode = record.Value<string>("Mode") ?? "footprint_only";

            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var floors = adapter.ValidateFloors(doc, floorIds);
            var footprints = adapter.ExtractBottomFootprints(floors);
            if (footprints.Count == 0)
            {
                throw new InvalidOperationException("方案沒有可用的控制樓板投影，無法建立範圍線。");
            }

            // 線環繪於水平面（設計地形頂 + 200mm），僅取 XY 呈現平面擾動範圍。
            var designBox = design.get_BoundingBox(null);
            var planeZ = (designBox?.Max.Z ?? 0)
                + UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);

            var xyTolerance = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var ringMaxChord = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            var arcStep = Math.PI / 12.0;

            var offsetFeet = mode == "offset_transition"
                ? UnitUtils.ConvertToInternalUnits(
                    record.Value<double?>("OffsetDistanceMeters") ?? 0, UnitTypeId.Meters)
                : 0.0;
            var maxExtensionFeet = mode == "slope_transition"
                ? UnitUtils.ConvertToInternalUnits(
                    record.Value<double?>("MaxExtensionMeters") ?? 20, UnitTypeId.Meters)
                : 0.0;
            var runPerRise = ParseRunPerRise(record.Value<string>("SlopeRatio"));

            // slope 模式的距離場坡腳需取樣原地形。
            IReadOnlyList<Solid> originalSolids = new List<Solid>();
            var rayBottomZ = 0.0;
            var rayTopZ = 0.0;
            if (mode == "slope_transition")
            {
                var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
                if (doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original)
                {
                    originalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
                    var originalBox = original.get_BoundingBox(null);
                    if (originalBox != null)
                    {
                        rayBottomZ = originalBox.Min.Z - 10.0;
                        rayTopZ = originalBox.Max.Z + 10.0;
                    }
                }
            }

            var innerLoops = new List<List<XYZ>>();
            var outerLoops = new List<List<XYZ>>();
            foreach (var footprint in footprints)
            {
                innerLoops.Add(footprint.OuterLoop.Select(p => new XYZ(p.X, p.Y, planeZ)).ToList());
                if (mode == "footprint_only")
                {
                    continue;
                }

                var anchors = Polygon2D.DensifiedBoundaryWithCornerArcs(footprint.OuterLoop, ringMaxChord, arcStep);
                var outer = new List<XYZ>();
                foreach (var (basePoint, direction) in anchors)
                {
                    double extension;
                    if (mode == "offset_transition")
                    {
                        extension = offsetFeet;
                    }
                    else
                    {
                        var bottomZ = footprint.BottomElevationAt(basePoint.X, basePoint.Y);
                        extension = maxExtensionFeet;
                        for (var iteration = 0; iteration < 8; iteration++)
                        {
                            var probe = new Point2D(
                                basePoint.X + (direction.X * extension),
                                basePoint.Y + (direction.Y * extension));
                            var probeTerrainZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                                originalSolids, probe, rayBottomZ, rayTopZ);
                            if (!probeTerrainZ.HasValue)
                            {
                                break;
                            }

                            var needed = TransitionGeometry.SlopeExtensionNeeded(
                                bottomZ, probeTerrainZ.Value, runPerRise);
                            extension = Math.Min(needed, maxExtensionFeet);
                        }

                        if (extension < xyTolerance)
                        {
                            continue;
                        }
                    }

                    var x = basePoint.X + (direction.X * extension);
                    var y = basePoint.Y + (direction.Y * extension);
                    if (footprints.Any(other => Polygon2D.Contains(other.OuterLoop, new Point2D(x, y), xyTolerance)))
                    {
                        continue;
                    }

                    outer.Add(new XYZ(x, y, planeZ));
                }

                if (outer.Count >= 2)
                {
                    outerLoops.Add(outer);
                }
            }

            int innerCurves;
            int outerCurves;
            using (var transaction = new Transaction(doc, "建立整地範圍線"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立整地範圍線交易。");
                }

                var sketchPlane = SketchPlane.Create(
                    doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, planeZ)));
                innerCurves = DrawModelLoops(doc, sketchPlane, innerLoops);
                outerCurves = DrawModelLoops(doc, sketchPlane, outerLoops);

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("建立整地範圍線交易未能提交。");
                }
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                Mode = mode,
                FootprintCurveCount = innerCurves,
                DaylightCurveCount = outerCurves,
                Message = mode == "footprint_only"
                    ? $"整地範圍線已建立（樓板投影邊界 {innerCurves} 段；footprint 無放坡帶）。"
                    : $"整地範圍線已建立（樓板投影邊界 {innerCurves} 段＋放坡外緣 {outerCurves} 段，圓角）。"
            };
        }

        /// <summary>解析坡度字串「1:n」為 run/rise（水平/垂直）；無效時回緩坡預設 2。</summary>
        private static double ParseRunPerRise(string slopeRatio)
        {
            if (!string.IsNullOrWhiteSpace(slopeRatio))
            {
                var parts = slopeRatio.Split(':');
                if (parts.Length == 2
                    && double.TryParse(parts[0], out var rise)
                    && double.TryParse(parts[1], out var run)
                    && rise > 0 && run > 0)
                {
                    return run / rise;
                }
            }

            return 2.0;
        }

        /// <summary>把各封閉點環以模型線段連成環（退化段容錯略過），回傳建立的線段數。</summary>
        private static int DrawModelLoops(Document doc, SketchPlane sketchPlane, List<List<XYZ>> loops)
        {
            var minLength = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var count = 0;
            foreach (var loop in loops)
            {
                if (loop.Count < 2)
                {
                    continue;
                }

                for (var index = 0; index < loop.Count; index++)
                {
                    var start = loop[index];
                    var end = loop[(index + 1) % loop.Count];
                    if (start.DistanceTo(end) < minLength)
                    {
                        continue;
                    }

                    try
                    {
                        doc.Create.NewModelCurve(Line.CreateBound(start, end), sketchPlane);
                        count++;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 退化或共點線段略過。
                    }
                }
            }

            return count;
        }
    }
}
#endif
