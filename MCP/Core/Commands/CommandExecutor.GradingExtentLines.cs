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
        // 範圍線取樣參數：步距 1 m（超過 4 萬節點時自動放大）、擾動判定門檻 50 mm、
        // 簡化容差 0.75 步距（把 1 步距階梯斜線收成直線）、最小環面積 4 m²。
        // 實機教訓（2026-07-08 新址總驗）：設計副本加點重新三角化會在帶內外留下 2〜20cm 的
        // 表面微差斑點（25 環其中 24 個是斑點），門檻 50mm＋面積過濾雙保險去除；
        // 真實擾動（放坡收尾帶 1:2 時每 10cm 水平即 5cm 高差）不受影響。
        private const double ExtentSampleStepMillimeters = 1000.0;
        private const double ExtentDisturbedToleranceMillimeters = 50.0;
        private const double ExtentMinLoopAreaSquareMeters = 4.0;
        private const int ExtentMaxGridNodes = 40000;

        /// <summary>
        /// 整地範圍線：量測「原地形 vs 設計地形」差異場的邊界，畫成封閉、不自交的完整模型線環。
        /// 2026-07-08 使用者裁決重寫：舊做法逐板重建坡腳環——多板環交疊、跳過處連弦、飄出地形；
        /// 改為對結果取等值輪廓，多板聯集、逐邊坡率、上限截止、地形邊界全部自動正確。
        /// 重跑會先清掉本方案先前的範圍線（登記簿 ExtentLineIds；舊版無記錄者以高程平面清除）。
        /// 純標示，擾動面積以登記簿為準。
        /// </summary>
        private object CreateGradingExtentLines(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var view = ResolveSchemeView(doc, parameters, record);
            var mode = record.Value<string>("Mode") ?? "footprint_only";

            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original))
            {
                throw new InvalidOperationException($"方案的原地形 {originalId} 不存在，無法比對擾動範圍。");
            }

            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var floors = adapter.ValidateFloors(doc, floorIds);
            var footprints = adapter.ExtractBottomFootprints(floors);
            if (footprints.Count == 0)
            {
                throw new InvalidOperationException("方案沒有可用的控制樓板投影，無法建立範圍線。");
            }

            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
            var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                throw new InvalidOperationException("取不到原地形或設計地形的實體幾何。");
            }

            var originalBox = original.get_BoundingBox(null);
            var designBox = design.get_BoundingBox(null);
            if (originalBox == null || designBox == null)
            {
                throw new InvalidOperationException("取不到地形範圍盒。");
            }

            var rayBottomZ = Math.Min(originalBox.Min.Z, designBox.Min.Z) - 10.0;
            var rayTopZ = Math.Max(originalBox.Max.Z, designBox.Max.Z) + 10.0;

            // 取樣範圍：控制樓板投影包圍盒外擴帶寬＋2 m 邊距。
            var bandWidthMeters = mode == "offset_transition"
                ? record.Value<double?>("OffsetDistanceMeters") ?? 0
                : mode == "slope_transition"
                    ? record.Value<double?>("MaxExtensionMeters") ?? 20
                    : 0.0;
            var margin = UnitUtils.ConvertToInternalUnits(bandWidthMeters + 2.0, UnitTypeId.Meters);
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var point in footprints.SelectMany(footprint => footprint.OuterLoop))
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            minX -= margin;
            minY -= margin;
            maxX += margin;
            maxY += margin;

            var step = UnitUtils.ConvertToInternalUnits(ExtentSampleStepMillimeters, UnitTypeId.Millimeters);
            var estimatedNodes = (((maxX - minX) / step) + 1) * (((maxY - minY) / step) + 1);
            if (estimatedNodes > ExtentMaxGridNodes)
            {
                step *= Math.Sqrt(estimatedNodes / ExtentMaxGridNodes); // 超大基地自動放大步距。
            }

            var countX = (int)Math.Ceiling((maxX - minX) / step) + 1;
            var countY = (int)Math.Ceiling((maxY - minY) / step) + 1;
            var disturbedTolerance = UnitUtils.ConvertToInternalUnits(
                ExtentDisturbedToleranceMillimeters, UnitTypeId.Millimeters);

            var mask = new bool[countX, countY];
            for (var ix = 0; ix < countX; ix++)
            {
                for (var iy = 0; iy < countY; iy++)
                {
                    var sample = new Point2D(minX + (ix * step), minY + (iy * step));
                    var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        originalSolids, sample, rayBottomZ, rayTopZ);
                    if (!originalZ.HasValue)
                    {
                        continue; // 超出地形＝未擾動，輪廓自動貼齊地形邊界。
                    }

                    var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        designSolids, sample, rayBottomZ, rayTopZ);
                    if (!designZ.HasValue)
                    {
                        continue;
                    }

                    mask[ix, iy] = Math.Abs(designZ.Value - originalZ.Value) > disturbedTolerance;
                }
            }

            var minLoopArea = UnitUtils.ConvertToInternalUnits(
                ExtentMinLoopAreaSquareMeters, UnitTypeId.SquareMeters);
            var loops = DisturbanceContour.ExtractBoundaryLoops(mask, minX, minY, step)
                .Where(loop => Polygon2D.Area(loop) >= minLoopArea) // 去除重新三角化微差斑點環
                .Select(loop => DisturbanceContour.SimplifyLoop(loop, step * 0.75))
                .Where(loop => loop.Count >= 3)
                .ToList();
            if (loops.Count == 0)
            {
                throw new InvalidOperationException("量測不到擾動範圍（原地形與設計地形差異低於門檻）。");
            }

            // 線環繪於水平面（設計地形頂 + 200mm），僅取 XY 呈現平面擾動範圍。
            var planeZ = designBox.Max.Z + UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);

            int curveCount;
            using (var transaction = new Transaction(doc, "建立整地範圍線"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立整地範圍線交易。");
                }

                DeletePreviousExtentLines(doc, record, planeZ);

                var sketchPlane = SketchPlane.Create(
                    doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, planeZ)));
                var createdIds = DrawModelLoops(
                    doc,
                    sketchPlane,
                    loops.Select(loop => loop.Select(p => new XYZ(p.X, p.Y, planeZ)).ToList()).ToList());
                curveCount = createdIds.Count;

                record["ExtentLineIds"] = new JArray(createdIds.Select(id => id.GetIdValue()));
                UpdateSchemeRecord(doc, design, record);

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("建立整地範圍線交易未能提交。");
                }
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                Mode = mode,
                LoopCount = loops.Count,
                CurveCount = curveCount,
                Message = $"整地範圍線已建立：{loops.Count} 條封閉邊界環、共 {curveCount} 段"
                    + "（量測原/設計地形差異場，多板聯集自動合併；重跑會先清除舊線）。"
            };
        }

        /// <summary>
        /// 清除本方案先前建立的範圍線：優先用登記簿記錄的 ExtentLineIds（精確）；
        /// 舊版記錄沒有 ID 時，清掉位於範圍線高程平面上的水平模型線（一次性遷移，
        /// 各方案設計地形同源故平面高程相同——舊版亂線全屬待清雜訊，可一併移除）。
        /// </summary>
        private static void DeletePreviousExtentLines(Document doc, JObject record, double planeZ)
        {
            var recordedIds = (record["ExtentLineIds"] as JArray)?.Values<long>().ToList();
            if (recordedIds != null && recordedIds.Count > 0)
            {
                var deletable = recordedIds
                    .Select(id => new ElementId(checked((int)id)))
                    .Where(id => doc.GetElement(id) != null)
                    .ToList();
                if (deletable.Count > 0)
                {
                    doc.Delete(deletable);
                }

                return;
            }

            var zTolerance = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var legacyIds = new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .Where(curveElement => curveElement is ModelCurve
                    && curveElement.GeometryCurve is Line line
                    && Math.Abs(line.GetEndPoint(0).Z - planeZ) < zTolerance
                    && Math.Abs(line.GetEndPoint(1).Z - planeZ) < zTolerance)
                .Select(curveElement => curveElement.Id)
                .ToList();
            if (legacyIds.Count > 0)
            {
                doc.Delete(legacyIds);
            }
        }

        /// <summary>把各封閉點環以模型線段連成環（退化段容錯略過），回傳建立的線段元素 ID。</summary>
        private static List<ElementId> DrawModelLoops(Document doc, SketchPlane sketchPlane, List<List<XYZ>> loops)
        {
            var minLength = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var created = new List<ElementId>();
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
                        var curve = doc.Create.NewModelCurve(Line.CreateBound(start, end), sketchPlane);
                        created.Add(curve.Id);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 退化或共點線段略過。
                    }
                }
            }

            return created;
        }
    }
}
#endif
