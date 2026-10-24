#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    /// <summary>
    /// 整地方案 × 坵塊法限建坵塊疊圖檢查（check_grading_parcel_limits）。
    /// 把坵塊法（CommandExecutor.ParcelSlope.cs）算出的限建坵塊，疊到整地方案
    /// （方案登記簿，見 CommandExecutor.GradingView.cs 的 RequireSchemeRecord）控制樓板上，
    /// 回報每片樓板壓到多少限建面積。
    ///
    /// 設計見 docs/superpowers/specs/2026-10-05-grading-parcel-limit-check-design.md；
    /// 2026-10-05 主線裁決：
    ///   1) 持久化方案 A——本工具內用同組坵塊參數重算，不新增 ExtensibleStorage。
    ///   2) 單一工具＋drawOverlay 布林（預設 false＝只算不畫）。
    ///   3) 零面積／相切一律排除（見 ParcelLimitCheck.DefaultAreaToleranceSquareMeters）。
    ///   4) 疊圖冪等不升 schema、不在登記簿加欄位——比照既有線型子分類＋依方案辨識的做法。
    ///   5) 樓板來源沿用登記簿既有 FloorIds／ExtractBottomFootprints，不處理「樓板設計副本」規格。
    ///   6) 只畫線框（貼地形 ModelCurve），用兩個線型子分類分區限空地／不計入空地兩種限制。
    ///   8) 效能沿用既有 MaxParcelCells 上限，重疊判定前先用 bounding box 快篩。
    ///
    /// 2026-10-05 審查修正（獨立審查者用 shapely 驗算 1,342 案例，裁切面積本身全對；
    /// 下列是情境性問題——靜默偏差比報錯更危險，見審查意見逐條）：
    ///   1) 樓板落在坵塊網格涵蓋範圍外會靜默算 0 → 新增 CoveredAreaSquareMeters／warnings。
    ///   2) ExtractBottomFootprints 只取最大環，內孔忽略 → 偵測內環並寫進 warnings（不改 adapter）。
    ///   3) 視圖錯誤訊息沿用坵塊法原文會誤導使用者填錯參數名 → 攔截改寫成 parcelViewId。
    ///   5) 25m 坵塊邊只連 4 角點貼地形會穿地 → 先以 ~1m 細分再逐點貼地。
    ///   6) drawOverlay 失敗路徑：視圖要先查、線型子分類失敗要回滾、標籤要跨視圖清、失敗要進 warnings。
    ///   7) matchedCells／疊圖格數都設上限，超過用 warnings／truncated 說明，總量仍是全量。
    ///   9) ExtractBottomFootprint 對非單一平面底面樓板會丟例外中止整批——改成逐片捕捉。
    ///  10) 標籤站位改用面積質心（退化時退回邊內側點），不用頂點平均（U 形等凹形會跑到板外）。
    ///  11) drawOverlay=false 但文件上已有舊疊圖時提示可能過期。
    /// 本檔只做「橫跨兩套既有工具」的黏合，坵塊幾何讀取與整地樓板讀取都直接呼叫既有私有方法，
    /// 不重寫、不另建座標轉換（坵塊法輸出本身就是專案內部座標系的公尺值）。
    /// </summary>
    public partial class CommandExecutor
    {
        /// <summary>疊圖線型子分類前綴：兩種限制各一個子分類，各方案再依設計地形 ID 各自一份。</summary>
        private const string ParcelLimitLineStylePrefix = "坵塊限建疊圖";

        /// <summary>疊圖貼地形淨空（毫米），與 CreateGradingExtentLines 的範圍線一致，避免視覺穿地。</summary>
        private const double ParcelLimitDrapeOffsetMillimeters = 150.0;

        /// <summary>
        /// 坵塊外框貼地形前的邊細分步距（公尺）。審查修正 5：25m 的坵塊邊只連 4 個角點貼地形，
        /// 中段地形起伏會被整段忽略（線段埋進或浮出地形），改成每邊先切成約 1m 一段再逐點貼地。
        /// </summary>
        private const double ParcelLimitDrapeSampleStepMeters = 1.0;

        /// <summary>單一樓板回應裡最多列出的 matchedCells 筆數；超過用 truncated 說明，面積合計仍為全量。</summary>
        private const int ParcelLimitMaxMatchedCellsPerFloorResponse = 500;

        /// <summary>疊圖實際畫線的坵塊格數上限；超過整批不畫（計算結果不受影響），回 warnings 說明原因。</summary>
        private const int ParcelLimitMaxOverlayCells = 300;

        /// <summary>
        /// 樓板「落在坵塊網格涵蓋範圍外」面積的警示門檻（平方公尺）。小於此值視為浮點／取樣誤差，
        /// 不值得警示；大於此值代表樓板很可能跨出了等高線範圍、或圖層／坵塊參數選錯。
        /// </summary>
        private const double ParcelLimitUncoveredAreaWarningThresholdSquareMeters = 1.0;

        /// <summary>
        /// 與 <see cref="RevitToposolidGradingAdapter"/> 判定「底面」的門檻一致（±15° 內視為水平底面），
        /// 用於偵測樓板是否有內環（孔洞）——只數環數，不重寫 ExtractBottomFootprint 的完整邏輯。
        /// </summary>
        private const double ParcelLimitBottomFaceNormalZMaximum = -0.965925826289068;

        /// <summary>
        /// 檢查整地方案控制樓板是否壓到坵塊法算出的限建坵塊（§262 的 30%／55% 兩段）。
        /// 只回報幾何事實（哪塊樓板、壓到哪幾格、各多少平方公尺、該格坡度與分級），
        /// <b>不下</b>「可否開發建築」之類終局結論——詳見回應 disclaimer 與 domain/parcel-slope-analysis.md。
        /// </summary>
        private object CheckGradingParcelLimits(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var drawOverlay = parameters["drawOverlay"]?.Value<bool?>() ?? false;
            var warnings = new List<string>();

            // 審查修正 6(a)：drawOverlay=true 時先確認方案視圖解析得到，不要等全部算完才報錯。
            View schemeView = null;
            if (drawOverlay)
            {
                schemeView = ResolveSchemeView(doc, parameters, record);
            }
            else if (HasExistingParcelLimitOverlay(doc, design.Id))
            {
                // 審查修正 11：沒有重畫，但文件上已有舊疊圖，提醒使用者畫面可能已過期。
                warnings.Add(
                    "drawOverlay=false：本次只計算未重畫，但偵測到文件上已有本方案先前建立的坵塊限建疊圖，"
                    + "畫面可能已過期（例如坵塊參數或樓板位置已變動）；若要更新疊圖請改用 drawOverlay=true 重新繪製。");
            }

            // 坵塊參數照抄坵塊法工具的參數名；視圖另外用 parcelViewId（讀 DWG 等高線的平面視圖），
            // 跟整地方案的 viewId（畫疊圖用的 3D 方案視圖）角色不同、刻意不共用參數名，
            // 避免 ReadParcelInput 與 ResolveSchemeView 搶同一個 "viewId" 鍵。
            var parcelParameters = new JObject
            {
                ["layerName"] = parameters["layerName"],
                ["contourIntervalMeters"] = parameters["contourIntervalMeters"],
                ["cellSizeMeters"] = parameters["cellSizeMeters"],
                ["rotationDegrees"] = parameters["rotationDegrees"],
                ["viewId"] = parameters["parcelViewId"],
            };

            ParcelInput parcelInput;
            try
            {
                parcelInput = ReadParcelInput(doc, parcelParameters);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("viewId"))
            {
                // 審查修正 3(a)：底層訊息教使用者填 "viewId"，但本工具該填的是 "parcelViewId"。
                throw new ArgumentException(RenameViewIdInErrorMessage(ex.Message));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("viewId"))
            {
                throw new InvalidOperationException(RenameViewIdInErrorMessage(ex.Message));
            }

            var parcelResult = ComputeParcelSlope(doc, parcelInput);

            var cellSummaries = parcelResult.Cells.Select(cell => new ParcelCellSummary
            {
                Label = cell.Label,
                CornersMeters = cell.Corners.Select(p => new Point2D(p.X, p.Y)).ToList(),
                SlopePercent = cell.SlopePercent,
                Restriction = cell.Restriction,
            }).ToList();
            var cellsByLabel = cellSummaries.ToDictionary(c => c.Label, c => c);

            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            if (floorIds.Count == 0)
            {
                throw new InvalidOperationException("方案沒有控制樓板，無法檢查限建範圍。");
            }

            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var floors = adapter.ValidateFloors(doc, floorIds);

            var floorResults = new List<ParcelFloorLimitResult>();
            var floorLoopsMeters = new Dictionary<long, List<Point2D>>();
            var touchedCellLabels = new HashSet<string>();
            foreach (var floor in floors)
            {
                long floorId = floor.Id.Value;

                // 審查修正 9：ExtractBottomFootprints 對非單一平面底面（斜板、編修過形狀的板）
                // 會丟例外，批次呼叫會讓整個命令中止。改成逐片呼叫、逐片捕捉，一片壞不拖垮其他片。
                IReadOnlyList<FloorFootprint> singleFootprint;
                try
                {
                    singleFootprint = adapter.ExtractBottomFootprints(new List<Floor> { floor });
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"樓板 {floorId}：無法取得輪廓（{ex.Message}），已略過，不計入面積統計。");
                    continue;
                }

                if (singleFootprint.Count == 0)
                {
                    warnings.Add($"樓板 {floorId}：取不到投影，已略過，不計入面積統計。");
                    continue;
                }

                var footprint = singleFootprint[0];

                // 審查修正 2：ExtractBottomFootprint 只取面積最大的那個環（忽略內孔）。
                // 不改 adapter 介面（會動到其他既有呼叫點），改為偵測並寫進 warnings。
                if (FloorHasInnerLoops(floor))
                {
                    warnings.Add(
                        $"樓板 {floorId}：偵測到內環（孔洞），目前工具只用外環計算，面積可能偏高，內孔面積未扣除。");
                }

                var loopMeters = footprint.OuterLoop
                    .Select(ParcelUnitConversion.FeetPointToMeters)
                    .ToList();
                var result = ParcelLimitCheck.CheckFloor(floorId, loopMeters, cellSummaries);
                floorResults.Add(result);
                floorLoopsMeters[floorId] = loopMeters;

                // 審查修正 1：格網只蓋等高線外接範圍，樓板落在範圍外／圖層選錯／該處無格子時，
                // 過去會靜默算 0。現在用 CoveredAreaSquareMeters 算出「未涵蓋面積」並提示。
                var uncovered = result.FloorAreaSquareMeters - result.CoveredAreaSquareMeters;
                if (uncovered > ParcelLimitUncoveredAreaWarningThresholdSquareMeters)
                {
                    warnings.Add(
                        $"樓板 {floorId}：有 {uncovered:0.00} m² 落在坵塊網格涵蓋範圍外"
                        + "（可能是圖層選錯、等高線範圍不足、坵塊參數與樓板位置不符，或樓板超出基地），"
                        + "未計入限建面積統計，請與 preview_parcel_slope 的格網範圍比對。");
                }

                foreach (var matched in result.MatchedCells)
                {
                    touchedCellLabels.Add(matched.CellLabel);
                }
            }

            List<long> createdElementIds = null;
            if (drawOverlay)
            {
                var labelTextSizeMm = parameters["labelTextSizeMm"]?.Value<double?>() ?? 3.0;
                createdElementIds = DrawParcelLimitOverlay(
                    doc, design, schemeView, labelTextSizeMm,
                    floorResults, floorLoopsMeters, cellsByLabel, touchedCellLabels, warnings);
            }

            return BuildParcelLimitResponse(
                design, parcelInput, parcelResult, drawOverlay, floorResults, createdElementIds, warnings);
        }

        private static string RenameViewIdInErrorMessage(string message) => message.Replace("viewId", "parcelViewId");

        /// <summary>偵測樓板底面是否有一個以上的邊界環（內環／孔洞）。只數環數，不重寫完整的輪廓抽取邏輯。</summary>
        private static bool FloorHasInnerLoops(Floor floor)
        {
            var geometry = floor.get_Geometry(new Options());
            if (geometry == null)
            {
                return false;
            }

            foreach (var geometryObject in geometry)
            {
                if (!(geometryObject is Solid solid) || solid.Volume <= 0)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    if (face is PlanarFace planarFace && planarFace.FaceNormal.Z < ParcelLimitBottomFaceNormalZMaximum)
                    {
                        var loops = planarFace.GetEdgesAsCurveLoops();
                        if (loops != null && loops.Count > 1)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>唯讀檢查：文件上是否已有本方案先前建立的限建疊圖線（供 drawOverlay=false 時提示過期用）。</summary>
        private static bool HasExistingParcelLimitOverlay(Document doc, ElementId designId)
        {
            var styleNames = new HashSet<string>
            {
                ParcelLimitLineStyleName(designId, ParcelRestriction.OpenSpaceOnly),
                ParcelLimitLineStyleName(designId, ParcelRestriction.ExcludedFromOpenSpace),
            };

            return new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .Any(curveElement => curveElement.LineStyle != null && styleNames.Contains(curveElement.LineStyle.Name));
        }

        /// <summary>
        /// 在方案 3D 視圖畫出被壓到的限建坵塊外框（貼地形 ModelCurve）＋每片觸及樓板一則文字摘要。
        /// 重跑冪等：先依線型子分類（依設計地形 ID 辨識）清掉上次疊圖與舊標籤，
        /// 不靠登記簿欄位、不升 schema（主線裁決 4）。
        /// 審查修正 6：view 由呼叫端先解析好傳入（不在算完才報錯）；線型子分類建立失敗會中止並回滾
        /// （不用預設線型畫，否則下次清不掉）；標籤清除改成全文件範圍（換 viewId 重跑也清得到）；
        /// 標籤建立失敗進 warnings，不吞掉。
        /// </summary>
        private List<long> DrawParcelLimitOverlay(
            Document doc,
            Toposolid design,
            View view,
            double labelTextSizeMm,
            List<ParcelFloorLimitResult> floorResults,
            Dictionary<long, List<Point2D>> floorLoopsMeters,
            Dictionary<string, ParcelCellSummary> cellsByLabel,
            HashSet<string> touchedCellLabels,
            List<string> warnings)
        {
            var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
            if (designSolids.Count == 0)
            {
                throw new InvalidOperationException("設計 Toposolid 沒有可用的實體幾何，無法貼地形畫疊圖。");
            }

            var designBox = design.get_BoundingBox(null);
            if (designBox == null)
            {
                throw new InvalidOperationException("取不到設計 Toposolid 的範圍盒。");
            }

            var rayBottomZ = designBox.Min.Z - 10.0;
            var rayTopZ = designBox.Max.Z + 10.0;
            var drapeOffset = UnitUtils.ConvertToInternalUnits(
                ParcelLimitDrapeOffsetMillimeters, UnitTypeId.Millimeters);
            var labelFallbackZ = designBox.Max.Z;

            var createdIds = new List<long>();
            using (var transaction = new Transaction(doc, "坵塊限建疊圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動坵塊限建疊圖交易。");
                }

                try
                {
                    ClearPreviousParcelLimitOverlay(doc, design.Id);
                    ClearPreviousParcelLimitLabels(doc, design.Id);

                    // 審查修正 6(b)：建立失敗就丟例外中止（外層 catch 會回滾），不要用預設線型畫下去——
                    // 否則下次重跑靠線型子分類名稱辨識清除時找不到、清不掉。
                    var openSpaceStyle = EnsureParcelLimitLineStyle(doc, design.Id, ParcelRestriction.OpenSpaceOnly);
                    var excludedStyle = EnsureParcelLimitLineStyle(doc, design.Id, ParcelRestriction.ExcludedFromOpenSpace);

                    // 審查修正 7：疊圖格數設上限，超過整批不畫（計算結果不受影響，仍是全量），回 warnings 說明。
                    if (touchedCellLabels.Count > ParcelLimitMaxOverlayCells)
                    {
                        warnings.Add(
                            $"本次有 {touchedCellLabels.Count} 格限建坵塊被壓到，超過疊圖上限 "
                            + $"{ParcelLimitMaxOverlayCells} 格，本次未繪製坵塊外框線（面積計算結果不受影響，仍為全量）；"
                            + "如需查看請縮小分析範圍或放大坵塊邊長後再試。");
                    }
                    else
                    {
                        foreach (var label in touchedCellLabels)
                        {
                            if (!cellsByLabel.TryGetValue(label, out var cell))
                            {
                                continue;
                            }

                            var lineStyle = cell.Restriction == ParcelRestriction.OpenSpaceOnly
                                ? openSpaceStyle
                                : excludedStyle;

                            // 審查修正 5：先細分邊再貼地，取樣不到的點用鄰近有效點回補（不是固定的地形最高點）。
                            var draped = DrapeLoopToDesignTerrain(cell.CornersMeters, designSolids, rayBottomZ, rayTopZ, drapeOffset);
                            if (draped == null)
                            {
                                warnings.Add($"坵塊 {label}：整格貼地形取樣失敗（可能落在地形範圍外），本次未繪製外框線。");
                                continue;
                            }

                            createdIds.AddRange(DrawDrapedCellLoop(doc, draped, lineStyle));
                        }
                    }

                    // 審查修正 4：疊圖子分類的跨方案可見性已併入既有共用函式 ApplyExtentLineCategoryVisibility
                    // （CommandExecutor.GradingExtentLines.cs），所有既有呼叫點自動一併受益，不必逐一改動。
                    ApplyExtentLineCategoryVisibility(doc, view, design.Id);

                    var textType = ResolveTextNoteType(doc, labelTextSizeMm);
                    var labelLift = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
                    var labelPrefix = ParcelLimitLabelPrefix(design.Id);
                    foreach (var result in floorResults)
                    {
                        if (result.MatchedCells.Count == 0
                            || !floorLoopsMeters.TryGetValue(result.FloorId, out var loopMeters))
                        {
                            continue;
                        }

                        // 審查修正 10：站位改用面積質心（退化／落在板外時退回邊內側點），不用頂點平均——
                        // U 形等極端凹多邊形的頂點平均常常落在板外。
                        Point2D anchorMeters;
                        try
                        {
                            anchorMeters = ParcelLimitCheck.ComputeLabelAnchor(loopMeters);
                        }
                        catch (ArgumentException)
                        {
                            continue; // 理論上不會發生（loopMeters 至少 3 點），防禦。
                        }

                        var anchorFeetX = UnitUtils.ConvertToInternalUnits(anchorMeters.X, UnitTypeId.Meters);
                        var anchorFeetY = UnitUtils.ConvertToInternalUnits(anchorMeters.Y, UnitTypeId.Meters);
                        var anchorZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                            designSolids, new Point2D(anchorFeetX, anchorFeetY), rayBottomZ, rayTopZ);
                        var labelPosition = new XYZ(
                            anchorFeetX, anchorFeetY, (anchorZ ?? labelFallbackZ) + drapeOffset + labelLift);

                        var text = labelPrefix + $"樓板 {result.FloorId}" + Environment.NewLine
                            + $"限空地：{result.OpenSpaceOnlyOverlapAreaSquareMeters:0.00} m²" + Environment.NewLine
                            + $"不得計入法定空地：{result.ExcludedOverlapAreaSquareMeters:0.00} m²";

                        try
                        {
                            var note = TextNote.Create(doc, view.Id, labelPosition, text,
                                new TextNoteOptions(textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Center });
                            if (note != null)
                            {
                                createdIds.Add(note.Id.Value);
                            }
                            else
                            {
                                // 審查修正 6(d)：建立失敗不可吞掉。
                                warnings.Add($"樓板 {result.FloorId}：建立限建面積標籤失敗（TextNote.Create 回傳 null）。");
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            warnings.Add($"樓板 {result.FloorId}：建立限建面積標籤失敗（{ex.Message}），不影響面積計算結果。");
                        }
                    }

                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("坵塊限建疊圖交易未能提交。");
                    }
                }
                catch
                {
                    if (transaction.HasStarted() && !transaction.HasEnded())
                    {
                        transaction.RollBack();
                    }

                    throw;
                }
            }

            return createdIds;
        }

        /// <summary>
        /// 把坵塊角點（公尺）先依 <see cref="ParcelLimitDrapeSampleStepMeters"/> 細分邊、再逐點貼設計地形
        /// 頂面＋淨空；取樣不到的點用環內鄰近有效點回補（不是固定的地形最高點），整圈都打不到時回傳 null
        /// （呼叫端判定整格不畫、計入 warnings）。做法比照 CreateGradingExtentLines 的貼地形手法，
        /// 但加了邊細分（審查修正 5：25m 坵塊邊只連 4 角點，中段地形起伏會被整段忽略）。
        /// </summary>
        private static List<XYZ> DrapeLoopToDesignTerrain(
            IReadOnlyList<Point2D> cornersMeters,
            IReadOnlyList<Solid> designSolids,
            double rayBottomZ,
            double rayTopZ,
            double drapeOffset)
        {
            var sampledMeters = new List<Point2D>();
            for (var i = 0; i < cornersMeters.Count; i++)
            {
                var start = cornersMeters[i];
                var end = cornersMeters[(i + 1) % cornersMeters.Count];
                sampledMeters.Add(start);

                var edgeLength = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
                var segments = Math.Max(1, (int)Math.Round(edgeLength / ParcelLimitDrapeSampleStepMeters));
                for (var step = 1; step < segments; step++)
                {
                    var t = (double)step / segments;
                    sampledMeters.Add(new Point2D(
                        start.X + (t * (end.X - start.X)), start.Y + (t * (end.Y - start.Y))));
                }
            }

            var feetXY = new (double X, double Y)[sampledMeters.Count];
            var sampledZ = new double?[sampledMeters.Count];
            for (var i = 0; i < sampledMeters.Count; i++)
            {
                var feetX = UnitUtils.ConvertToInternalUnits(sampledMeters[i].X, UnitTypeId.Meters);
                var feetY = UnitUtils.ConvertToInternalUnits(sampledMeters[i].Y, UnitTypeId.Meters);
                feetXY[i] = (feetX, feetY);
                sampledZ[i] = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                    designSolids, new Point2D(feetX, feetY), rayBottomZ, rayTopZ);
            }

            if (sampledZ.All(z => !z.HasValue))
            {
                return null; // 整圈都打不到地形。
            }

            FillWithNearestValidNeighbor(sampledZ);

            var draped = new List<XYZ>(sampledMeters.Count);
            for (var i = 0; i < sampledMeters.Count; i++)
            {
                draped.Add(new XYZ(feetXY[i].X, feetXY[i].Y, sampledZ[i].Value + drapeOffset));
            }

            return draped;
        }

        /// <summary>
        /// 環狀補值：先正向傳遞兩輪（處理環繞起點）、再反向傳遞一輪補收尾，一律用「鄰近的有效樣本」
        /// 回補缺值，不使用固定的地形最高點當後備（審查修正 5）。呼叫前已確認至少一個有效樣本。
        /// </summary>
        private static void FillWithNearestValidNeighbor(double?[] values)
        {
            var n = values.Length;
            double? lastKnown = null;
            for (var lap = 0; lap < 2; lap++)
            {
                for (var i = 0; i < n; i++)
                {
                    if (values[i].HasValue)
                    {
                        lastKnown = values[i];
                    }
                    else if (lastKnown.HasValue)
                    {
                        values[i] = lastKnown;
                    }
                }
            }

            lastKnown = null;
            for (var i = n - 1; i >= 0; i--)
            {
                if (values[i].HasValue)
                {
                    lastKnown = values[i];
                }
                else if (lastKnown.HasValue)
                {
                    values[i] = lastKnown;
                }
            }
        }

        /// <summary>
        /// 把貼地形的 3D 點環以模型線段連成環，套專屬線型子分類，回傳建立的線段元素 ID。
        /// 做法沿用 CreateGradingExtentLines 的 DrawDrapedLoops（每段建立含該段的草圖平面；
        /// 退化段容錯略過）。
        /// </summary>
        private static List<long> DrawDrapedCellLoop(Document doc, List<XYZ> loop, GraphicsStyle lineStyle)
        {
            var created = new List<long>();
            if (loop.Count < 2)
            {
                return created;
            }

            var minLength = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
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
                    var direction = (end - start).Normalize();
                    var normal = direction.CrossProduct(XYZ.BasisZ);
                    if (normal.GetLength() < 1e-9)
                    {
                        normal = direction.CrossProduct(XYZ.BasisX);
                    }

                    var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal.Normalize(), start));
                    var curve = doc.Create.NewModelCurve(Line.CreateBound(start, end), sketchPlane);
                    if (lineStyle != null)
                    {
                        curve.LineStyle = lineStyle;
                    }

                    created.Add(curve.Id.Value);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 退化或共點線段略過，不阻斷整個疊圖。
                }
            }

            return created;
        }

        /// <summary>「坵塊限建疊圖-{限制}-{設計地形ID}」專屬線型子分類名稱。</summary>
        private static string ParcelLimitLineStyleName(ElementId designId, ParcelRestriction restriction)
        {
            var suffix = restriction == ParcelRestriction.OpenSpaceOnly ? "限空地" : "不計入空地";
            return $"{ParcelLimitLineStylePrefix}-{suffix}-{designId.Value}";
        }

        /// <summary>疊圖標籤文字前綴（含方案 ID，供跨視圖清除時精確辨識、不誤刪別方案或使用者自己的文字）。</summary>
        private static string ParcelLimitLabelPrefix(ElementId designId) => $"【坵塊限建疊圖-{designId.Value}】";

        /// <summary>
        /// 確保「坵塊限建疊圖-{限制}-{方案}」子分類存在：限空地橘、不計入空地紅
        /// （色碼沿用 ApplyParcelColor 既有的分級配色）。冪等——已存在即校正顏色/線寬。
        /// 審查修正 6(b)：建立失敗一律丟例外（由呼叫端的交易回滾），不回傳 null 讓呼叫端改用預設線型畫——
        /// 否則下次重跑靠線型子分類名稱辨識清除時，這批線永遠清不掉。
        /// </summary>
        private static GraphicsStyle EnsureParcelLimitLineStyle(
            Document doc, ElementId designId, ParcelRestriction restriction)
        {
            var styleName = ParcelLimitLineStyleName(designId, restriction);
            var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category subCategory = null;
            foreach (Category candidate in linesCategory.SubCategories)
            {
                if (candidate.Name == styleName)
                {
                    subCategory = candidate;
                    break;
                }
            }

            try
            {
                if (subCategory == null)
                {
                    subCategory = doc.Settings.Categories.NewSubcategory(linesCategory, styleName);
                }

                subCategory.SetLineWeight(6, GraphicsStyleType.Projection);
                subCategory.LineColor = restriction == ParcelRestriction.OpenSpaceOnly
                    ? new Color(255, 127, 0)
                    : new Color(227, 26, 28);
                return subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new InvalidOperationException(
                    $"無法建立坵塊限建疊圖線型子分類「{styleName}」：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 清除本方案（designId）先前建立的限建疊圖線：依線型子分類名稱辨識（名稱含方案 ID），
        /// 不靠登記簿欄位（主線裁決 4）。全文件範圍找，因為 ModelCurve 不綁視圖。
        /// </summary>
        private static void ClearPreviousParcelLimitOverlay(Document doc, ElementId designId)
        {
            var styleNames = new HashSet<string>
            {
                ParcelLimitLineStyleName(designId, ParcelRestriction.OpenSpaceOnly),
                ParcelLimitLineStyleName(designId, ParcelRestriction.ExcludedFromOpenSpace),
            };

            var staleIds = new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .Where(curveElement => curveElement.LineStyle != null
                    && styleNames.Contains(curveElement.LineStyle.Name))
                .Select(curveElement => curveElement.Id)
                .ToList();
            if (staleIds.Count > 0)
            {
                doc.Delete(staleIds);
            }
        }

        /// <summary>
        /// 清除本方案先前建立的限建疊圖文字標籤：依「含方案 ID」的固定文字前綴辨識，範圍是<b>全文件</b>
        /// （審查修正 6(c)：換 viewId 重跑時，舊視圖裡的舊標籤也要清掉，不能只清目前視圖）。
        /// 前綴含方案 ID，不會誤刪別方案的疊圖標籤或使用者自己寫的文字。
        /// </summary>
        private static void ClearPreviousParcelLimitLabels(Document doc, ElementId designId)
        {
            var prefix = ParcelLimitLabelPrefix(designId);
            var staleIds = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNote))
                .Cast<TextNote>()
                .Where(note => note.Text != null && note.Text.StartsWith(prefix, StringComparison.Ordinal))
                .Select(note => note.Id)
                .ToList();
            if (staleIds.Count > 0)
            {
                doc.Delete(staleIds);
            }
        }

        private static object BuildParcelLimitResponse(
            Toposolid design,
            ParcelInput parcelInput,
            ParcelComputation parcelResult,
            bool drawOverlay,
            List<ParcelFloorLimitResult> floorResults,
            List<long> createdElementIds,
            List<string> warnings)
        {
            return new
            {
                designToposolidId = design.Id.Value,
                parcelViewId = parcelInput.View.Id.Value,
                parcelViewName = parcelInput.View.Name,
                layerName = parcelInput.LayerName,
                contourIntervalMeters = parcelInput.ContourIntervalMeters,
                cellSizeMeters = parcelInput.CellSizeMeters,
                rotationDegrees = parcelInput.RotationDegrees,
                drawOverlay,

                // 審查修正 1/3(b)：回傳本次坵塊法重算用的格網範圍與格數，供與 preview_parcel_slope
                // 當次的輸出比對，確認兩邊用的是同一份等高線/同一組參數。
                parcelSummary = new
                {
                    cellCount = parcelResult.Cells.Count,
                    contourCurveCount = parcelResult.ContourCurveCount,
                    gridBounds = new
                    {
                        minXMeters = Math.Round(parcelResult.Bounds.MinX, 3),
                        minYMeters = Math.Round(parcelResult.Bounds.MinY, 3),
                        maxXMeters = Math.Round(parcelResult.Bounds.MaxX, 3),
                        maxYMeters = Math.Round(parcelResult.Bounds.MaxY, 3),
                    },
                    counts = new
                    {
                        none = parcelResult.Cells.Count(c => c.Restriction == ParcelRestriction.None),
                        openSpaceOnly = parcelResult.Cells.Count(c => c.Restriction == ParcelRestriction.OpenSpaceOnly),
                        excluded = parcelResult.Cells.Count(c => c.Restriction == ParcelRestriction.ExcludedFromOpenSpace),
                    },
                },

                // 門檻一律引用既有常數，不另寫 30／55（見 ParcelSlopeMath）。
                thresholds = new
                {
                    steepThresholdPercent = ParcelSlopeMath.SteepThresholdPercent,
                    noOpenSpaceThresholdPercent = ParcelSlopeMath.NoOpenSpaceThresholdPercent,
                },

                floors = floorResults.Select(result =>
                {
                    var allMatched = result.MatchedCells;
                    var truncated = allMatched.Count > ParcelLimitMaxMatchedCellsPerFloorResponse;
                    var displayMatched = truncated
                        ? allMatched.Take(ParcelLimitMaxMatchedCellsPerFloorResponse).ToList()
                        : allMatched;

                    return new
                    {
                        floorId = result.FloorId,
                        floorAreaSquareMeters = Math.Round(result.FloorAreaSquareMeters, 2),
                        coveredAreaSquareMeters = Math.Round(result.CoveredAreaSquareMeters, 2),
                        uncoveredAreaSquareMeters = Math.Round(
                            Math.Max(0.0, result.FloorAreaSquareMeters - result.CoveredAreaSquareMeters), 2),
                        openSpaceOnlyOverlapAreaSquareMeters = Math.Round(result.OpenSpaceOnlyOverlapAreaSquareMeters, 2),
                        openSpaceOnlyOverlapFraction = result.FloorAreaSquareMeters > 0
                            ? Math.Round(result.OpenSpaceOnlyOverlapAreaSquareMeters / result.FloorAreaSquareMeters, 4)
                            : 0,
                        excludedOverlapAreaSquareMeters = Math.Round(result.ExcludedOverlapAreaSquareMeters, 2),
                        excludedOverlapFraction = result.FloorAreaSquareMeters > 0
                            ? Math.Round(result.ExcludedOverlapAreaSquareMeters / result.FloorAreaSquareMeters, 4)
                            : 0,
                        matchedCellCount = allMatched.Count,
                        truncated,
                        matchedCells = displayMatched.Select(cell => new
                        {
                            cellLabel = cell.CellLabel,
                            restriction = cell.Restriction.ToString(),
                            restrictionLabel = ParcelSlopeMath.RestrictionLabel(cell.Restriction),
                            slopePercent = Math.Round(cell.SlopePercent, 2),
                            overlapAreaSquareMeters = Math.Round(cell.OverlapAreaSquareMeters, 2),
                            overlapFractionOfFloor = Math.Round(cell.OverlapFractionOfFloor, 4),
                        }).ToList(),
                    };
                }).ToList(),

                createdElementIds,
                warnings,

                // 刻意寫死：本工具只回幾何事實，不下可否開發建築的結論。
                disclaimer = new[]
                {
                    "本結果僅標示整地方案樓板輪廓與坵塊法限建坵塊的幾何重疊面積，"
                        + "不等於「不得開發建築」或「不合格」的法律結論。",
                    "§262 第 1 項第 1 款要求先將原始地形「區劃成若干均質區」再判定但書是否成立，"
                        + "但書（區內最高低點間坡度 < 15% 且不含顯著獨立山頭或跨越主嶺線）"
                        + "需人工判讀地形，本工具不自動劃分均質區、不輸出可否開發建築之結論，"
                        + "詳見 domain/parcel-slope-analysis.md。",
                    "坵塊法計算為本次呼叫即時重算（結果不持久化）；若事後修改 DWG 等高線、"
                        + "搬移圖層、或調整坵塊參數（layerName／contourIntervalMeters／"
                        + "cellSizeMeters／rotationDegrees），結果會隨之改變，請以本次呼叫使用的參數為準。",
                    "本工具沿用控制樓板原始 ID（record.FloorIds）比對，"
                        + "尚未支援「樓板設計副本」模式；若方案改為該模式，比對樓板來源需另行確認。",
                    // 審查修正 8：坵塊法既有兩條品質提醒原文照搬（CommandExecutor.ParcelSlope.cs
                    // 的 BuildParcelResponse disclaimer），不要自己重寫措辭。
                    "§261 要求使用比例尺不小於 1/1200 之實測地形圖，工具無法驗證來源圖是否符合，請自行確認並記錄。",
                    "h 為等高線「首曲線」間距；若來源圖首曲線與計曲線同層，交點數會多計。",
                },
            };
        }
    }
}
#endif
