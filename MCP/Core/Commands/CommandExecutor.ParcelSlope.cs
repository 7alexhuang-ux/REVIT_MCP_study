using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    ///
    /// 第二期擴充（2026-10-31）：縣市預設檔（ParcelPresets）、網格平移與半格比對、
    /// 基地範圍裁切＋面積加權平均、§262 但書概略坡度提示、計算書 CSV。
    /// 純數學部分在 MCP/Core/Grading/ParcelSlopeMath.cs 與 ParcelAnalysis.cs（有 NUnit 測試）。
    /// </summary>
    public partial class CommandExecutor
    {
        /// <summary>坵塊網格數上限。超過即中止並要求放大坵塊邊長，避免無意義的巨量運算。</summary>
        private const int MaxParcelCells = 20000;

        /// <summary>基地範圍輪廓串接容差（公尺）：端點距離在此內視為相接。</summary>
        private const double ParcelBoundaryJoinToleranceMeters = 0.01;

        /// <summary>§262 第 1 項第 1 款但書的概略坡度門檻（%）。</summary>
        private const double ParcelProvisoRoughSlopePercent = 15.0;

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

        private static ElementId ParcelElementId(long value)
        {
#if REVIT2025_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId(checked((int)value));
#endif
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
                     + "若首曲線與計曲線同層，交點數會多計，請先在 CAD 端分層。"
                     + "基地範圍若在 DWG 內，可用 boundaryLayerName 指定其圖層（封閉曲線）。",
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

            return BuildParcelResponse(input, result, drawn: false, createdElementIds: null, csv: null);
        }

        // ────────────────────────────────────────────────
        // 3. 計算並繪製坵塊圖
        // ────────────────────────────────────────────────

        /// <summary>
        /// 產生坵塊圖：畫出坵塊網格線，並在每格中央標註「編號／交點數／平均坡度」，
        /// 受 §262 限制的坵塊另加標示文字。只畫主組；半格比對組只回數字。
        /// </summary>
        private object CreateParcelSlopeMap(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;

            // csvPath 先驗證（早於計算與繪圖）：相對路徑會落到 Revit 的工作目錄，使用者找不到檔案。
            var csvPath = parameters["csvPath"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(csvPath) && !ParcelReport.IsAbsolutePath(csvPath.Trim()))
            {
                throw new ArgumentException(
                    $"csvPath 必須是絕對路徑（例如 C:\\Temp\\parcel.csv），收到「{csvPath}」。"
                    + "相對路徑會寫到 Revit 的工作目錄，不是你預期的位置，已拒絕。");
            }

            var input = ReadParcelInput(doc, parameters);
            var result = ComputeParcelSlope(doc, input);

            var textSizeMm = parameters["textSizeMm"]?.Value<double?>() ?? 2.0;
            var createdIds = new List<long>();

            using (var tx = new Transaction(doc, "坵塊法平均坡度分析"))
            {
                tx.Start();

                var textType = ResolveTextNoteType(doc, textSizeMm);
                var edgeLabelKeys = new HashSet<string>(StringComparer.Ordinal);

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

                    // 格中央標註：編號／交點數／坡度（有基地範圍時加基地內面積）
                    var label = $"{cell.Label}\nn={cell.IntersectionCount}\nS={cell.SlopePercent:0.0}%";
                    if (result.Boundary != null)
                    {
                        label += $"\nA={cell.AreaSquareMeters:0.0}㎡";
                    }

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

                    // 單邊交點數標示（§261 第（二）目「記於各方格邊上」）。共用邊兩格數字相同，只標一次。
                    if (input.LabelEdgeCounts)
                    {
                        for (var i = 0; i < 4; i++)
                        {
                            var a = cell.Corners[i];
                            var b = cell.Corners[(i + 1) % 4];
                            var mx = (a.X + b.X) / 2.0;
                            var my = (a.Y + b.Y) / 2.0;
                            var key = mx.ToString("0.000", CultureInfo.InvariantCulture) + ","
                                    + my.ToString("0.000", CultureInfo.InvariantCulture);
                            if (!edgeLabelKeys.Add(key))
                            {
                                continue;
                            }

                            var edgeNote = TextNote.Create(
                                doc, input.View.Id, ToInternalXyz(new ParcelPoint(mx, my)),
                                cell.EdgeCounts[i].ToString(CultureInfo.InvariantCulture),
                                new TextNoteOptions(textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Center });
                            if (edgeNote != null)
                            {
                                createdIds.Add(edgeNote.Id.GetIdValue());
                            }
                        }
                    }
                }

                tx.Commit();
            }

            // CSV 計算書：圖已畫完，寫檔失敗只回報、不回滾圖面。
            object csv = null;
            if (!string.IsNullOrWhiteSpace(csvPath))
            {
                csv = WriteParcelCsv(csvPath.Trim(), input, result);
            }

            return BuildParcelResponse(input, result, drawn: true, createdElementIds: createdIds, csv: csv);
        }

        private static object WriteParcelCsv(string path, ParcelInput input, ParcelComputation result)
        {
            try
            {
                var text = ParcelReport.BuildCsv(result.Cells, result.Statistics, new ParcelCsvContext
                {
                    Resolution = input.Resolution,
                    CellSizeMeters = input.CellSizeMeters,
                    ContourIntervalMeters = input.ContourIntervalMeters,
                    RotationDegrees = input.RotationDegrees,
                    OriginOffsetXMeters = input.OriginOffsetXMeters,
                    OriginOffsetYMeters = input.OriginOffsetYMeters,
                    BoundaryDescription = result.Boundary?.Description,
                });

                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return new { path = Path.GetFullPath(path), written = true, rowCount = result.Cells.Count, error = (string)null };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is NotSupportedException)
            {
                return new { path, written = false, rowCount = 0, error = ex.Message };
            }
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

            var warnings = new List<string>(input.Warnings);
            var contourBounds = ResolveBounds(input, contours);

            // 基地範圍（選填）。有基地範圍時網格以基地外框為準，確保蓋滿整個基地。
            var boundary = ResolveParcelBoundary(doc, input, warnings);
            var bounds = contourBounds;
            if (boundary != null)
            {
                bounds = boundary.Bounds;
                if (bounds.MinX < contourBounds.MinX || bounds.MinY < contourBounds.MinY
                    || bounds.MaxX > contourBounds.MaxX || bounds.MaxY > contourBounds.MaxY)
                {
                    warnings.Add("基地範圍超出等高線的外框：超出部分沒有等高線資料，交點數為 0、坡度視為 0%，"
                        + "會把平均坡度拉低。請確認地形圖涵蓋整個基地。");
                }
            }

            var cells = BuildParcelCells(
                contours, boundary, bounds, input, input.OriginOffsetXMeters, input.OriginOffsetYMeters);
            var stats = ParcelReport.Summarize(cells, input.Resolution.Preset);

            if (cells.Count == 0)
            {
                warnings.Add(boundary != null
                    ? "基地範圍與網格沒有任何交集，請確認基地範圍與等高線是否在同一座標系。"
                    : "沒有任何有效坵塊。");
            }

            // 半格平移比對：同參數再算一組（主組 offset 再各加 L/2），只回數字、不畫。
            ParcelHalfShiftResult half = null;
            if (input.CompareHalfShift)
            {
                var hx = ParcelSlopeMath.NormalizeOffset(
                    input.OriginOffsetXMeters + input.CellSizeMeters / 2.0, input.CellSizeMeters);
                var hy = ParcelSlopeMath.NormalizeOffset(
                    input.OriginOffsetYMeters + input.CellSizeMeters / 2.0, input.CellSizeMeters);
                try
                {
                    var halfCells = BuildParcelCells(contours, boundary, bounds, input, hx, hy);
                    var halfStats = ParcelReport.Summarize(halfCells, input.Resolution.Preset);
                    half = new ParcelHalfShiftResult
                    {
                        OffsetXMeters = hx,
                        OffsetYMeters = hy,
                        Statistics = halfStats,
                        Delta = ParcelReport.Delta(stats, halfStats),
                    };
                }
                catch (ParcelCellLimitException ex)
                {
                    // 比對組只是輔助：格數超限時略過比對，不讓主組結果跟著失敗。
                    warnings.Add("半格平移比對已略過：" + ex.Message);
                }
            }

            // §262 第 1 項第 1 款但書的概略坡度提示（只在有基地範圍時才有「基地內」可言）。
            string roughNote;
            var rough = ComputeParcelRoughSlope(doc, input, boundary, out roughNote);

            return new ParcelComputation
            {
                Cells = cells,
                ContourCurveCount = contours.Count,
                Bounds = bounds,
                Statistics = stats,
                Boundary = boundary,
                HalfShift = half,
                RoughSlope = rough,
                RoughSlopeNote = roughNote,
                Warnings = warnings,
            };
        }

        /// <summary>
        /// 用指定的網格原點平移算出整組坵塊。
        /// 有基地範圍：只保留與基地相交的坵塊，A_i 為基地內面積，並重新連號；
        /// 無基地範圍：維持舊行為（整格 A=L²，空格濾除）。
        /// </summary>
        private List<ParcelCellRecord> BuildParcelCells(
            IReadOnlyList<Curve> contours, ParcelBoundaryInfo boundary, ParcelBounds bounds,
            ParcelInput input, double offsetX, double offsetY)
        {
            var grid = ParcelSlopeMath.BuildGrid(
                bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY,
                input.CellSizeMeters, input.RotationDegrees, offsetX, offsetY);

            if (grid.Count > MaxParcelCells)
            {
                throw new ParcelCellLimitException(
                    $"坵塊數 {grid.Count} 超過上限 {MaxParcelCells}。請放大坵塊邊長"
                    + $"（目前 {input.CellSizeMeters} m，預設檔「{input.Resolution.Preset.DisplayName}」上限 "
                    + $"{input.Resolution.Preset.MaxCellSizeMeters} m）或縮小分析範圍。");
            }

            var cellArea = input.CellSizeMeters * input.CellSizeMeters;
            var cells = new List<ParcelCellRecord>();
            var index = 0;

            foreach (var cell in grid)
            {
                double area;
                if (boundary != null)
                {
                    if (!CellMayOverlap(cell, boundary.Bounds))
                    {
                        continue;
                    }

                    area = ParcelSlopeMath.AreaInsideBoundary(cell.Corners, boundary.Polygon);
                    if (area <= 0)
                    {
                        continue; // 與基地不相交
                    }
                }
                else
                {
                    area = cellArea;
                }

                var edgeCounts = CountEdgeIntersections(cell, contours);
                var n = edgeCounts.Sum();

                // 無基地範圍時：完全沒有交點、且格中心落在等高線範圍外的空格不輸出，避免滿版空格洗掉圖面。
                if (boundary == null && n == 0 && !IsWithinBounds(cell.Center, bounds))
                {
                    continue;
                }

                var slope = ParcelSlopeMath.SlopePercent(n, input.ContourIntervalMeters, input.CellSizeMeters);
                index++;

                cells.Add(new ParcelCellRecord
                {
                    Label = index.ToString(CultureInfo.InvariantCulture),
                    Col = cell.Col,
                    Row = cell.Row,
                    Center = cell.Center,
                    Corners = cell.Corners,
                    EdgeCounts = edgeCounts,
                    IntersectionCount = n,
                    SlopePercent = slope,
                    AreaSquareMeters = area,
                    Restriction = ParcelSlopeMath.Classify(slope),
                    GradeLabel = ParcelReport.GradeLabel(slope, input.Resolution.Preset.GradeThresholdsPercent),
                });
            }

            return cells;
        }

        private static bool CellMayOverlap(ParcelCell cell, ParcelBounds b)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in cell.Corners)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            return maxX >= b.MinX && minX <= b.MaxX && maxY >= b.MinY && minY <= b.MaxY;
        }

        /// <summary>
        /// 數該坵塊<b>四邊</b>與等高線的交點，回傳每邊各自的點數（邊 i = 角 i→角 i+1）；
        /// n 為四邊總和（§261 第一款第（二）目）。
        /// 相鄰坵塊共用邊上的交點，兩格各自計入——條文是逐格獨立計算，不做共用邊去重。
        /// </summary>
        private static int[] CountEdgeIntersections(ParcelCell cell, IReadOnlyList<Curve> contours)
        {
            var perEdge = new int[4];

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
                        perEdge[i] += results.Size;
                    }

                    // Subset / Superset / Equal 皆代表共線重疊 → 依 domain §3.3 不計。
                }
            }

            return perEdge;
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

            var resolution = ParcelPresets.Resolve(parameters["jurisdiction"]?.Value<string>());
            // 違反預設檔格邊長規定 → 拒絕（ArgumentOutOfRangeException，訊息含規定與來源）。
            ParcelPresets.ValidateCellSize(resolution.Preset, cellSize);

            var boundaryLayer = parameters["boundaryLayerName"]?.Value<string>();
            var boundaryIds = ReadBoundaryElementIds(parameters["boundaryElementId"]);
            if (!string.IsNullOrWhiteSpace(boundaryLayer) && boundaryIds.Count > 0)
            {
                throw new ArgumentException("boundaryLayerName 與 boundaryElementId 只能擇一指定基地範圍來源。");
            }

            // 平移量先對 L 取正餘數（0 ≤ offset < L）：平移整數格等於同一組格線。
            var rawOffsetX = parameters["originOffsetXMeters"]?.Value<double?>() ?? 0.0;
            var rawOffsetY = parameters["originOffsetYMeters"]?.Value<double?>() ?? 0.0;
            var offsetX = ParcelSlopeMath.NormalizeOffset(rawOffsetX, cellSize);
            var offsetY = ParcelSlopeMath.NormalizeOffset(rawOffsetY, cellSize);
            var inputWarnings = new List<string>();
            if (Math.Abs(offsetX - rawOffsetX) > 1e-9 || Math.Abs(offsetY - rawOffsetY) > 1e-9)
            {
                inputWarnings.Add(
                    $"原點平移量已對 L={cellSize} m 取正餘數：X {rawOffsetX} → {offsetX}、Y {rawOffsetY} → {offsetY}"
                    + "（平移整數格等於同一組格線，結果相同）。回應與計算書記錄的是取餘後的有效值。");
            }

            return new ParcelInput
            {
                View = ResolveParcelView(doc, parameters),
                LayerName = layerName.Trim(),
                ContourIntervalMeters = contourInterval.Value,
                CellSizeMeters = cellSize,
                RotationDegrees = rotation,
                Resolution = resolution,
                OriginOffsetXMeters = offsetX,
                OriginOffsetYMeters = offsetY,
                Warnings = inputWarnings,
                CompareHalfShift = parameters["compareHalfShift"]?.Value<bool?>() ?? false,
                BoundaryLayerName = string.IsNullOrWhiteSpace(boundaryLayer) ? null : boundaryLayer.Trim(),
                BoundaryElementIds = boundaryIds,
                LabelEdgeCounts = parameters["labelEdgeCounts"]?.Value<bool?>() ?? false,
            };
        }

        private static List<long> ReadBoundaryElementIds(JToken token)
        {
            var ids = new List<long>();
            if (token == null || token.Type == JTokenType.Null)
            {
                return ids;
            }

            if (token is JArray arr)
            {
                ids.AddRange(arr.Select(t => t.Value<long>()));
            }
            else
            {
                ids.Add(token.Value<long>());
            }

            return ids;
        }

        private View ResolveParcelView(Document doc, JObject parameters)
        {
            var viewIdValue = parameters["viewId"]?.Value<long?>();
            if (viewIdValue.HasValue)
            {
                if (doc.GetElement(ParcelElementId(viewIdValue.Value)) is View resolved)
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
        // 基地範圍
        // ────────────────────────────────────────────────

        /// <summary>
        /// 取得基地範圍多邊形（公尺）。來源：DWG 圖層（boundaryLayerName）或 Revit 元素（boundaryElementId：
        /// PropertyLine、模型線／詳圖線，或包含這些線的群組）。未指定回 null。
        /// 同一來源若串出多個封閉輪廓，只取面積最大者並警告（內含孔洞／多筆基地的情形不在第一期範圍）。
        /// </summary>
        private ParcelBoundaryInfo ResolveParcelBoundary(Document doc, ParcelInput input, List<string> warnings)
        {
            List<IReadOnlyList<ParcelPoint>> chains;
            string source;
            var sawArc = false;

            if (!string.IsNullOrEmpty(input.BoundaryLayerName))
            {
                chains = CollectBoundaryChainsFromLayer(doc, input, ref sawArc);
                source = $"DWG 圖層「{input.BoundaryLayerName}」";
                if (chains.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"基地範圍圖層「{input.BoundaryLayerName}」在本視圖中找不到任何曲線。"
                        + "請以 get_parcel_contour_layers 確認圖層名稱。");
                }
            }
            else if (input.BoundaryElementIds.Count > 0)
            {
                chains = new List<IReadOnlyList<ParcelPoint>>();
                foreach (var id in input.BoundaryElementIds)
                {
                    chains.AddRange(CollectBoundaryChainsFromElement(doc, id, ref sawArc));
                }

                source = "Revit 元素 " + string.Join("、", input.BoundaryElementIds);
                if (chains.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{source} 讀不到任何曲線。boundaryElementId 應為 PropertyLine、模型線／詳圖線，"
                        + "或包含這些線的群組。");
                }
            }
            else
            {
                return null;
            }

            var loops = ParcelSlopeMath.JoinSegmentsToLoops(chains, ParcelBoundaryJoinToleranceMeters);
            var discarded = loops.UnclosedChainCount + loops.DanglingChainCount;
            if (loops.ClosedLoops.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{source} 無法串成封閉輪廓（有 {discarded} 條未閉合或端點孤立的線，"
                    + $"端點距離需 ≤ {ParcelBoundaryJoinToleranceMeters} m）。請確認基地範圍線是封閉的。");
            }

            if (loops.DanglingChainCount > 0)
            {
                warnings.Add($"{source} 有 {loops.DanglingChainCount} 條端點孤立的懸線（例如角點多出的短線）已忽略；"
                    + "請確認它不是基地範圍的一部分。");
            }

            if (loops.UnclosedChainCount > 0)
            {
                warnings.Add($"{source} 另有 {loops.UnclosedChainCount} 條線未能串成封閉輪廓，已忽略。");
            }

            var ordered = loops.ClosedLoops
                .Select(l => new { Loop = l, Area = ParcelSlopeMath.PolygonArea(l) })
                .OrderByDescending(x => x.Area)
                .ToList();
            var chosen = ordered[0];
            if (ordered.Count > 1)
            {
                warnings.Add($"{source} 串出 {ordered.Count} 個封閉輪廓，只採面積最大者"
                    + $"（{chosen.Area:0.00} ㎡），其餘 {ordered.Count - 1} 個已忽略；"
                    + "若基地由多個輪廓組成或含孔洞，請分開計算。");
            }

            if (chosen.Area <= 0)
            {
                throw new InvalidOperationException($"{source} 的封閉輪廓面積為 0，無法作為基地範圍。");
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in chosen.Loop)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            if (sawArc)
            {
                warnings.Add("基地範圍中的圓弧以 24 段折線近似，面積為近似值。");
            }

            return new ParcelBoundaryInfo
            {
                Polygon = chosen.Loop,
                AreaSquareMeters = chosen.Area,
                LoopCount = ordered.Count,
                Source = source,
                Description = $"{source}，封閉輪廓 {ordered.Count} 個，採最大者，面積 {chosen.Area:0.00} ㎡",
                Bounds = new ParcelBounds { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY },
            };
        }

        private List<IReadOnlyList<ParcelPoint>> CollectBoundaryChainsFromLayer(Document doc, ParcelInput input, ref bool sawArc)
        {
            var chains = new List<IReadOnlyList<ParcelPoint>>();
            var imports = new FilteredElementCollector(doc, input.View.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            var opts = new Options { ComputeReferences = false, View = input.View };
            foreach (var import in imports)
            {
                var geo = import.get_Geometry(opts);
                if (geo == null)
                {
                    continue;
                }

                foreach (var obj in FlattenGeometry(geo))
                {
                    if (!string.Equals(ResolveLayerName(doc, obj), input.BoundaryLayerName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (obj is PolyLine polyline)
                    {
                        var pts = polyline.GetCoordinates().Select(ToParcelPointMeters).ToList();
                        if (pts.Count >= 2)
                        {
                            chains.Add(pts);
                        }
                    }
                    else if (obj is Curve curve)
                    {
                        if (!(curve is Line))
                        {
                            sawArc = true;
                        }

                        chains.Add(CurveSamplePoints(curve).Select(ToParcelPointMeters).ToList());
                    }
                }
            }

            return chains;
        }

        private List<IReadOnlyList<ParcelPoint>> CollectBoundaryChainsFromElement(Document doc, long id, ref bool sawArc)
        {
            var root = doc.GetElement(ParcelElementId(id));
            if (root == null)
            {
                throw new ArgumentException($"boundaryElementId {id} 不是有效的元素。");
            }

            var elements = new List<Element>();
            if (root is Group group)
            {
                foreach (var memberId in group.GetMemberIds())
                {
                    var member = doc.GetElement(memberId);
                    if (member != null)
                    {
                        elements.Add(member);
                    }
                }
            }
            else
            {
                elements.Add(root);
            }

            var chains = new List<IReadOnlyList<ParcelPoint>>();
            foreach (var element in elements)
            {
                foreach (var curve in CollectElementCurves(doc, element))
                {
                    if (!(curve is Line))
                    {
                        sawArc = true;
                    }

                    chains.Add(CurveSamplePoints(curve).Select(ToParcelPointMeters).ToList());
                }
            }

            return chains;
        }

        /// <summary>
        /// 取出元素的曲線。PropertyLine 父元件與子線段（PropertyLineSegment）常回傳同一條曲線，
        /// 重複的由 ParcelSlopeMath.JoinSegmentsToLoops 去重。
        /// </summary>
        private static List<Curve> CollectElementCurves(Document doc, Element element)
        {
            var curves = new List<Curve>();
            var options = new Options { IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };

            try
            {
                AppendCurves(element.get_Geometry(options), curves);
                if (curves.Count == 0)
                {
                    var c = (element as CurveElement)?.GeometryCurve ?? (element.Location as LocationCurve)?.Curve;
                    if (c != null)
                    {
                        curves.Add(c);
                    }
                }

                // 父元件讀不到幾何時，退而讀它的相依子元素（例如 PropertyLine 的線段）。
                if (curves.Count == 0)
                {
                    foreach (var depId in element.GetDependentElements(null))
                    {
                        var dep = doc.GetElement(depId);
                        if (dep == null || dep.Id == element.Id)
                        {
                            continue;
                        }

                        AppendCurves(dep.get_Geometry(options), curves);
                        if (dep is CurveElement ce && ce.GeometryCurve != null && curves.Count == 0)
                        {
                            curves.Add(ce.GeometryCurve);
                        }
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 讀不到幾何的元素略過；全部讀不到時呼叫端會丟「讀不到任何曲線」。
            }

            return curves;
        }

        private static void AppendCurves(GeometryElement geometry, List<Curve> curves)
        {
            if (geometry == null)
            {
                return;
            }

            foreach (var obj in geometry)
            {
                if (obj is Curve c)
                {
                    curves.Add(c);
                }
                else if (obj is GeometryInstance gi)
                {
                    AppendCurves(gi.GetInstanceGeometry(), curves);
                }
            }
        }

        private static ParcelPoint ToParcelPointMeters(XYZ p)
        {
            return new ParcelPoint(ParcelFeetToMeters(p.X), ParcelFeetToMeters(p.Y));
        }

        // ────────────────────────────────────────────────
        // §262 但書概略坡度（提示用）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 取基地內等高線取樣點的最高、最低高程與水平距離，求概略坡度。
        /// 高程取自 DWG 曲線的 Z 值（假設 DWG 以公尺為單位、匯入基準高程為 0）；
        /// Z 全相同（平面 CAD）或沒有基地範圍時回 null 並說明原因。
        /// </summary>
        private IReadOnlyList<ParcelPoint3> CollectContourElevationPoints(
            Document doc, ParcelInput input, ParcelBoundaryInfo boundary)
        {
            var imports = new FilteredElementCollector(doc, input.View.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            var opts = new Options { ComputeReferences = false, View = input.View };
            var points = new List<ParcelPoint3>();

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

                    IEnumerable<XYZ> pts;
                    if (obj is PolyLine polyline)
                    {
                        pts = polyline.GetCoordinates();
                    }
                    else if (obj is Curve curve)
                    {
                        pts = CurveSamplePoints(curve);
                    }
                    else
                    {
                        continue;
                    }

                    foreach (var p in pts)
                    {
                        var x = ParcelFeetToMeters(p.X);
                        var y = ParcelFeetToMeters(p.Y);
                        if (ParcelSlopeMath.PointInPolygon(boundary.Polygon, x, y))
                        {
                            points.Add(new ParcelPoint3(x, y, ParcelFeetToMeters(p.Z)));
                        }
                    }
                }
            }

            return points;
        }

        private ParcelRoughSlope ComputeParcelRoughSlope(
            Document doc, ParcelInput input, ParcelBoundaryInfo boundary, out string note)
        {
            if (boundary == null)
            {
                note = "未指定基地範圍（boundaryLayerName／boundaryElementId），無法取「基地內」等高線的最高低點，略過但書概略坡度。";
                return null;
            }

            var points = CollectContourElevationPoints(doc, input, boundary);
            if (points.Count == 0)
            {
                note = "基地內沒有等高線取樣點，略過但書概略坡度。";
                return null;
            }

            var rough = ParcelSlopeMath.RoughSlopeBetweenExtremes(points);
            if (rough == null)
            {
                note = "基地內等高線沒有可用的高程資訊（Z 值全部相同或最高低點水平重合，常見於平面 CAD），"
                     + "略過但書概略坡度；請自行以圖面標高判讀。";
                return null;
            }

            note = "概略坡度＝基地內等高線取樣點之最高、最低點高差 ÷ 水平距離，取樣點為極值非逐線精算；"
                 + "高程取自 DWG 曲線 Z 值（假設單位為公尺、匯入基準高程為 0），請與圖面標高核對。";
            return rough;
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

        private static object StatisticsPayload(ParcelStatistics s)
        {
            return new
            {
                cellCount = s.CellCount,
                totalAreaSquareMeters = Math.Round(s.TotalAreaSquareMeters, 2),
                slopeAreaSum = Math.Round(s.SlopeAreaSum, 2),
                averageSlopePercent = Math.Round(s.WeightedAverageSlopePercent, 3),
                maxSlopePercent = Math.Round(s.MaxSlopePercent, 2),
                minSlopePercent = Math.Round(s.MinSlopePercent, 2),
                countNone = s.CountNone,
                countOpenSpaceOnly = s.CountOpenSpaceOnly,
                countExcluded = s.CountExcluded,
                areaNoneSquareMeters = Math.Round(s.AreaNoneSquareMeters, 2),
                areaOpenSpaceOnlySquareMeters = Math.Round(s.AreaOpenSpaceOnlySquareMeters, 2),
                areaExcludedSquareMeters = Math.Round(s.AreaExcludedSquareMeters, 2),
                areaOver30SquareMeters = Math.Round(s.AreaOver30SquareMeters, 2),
                gradeBreakdown = s.Grades.Select(g => new
                {
                    grade = g.Label,
                    cellCount = g.CellCount,
                    areaSquareMeters = Math.Round(g.AreaSquareMeters, 2),
                }).ToList(),
            };
        }

        private static object BuildParcelResponse(
            ParcelInput input, ParcelComputation result, bool drawn, List<long> createdElementIds, object csv)
        {
            var cells = result.Cells;
            var stats = result.Statistics;
            var preset = input.Resolution.Preset;

            var warnings = new List<string>();
            warnings.AddRange(ParcelPresets.Warnings(input.Resolution));
            warnings.AddRange(result.Warnings);

            object halfShift = null;
            if (result.HalfShift != null)
            {
                var d = result.HalfShift.Delta;
                halfShift = new
                {
                    description = "半格平移比對（陳璁慶 2004，水土保持學報）：法規未訂網格起點，"
                                + "將網格再平移 L/2 重算一次，比較結果是否穩定。比對組只回數字、不繪圖。",
                    main = new
                    {
                        originOffsetXMeters = input.OriginOffsetXMeters,
                        originOffsetYMeters = input.OriginOffsetYMeters,
                        statistics = StatisticsPayload(stats),
                    },
                    shifted = new
                    {
                        originOffsetXMeters = result.HalfShift.OffsetXMeters,
                        originOffsetYMeters = result.HalfShift.OffsetYMeters,
                        statistics = StatisticsPayload(result.HalfShift.Statistics),
                    },
                    difference = new
                    {
                        note = "shifted − main",
                        averageSlopePercent = Math.Round(d.AverageSlopePercent, 3),
                        areaOver30SquareMeters = Math.Round(d.AreaOver30SquareMeters, 2),
                        areaOpenSpaceOnlySquareMeters = Math.Round(d.AreaOpenSpaceOnlySquareMeters, 2),
                        areaExcludedSquareMeters = Math.Round(d.AreaExcludedSquareMeters, 2),
                        totalAreaSquareMeters = Math.Round(d.TotalAreaSquareMeters, 2),
                        cellCount = d.CellCount,
                    },
                };
            }

            object provisoHint = null;
            if (result.RoughSlope != null)
            {
                var below = result.RoughSlope.SlopePercent < ParcelProvisoRoughSlopePercent;
                provisoHint = new
                {
                    roughSlopePercent = Math.Round(result.RoughSlope.SlopePercent, 2),
                    elevationDifferenceMeters = Math.Round(result.RoughSlope.ElevationDifferenceMeters, 3),
                    horizontalDistanceMeters = Math.Round(result.RoughSlope.HorizontalDistanceMeters, 2),
                    highest = new { x = result.RoughSlope.Highest.X, y = result.RoughSlope.Highest.Y, z = result.RoughSlope.Highest.Z },
                    lowest = new { x = result.RoughSlope.Lowest.X, y = result.RoughSlope.Lowest.Y, z = result.RoughSlope.Lowest.Z },
                    belowThreshold = below,
                    hint = below
                        ? "概略坡度 < 15%：可能適用第262條第一項第一款但書，"
                          + "須另檢討有無獨立山頭／跨越主嶺線。本工具不自動豁免任何坵塊。"
                        : "概略坡度 ≥ 15%：不落入但書的 15% 條件。",
                    note = result.RoughSlopeNote,
                };
            }

            return new
            {
                drawn,
                viewId = input.View.Id.GetIdValue(),
                viewName = input.View.Name,
                layerName = input.LayerName,

                // 這幾個參數會改變逐格結果，必須隨結果一起揭露並標在圖上。
                contourIntervalMeters = input.ContourIntervalMeters,
                cellSizeMeters = input.CellSizeMeters,
                rotationDegrees = input.RotationDegrees,
                originOffsetXMeters = input.OriginOffsetXMeters,
                originOffsetYMeters = input.OriginOffsetYMeters,

                jurisdiction = new
                {
                    requested = string.IsNullOrEmpty(input.Resolution.Requested) ? null : input.Resolution.Requested,
                    key = preset.Key,
                    name = preset.DisplayName,
                    verified = preset.Verified,
                    isFallbackToCentral = input.Resolution.IsFallback,
                    fallbackNote = input.Resolution.FallbackNote,
                    legalSource = preset.LegalSource,
                    maxCellSizeMeters = preset.MaxCellSizeMeters,
                    maxCellSizeExclusive = preset.MaxCellSizeExclusive,
                    allowedCellSizes = preset.AllowedCellSizes,
                    minMapScaleDenominator = preset.MinMapScaleDenominator,
                    gradeThresholdsPercent = preset.GradeThresholdsPercent,
                    requiresOriginalAndCurrent = preset.RequiresOriginalAndCurrent,
                    notes = preset.Notes,
                },

                boundary = result.Boundary == null
                    ? null
                    : new
                    {
                        source = result.Boundary.Source,
                        closedLoopCount = result.Boundary.LoopCount,
                        areaSquareMeters = Math.Round(result.Boundary.AreaSquareMeters, 2),
                        weighting = "面積加權：平均坡度＝Σ(S×A)/ΣA，A 為坵塊落在基地內的面積；n 仍依整格四邊計。",
                    },

                formula = "S(%) = n × 3.14 × h / (8 × L) × 100　（建築技術規則建築設計施工編第 261 條）",
                contourCurveCount = result.ContourCurveCount,
                cellCount = cells.Count,

                summary = new
                {
                    // 既有欄位保留。averageSlopePercent 現為面積加權；無基地範圍時每格 A=L²，等同簡單平均。
                    maxSlopePercent = stats.MaxSlopePercent,
                    minSlopePercent = stats.MinSlopePercent,
                    averageSlopePercent = stats.WeightedAverageSlopePercent,
                    countNone = stats.CountNone,
                    countOpenSpaceOnly = stats.CountOpenSpaceOnly,
                    countExcluded = stats.CountExcluded,

                    // 第二期新增
                    averageMethod = "面積加權 Σ(S×A)/ΣA",
                    totalAreaSquareMeters = Math.Round(stats.TotalAreaSquareMeters, 2),
                    slopeAreaSum = Math.Round(stats.SlopeAreaSum, 2),
                    areaNoneSquareMeters = Math.Round(stats.AreaNoneSquareMeters, 2),
                    areaOpenSpaceOnlySquareMeters = Math.Round(stats.AreaOpenSpaceOnlySquareMeters, 2),
                    areaExcludedSquareMeters = Math.Round(stats.AreaExcludedSquareMeters, 2),
                    areaOver30SquareMeters = Math.Round(stats.AreaOver30SquareMeters, 2),
                    gradeBreakdown = stats.Grades.Select(g => new
                    {
                        grade = g.Label,
                        cellCount = g.CellCount,
                        areaSquareMeters = Math.Round(g.AreaSquareMeters, 2),
                    }).ToList(),
                },

                halfShiftComparison = halfShift,
                provisoHint,

                cells = cells.Select(c => new
                {
                    label = c.Label,
                    col = c.Col,
                    row = c.Row,
                    centerX = c.Center.X,
                    centerY = c.Center.Y,
                    intersectionCount = c.IntersectionCount,
                    edgeIntersectionCounts = c.EdgeCounts,
                    slopePercent = Math.Round(c.SlopePercent, 2),
                    areaSquareMeters = Math.Round(c.AreaSquareMeters, 2),
                    slopeAreaProduct = Math.Round(c.SlopePercent * c.AreaSquareMeters, 2),
                    gradeLabel = c.GradeLabel,
                    restriction = c.Restriction.ToString(),
                    restrictionLabel = ParcelSlopeMath.RestrictionLabel(c.Restriction),
                    verdict = ParcelReport.VerdictLabel(c.Restriction),
                }).ToList(),

                createdElementIds,
                csv,
                warnings,

                // 這段刻意寫死在回應裡：§262 要求先劃均質區再判但書，工具不得代為下結論。
                disclaimer = new[]
                {
                    "本結果為逐格坵塊平均坡度計算值。",
                    "§262 第 1 項第 1 款要求先將原始地形「區劃成若干均質區」再判定，"
                        + "且但書（區內最高低點間坡度 < 15% 且不含顯著獨立山頭或跨越主嶺線）"
                        + "需人工判讀地形，本工具不自動劃分均質區、不輸出可否開發建築之結論。",
                    "§261 要求使用比例尺不小於 1/1200 之實測地形圖，工具無法驗證來源圖是否符合，請自行確認並記錄。",
                    "h 為等高線「首曲線」間距；若來源圖首曲線與計曲線同層，交點數會多計。",
                    "判定欄（≤30%／30–55%／>55%）只陳述 §262 逐格坡度分段的法律效果，不是可否開發的終局結論。",
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
            public ParcelPresetResolution Resolution;
            public double OriginOffsetXMeters;
            public double OriginOffsetYMeters;
            public bool CompareHalfShift;
            public string BoundaryLayerName;
            public List<long> BoundaryElementIds = new List<long>();
            public bool LabelEdgeCounts;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>坵塊數超過 <see cref="MaxParcelCells"/>。獨立型別，讓半格比對組可只略過比對。</summary>
        private sealed class ParcelCellLimitException : InvalidOperationException
        {
            public ParcelCellLimitException(string message) : base(message)
            {
            }
        }

        private struct ParcelBounds
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
        }

        private sealed class ParcelBoundaryInfo
        {
            public List<ParcelPoint> Polygon;
            public double AreaSquareMeters;
            public int LoopCount;
            public string Source;
            public string Description;
            public ParcelBounds Bounds;
        }

        private sealed class ParcelHalfShiftResult
        {
            public double OffsetXMeters;
            public double OffsetYMeters;
            public ParcelStatistics Statistics;
            public ParcelStatisticsDelta Delta;
        }

        private sealed class ParcelComputation
        {
            public List<ParcelCellRecord> Cells;
            public int ContourCurveCount;
            public ParcelBounds Bounds;
            public ParcelStatistics Statistics;
            public ParcelBoundaryInfo Boundary;
            public ParcelHalfShiftResult HalfShift;
            public ParcelRoughSlope RoughSlope;
            public string RoughSlopeNote;
            public List<string> Warnings = new List<string>();
        }
    }
}
