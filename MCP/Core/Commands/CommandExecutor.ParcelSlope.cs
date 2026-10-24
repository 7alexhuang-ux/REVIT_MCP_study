using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    /// <summary>
    /// 坵塊法平均坡度分析（建築技術規則建築設計施工編第 261、262 條）。
    /// 方法定義見 domain/parcel-slope-analysis.md——該檔是方法正本，本檔只是實作。
    ///
    /// 等高線來源為 DWG 圖層（2026-09-21 使用者拍板），不從 Toposolid 切剖。
    /// 讀取模式沿用 DwgColumnExecutor：ImportInstance → GeometryInstance → GraphicsStyle 比對圖層名。
    /// </summary>
    public partial class CommandExecutor
    {
        /// <summary>坵塊網格數上限。超過即中止並要求放大坵塊邊長，避免無意義的巨量運算。</summary>
        private const int MaxParcelCells = 20000;

        /// <summary>
        /// Revit 內部單位（英呎）轉公尺。
        /// 刻意不叫 FeetToMeters——同名方法定義在 CommandExecutor.ToposolidGrading.cs，
        /// 而該檔以 #if REVIT2024_OR_GREATER 包起來，R22/R23 建置時不存在。
        /// 本模組要支援 R22–R26 全版本，故自帶一份。
        /// </summary>
        private static double ParcelFeetToMeters(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        }

        // ────────────────────────────────────────────────
        // 1. 列出候選等高線圖層
        // ────────────────────────────────────────────────

        /// <summary>
        /// 列出目前平面視圖中各 DWG 圖層的曲線數量，供使用者辨認哪一層是等高線。
        /// 曲線數量由多到少排序——等高線圖層通常是曲線數最多的那幾層。
        /// </summary>
        private object GetParcelContourLayers(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var view = ResolveParcelView(doc, parameters);

            var imports = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            if (imports.Count == 0)
            {
                throw new InvalidOperationException(
                    "目前視圖中找不到任何 CAD 連結或匯入。坵塊法的等高線來源為 DWG 圖層，"
                    + "請先將地形圖 DWG 連結或匯入本視圖。");
            }

            var opts = new Options { ComputeReferences = false, View = view };
            var counts = new Dictionary<string, int>();

            foreach (var import in imports)
            {
                var geo = import.get_Geometry(opts);
                if (geo == null)
                {
                    continue;
                }

                foreach (var obj in FlattenGeometry(geo))
                {
                    var layer = ResolveLayerName(doc, obj);
                    if (string.IsNullOrEmpty(layer))
                    {
                        continue;
                    }

                    counts.TryGetValue(layer, out var n);
                    counts[layer] = n + CurveCountOf(obj);
                }
            }

            return new
            {
                viewId = view.Id.GetIdValue(),
                viewName = view.Name,
                importCount = imports.Count,
                layers = counts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new { layerName = kv.Key, curveCount = kv.Value })
                    .ToList(),
                note = "h（等高線首曲線間距）必須由使用者輸入，工具不得猜測；"
                     + "若首曲線與計曲線同層，交點數會多計，請先在 CAD 端分層。",
            };
        }

        // ────────────────────────────────────────────────
        // 2. 計算（不畫）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 坵塊法計算：建立坵塊網格、逐格數等高線與四邊交點、代入 §261 公式求平均坡度、
        /// 依 §262 分段。<b>只計算不繪圖</b>，供使用者先確認參數與統計是否合理。
        /// </summary>
        private object PreviewParcelSlope(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var input = ReadParcelInput(doc, parameters);
            var result = ComputeParcelSlope(doc, input);

            return BuildParcelResponse(input, result, drawn: false, createdElementIds: null);
        }

        // ────────────────────────────────────────────────
        // 3. 計算並繪製坵塊圖
        // ────────────────────────────────────────────────

        /// <summary>
        /// 產生坵塊圖：畫出坵塊網格線，並在每格中央標註「編號／交點數／平均坡度」，
        /// 受 §262 限制的坵塊另加標示文字。
        /// </summary>
        private object CreateParcelSlopeMap(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var input = ReadParcelInput(doc, parameters);
            var result = ComputeParcelSlope(doc, input);

            var textSizeMm = parameters["textSizeMm"]?.Value<double?>() ?? 2.0;
            var createdIds = new List<long>();

            using (var tx = new Transaction(doc, "坵塊法平均坡度分析"))
            {
                tx.Start();

                var textType = ResolveTextNoteType(doc, textSizeMm);

                foreach (var cell in result.Cells)
                {
                    // 網格線：四邊
                    for (var i = 0; i < 4; i++)
                    {
                        var a = cell.Corners[i];
                        var b = cell.Corners[(i + 1) % 4];
                        var line = Line.CreateBound(ToInternalXyz(a), ToInternalXyz(b));
                        var dc = doc.Create.NewDetailCurve(input.View, line);
                        if (dc != null)
                        {
                            createdIds.Add(dc.Id.GetIdValue());
                        }
                    }

                    // 格中央標註：編號／交點數／坡度
                    var label = $"{cell.Label}\nn={cell.IntersectionCount}\nS={cell.SlopePercent:0.0}%";
                    var restrictionText = ParcelSlopeMath.RestrictionLabel(cell.Restriction);
                    if (!string.IsNullOrEmpty(restrictionText))
                    {
                        label += "\n" + restrictionText;
                    }

                    var note = TextNote.Create(
                        doc, input.View.Id, ToInternalXyz(cell.Center), label,
                        new TextNoteOptions(textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Center });

                    if (note != null)
                    {
                        createdIds.Add(note.Id.GetIdValue());
                        ApplyParcelColor(doc, input.View, note.Id, cell.Restriction);
                    }
                }

                tx.Commit();
            }

            return BuildParcelResponse(input, result, drawn: true, createdElementIds: createdIds);
        }

        // ────────────────────────────────────────────────
        // 核心運算
        // ────────────────────────────────────────────────

        private ParcelComputation ComputeParcelSlope(Document doc, ParcelInput input)
        {
            var contours = CollectContourCurves(doc, input);
            if (contours.Count == 0)
            {
                throw new InvalidOperationException(
                    $"圖層「{input.LayerName}」在本視圖中找不到任何曲線。"
                    + "請以 get_parcel_contour_layers 確認圖層名稱。");
            }

            var bounds = ResolveBounds(input, contours);
            var grid = ParcelSlopeMath.BuildGrid(
                bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY,
                input.CellSizeMeters, input.RotationDegrees);

            if (grid.Count > MaxParcelCells)
            {
                throw new InvalidOperationException(
                    $"坵塊數 {grid.Count} 超過上限 {MaxParcelCells}。請放大坵塊邊長"
                    + $"（目前 {input.CellSizeMeters} m，法定上限 {ParcelSlopeMath.MaxCellSizeMeters} m）"
                    + "或縮小分析範圍。");
            }

            var cells = new List<ParcelCellResult>();
            var index = 0;

            foreach (var cell in grid)
            {
                var n = CountEdgeIntersections(cell, contours);

                // 完全沒有交點、且格中心落在等高線範圍外的空格不輸出，避免滿版空格洗掉圖面。
                if (n == 0 && !IsWithinBounds(cell.Center, bounds))
                {
                    continue;
                }

                var slope = ParcelSlopeMath.SlopePercent(n, input.ContourIntervalMeters, input.CellSizeMeters);
                index++;

                cells.Add(new ParcelCellResult
                {
                    Label = index.ToString(),
                    Col = cell.Col,
                    Row = cell.Row,
                    Center = cell.Center,
                    Corners = cell.Corners,
                    IntersectionCount = n,
                    SlopePercent = slope,
                    Restriction = ParcelSlopeMath.Classify(slope),
                });
            }

            return new ParcelComputation { Cells = cells, ContourCurveCount = contours.Count, Bounds = bounds };
        }

        /// <summary>
        /// 數該坵塊<b>四邊</b>與等高線的交點總和（§261 第一款第（二）目）。
        /// 相鄰坵塊共用邊上的交點，兩格各自計入——條文是逐格獨立計算，不做共用邊去重。
        /// </summary>
        private static int CountEdgeIntersections(ParcelCell cell, IReadOnlyList<Curve> contours)
        {
            var total = 0;

            for (var i = 0; i < 4; i++)
            {
                var a = ToInternalXyz(cell.Corners[i]);
                var b = ToInternalXyz(cell.Corners[(i + 1) % 4]);

                Line edge;
                try
                {
                    edge = Line.CreateBound(a, b);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue; // 退化邊（長度為 0），跳過
                }

                foreach (var contour in contours)
                {
                    IntersectionResultArray results;
                    var comparison = edge.Intersect(contour, out results);

                    if (comparison == SetComparisonResult.Overlap && results != null)
                    {
                        total += results.Size;
                    }

                    // Subset / Superset / Equal 皆代表共線重疊 → 依 domain §3.3 不計。
                }
            }

            return total;
        }

        // ────────────────────────────────────────────────
        // 輸入解析
        // ────────────────────────────────────────────────

        private ParcelInput ReadParcelInput(Document doc, JObject parameters)
        {
            var layerName = parameters["layerName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(layerName))
            {
                throw new ArgumentException("必須指定 layerName（等高線所在的 DWG 圖層）。");
            }

            var contourInterval = parameters["contourIntervalMeters"]?.Value<double?>();
            if (!contourInterval.HasValue)
            {
                throw new ArgumentException(
                    "必須指定 contourIntervalMeters（等高線首曲線間距，公尺）。"
                    + "此值為 §261 公式的 h，工具不得猜測或預設，請向使用者確認地形圖的等高線間距。");
            }

            var cellSize = parameters["cellSizeMeters"]?.Value<double?>() ?? ParcelSlopeMath.MaxCellSizeMeters;
            var rotation = parameters["rotationDegrees"]?.Value<double?>() ?? 0.0;

            return new ParcelInput
            {
                View = ResolveParcelView(doc, parameters),
                LayerName = layerName.Trim(),
                ContourIntervalMeters = contourInterval.Value,
                CellSizeMeters = cellSize,
                RotationDegrees = rotation,
            };
        }

        private View ResolveParcelView(Document doc, JObject parameters)
        {
            var viewIdValue = parameters["viewId"]?.Value<long?>();
            if (viewIdValue.HasValue)
            {
                if (doc.GetElement(new ElementId(checked((int)viewIdValue.Value))) is View resolved)
                {
                    return resolved;
                }

                throw new ArgumentException($"viewId {viewIdValue.Value} 不是有效的視圖。");
            }

            if (doc.ActiveView is ViewPlan plan)
            {
                return plan;
            }

            throw new InvalidOperationException(
                "坵塊圖必須在平面視圖中產生。請切換到地形平面視圖，或以 viewId 指定。");
        }

        private static ParcelBounds ResolveBounds(ParcelInput input, IReadOnlyList<Curve> contours)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var curve in contours)
            {
                foreach (var p in CurveSamplePoints(curve))
                {
                    var xm = ParcelFeetToMeters(p.X);
                    var ym = ParcelFeetToMeters(p.Y);
                    if (xm < minX) minX = xm;
                    if (ym < minY) minY = ym;
                    if (xm > maxX) maxX = xm;
                    if (ym > maxY) maxY = ym;
                }
            }

            if (minX >= maxX || minY >= maxY)
            {
                throw new InvalidOperationException("等高線範圍無效，無法建立坵塊網格。");
            }

            return new ParcelBounds { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
        }

        private static bool IsWithinBounds(ParcelPoint p, ParcelBounds b)
        {
            return p.X >= b.MinX && p.X <= b.MaxX && p.Y >= b.MinY && p.Y <= b.MaxY;
        }

        // ────────────────────────────────────────────────
        // DWG 幾何讀取
        // ────────────────────────────────────────────────

        /// <summary>
        /// 取出指定圖層的等高線並<b>壓平到 Z=0</b>。
        /// Revit 的 Curve.Intersect 要求兩曲線共面；等高線本身各在自己的高程上，
        /// 不壓平則與坵塊邊（Z=0）永遠無交點。
        /// </summary>
        private IReadOnlyList<Curve> CollectContourCurves(Document doc, ParcelInput input)
        {
            var imports = new FilteredElementCollector(doc, input.View.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            var opts = new Options { ComputeReferences = false, View = input.View };
            var curves = new List<Curve>();

            foreach (var import in imports)
            {
                var geo = import.get_Geometry(opts);
                if (geo == null)
                {
                    continue;
                }

                foreach (var obj in FlattenGeometry(geo))
                {
                    if (!string.Equals(ResolveLayerName(doc, obj), input.LayerName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    curves.AddRange(FlattenToGroundPlane(obj));
                }
            }

            return curves;
        }

        private static IEnumerable<Curve> FlattenToGroundPlane(GeometryObject obj)
        {
            if (obj is Line line)
            {
                var flat = TryFlatLine(line.GetEndPoint(0), line.GetEndPoint(1));
                if (flat != null)
                {
                    yield return flat;
                }
            }
            else if (obj is Arc || obj is Curve)
            {
                // 弧與其他曲線一律以密取樣折線近似後壓平。
                // 交點數是整數統計量，折線近似對「數交點」的影響遠小於高程差造成的完全無交點。
                var curve = (Curve)obj;
                var pts = CurveSamplePoints(curve).ToList();
                for (var i = 0; i + 1 < pts.Count; i++)
                {
                    var flat = TryFlatLine(pts[i], pts[i + 1]);
                    if (flat != null)
                    {
                        yield return flat;
                    }
                }
            }
            else if (obj is PolyLine polyline)
            {
                var pts = polyline.GetCoordinates();
                for (var i = 0; i + 1 < pts.Count; i++)
                {
                    var flat = TryFlatLine(pts[i], pts[i + 1]);
                    if (flat != null)
                    {
                        yield return flat;
                    }
                }
            }
        }

        private static Line TryFlatLine(XYZ a, XYZ b)
        {
            var p0 = new XYZ(a.X, a.Y, 0);
            var p1 = new XYZ(b.X, b.Y, 0);
            if (p0.DistanceTo(p1) < 1e-7)
            {
                return null; // 垂直線壓平後退化，捨棄
            }

            try
            {
                return Line.CreateBound(p0, p1);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        private static IEnumerable<XYZ> CurveSamplePoints(Curve curve)
        {
            if (curve is Line line)
            {
                yield return line.GetEndPoint(0);
                yield return line.GetEndPoint(1);
                yield break;
            }

            const int segments = 24;
            for (var i = 0; i <= segments; i++)
            {
                yield return curve.Evaluate(i / (double)segments, true);
            }
        }

        private static IEnumerable<GeometryObject> FlattenGeometry(GeometryElement geo)
        {
            foreach (var obj in geo)
            {
                if (obj is GeometryInstance instance)
                {
                    var nested = instance.GetInstanceGeometry();
                    if (nested == null)
                    {
                        continue;
                    }

                    foreach (var child in FlattenGeometry(nested))
                    {
                        yield return child;
                    }
                }
                else
                {
                    yield return obj;
                }
            }
        }

        private static string ResolveLayerName(Document doc, GeometryObject obj)
        {
            if (obj.GraphicsStyleId == ElementId.InvalidElementId)
            {
                return null;
            }

            var style = doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
            return style?.GraphicsStyleCategory?.Name;
        }

        private static int CurveCountOf(GeometryObject obj)
        {
            if (obj is PolyLine polyline)
            {
                return Math.Max(0, polyline.GetCoordinates().Count - 1);
            }

            return obj is Curve ? 1 : 0;
        }

        // ────────────────────────────────────────────────
        // 繪圖輔助
        // ────────────────────────────────────────────────

        private static XYZ ToInternalXyz(ParcelPoint p)
        {
            return new XYZ(
                UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters),
                0);
        }

        private static TextNoteType ResolveTextNoteType(Document doc, double textSizeMm)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .ToList();

            if (types.Count == 0)
            {
                throw new InvalidOperationException("專案中沒有任何文字類型（TextNoteType），無法標註坵塊圖。");
            }

            var targetFeet = UnitUtils.ConvertToInternalUnits(textSizeMm, UnitTypeId.Millimeters);

            return types
                .OrderBy(t =>
                {
                    var param = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    return param == null ? double.MaxValue : Math.Abs(param.AsDouble() - targetFeet);
                })
                .First();
        }

        /// <summary>
        /// 依 §262 分段上色：≤30% 不著色；30~55% 橘；&gt;55% 紅。
        /// 用視圖覆寫而非改元素本身，維持與本 repo 其他工具一致的作法。
        /// </summary>
        private static void ApplyParcelColor(Document doc, View view, ElementId id, ParcelRestriction restriction)
        {
            if (restriction == ParcelRestriction.None)
            {
                return;
            }

            var color = restriction == ParcelRestriction.OpenSpaceOnly
                ? new Color(255, 127, 0)
                : new Color(227, 26, 28);

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            view.SetElementOverrides(id, ogs);
        }

        // ────────────────────────────────────────────────
        // 回應組裝
        // ────────────────────────────────────────────────

        private static object BuildParcelResponse(
            ParcelInput input, ParcelComputation result, bool drawn, List<long> createdElementIds)
        {
            var cells = result.Cells;

            return new
            {
                drawn,
                viewId = input.View.Id.GetIdValue(),
                viewName = input.View.Name,
                layerName = input.LayerName,

                // 這三個參數會改變逐格結果，必須隨結果一起揭露並標在圖上。
                contourIntervalMeters = input.ContourIntervalMeters,
                cellSizeMeters = input.CellSizeMeters,
                rotationDegrees = input.RotationDegrees,

                formula = "S(%) = n × 3.14 × h / (8 × L) × 100　（建築技術規則建築設計施工編第 261 條）",
                contourCurveCount = result.ContourCurveCount,
                cellCount = cells.Count,

                summary = new
                {
                    maxSlopePercent = cells.Count == 0 ? 0 : cells.Max(c => c.SlopePercent),
                    minSlopePercent = cells.Count == 0 ? 0 : cells.Min(c => c.SlopePercent),
                    averageSlopePercent = cells.Count == 0 ? 0 : cells.Average(c => c.SlopePercent),
                    countNone = cells.Count(c => c.Restriction == ParcelRestriction.None),
                    countOpenSpaceOnly = cells.Count(c => c.Restriction == ParcelRestriction.OpenSpaceOnly),
                    countExcluded = cells.Count(c => c.Restriction == ParcelRestriction.ExcludedFromOpenSpace),
                },

                cells = cells.Select(c => new
                {
                    label = c.Label,
                    col = c.Col,
                    row = c.Row,
                    centerX = c.Center.X,
                    centerY = c.Center.Y,
                    intersectionCount = c.IntersectionCount,
                    slopePercent = Math.Round(c.SlopePercent, 2),
                    restriction = c.Restriction.ToString(),
                    restrictionLabel = ParcelSlopeMath.RestrictionLabel(c.Restriction),
                }).ToList(),

                createdElementIds,

                // 這段刻意寫死在回應裡：§262 要求先劃均質區再判但書，工具不得代為下結論。
                disclaimer = new[]
                {
                    "本結果為逐格坵塊平均坡度計算值。",
                    "§262 第 1 項第 1 款要求先將原始地形「區劃成若干均質區」再判定，"
                        + "且但書（區內最高低點間坡度 < 15% 且不含顯著獨立山頭或跨越主嶺線）"
                        + "需人工判讀地形，本工具不自動劃分均質區、不輸出可否開發建築之結論。",
                    "§261 要求使用比例尺不小於 1/1200 之實測地形圖，工具無法驗證來源圖是否符合，請自行確認並記錄。",
                    "h 為等高線「首曲線」間距；若來源圖首曲線與計曲線同層，交點數會多計。",
                },
            };
        }

        // ────────────────────────────────────────────────
        // 內部型別
        // ────────────────────────────────────────────────

        private sealed class ParcelInput
        {
            public View View;
            public string LayerName;
            public double ContourIntervalMeters;
            public double CellSizeMeters;
            public double RotationDegrees;
        }

        private struct ParcelBounds
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
        }

        private sealed class ParcelCellResult
        {
            public string Label;
            public int Col;
            public int Row;
            public ParcelPoint Center;
            public IReadOnlyList<ParcelPoint> Corners;
            public int IntersectionCount;
            public double SlopePercent;
            public ParcelRestriction Restriction;
        }

        private sealed class ParcelComputation
        {
            public List<ParcelCellResult> Cells;
            public int ContourCurveCount;
            public ParcelBounds Bounds;
        }
    }
}
