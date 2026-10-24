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
        /// 整地圖說出圖協調：對登記簿每個方案，建方案視圖＋挖填熱區＋坡度熱區＋範圍線＋標高，
        /// 排上一張帶圖框的圖紙，最後批次匯出 PDF。逐方案／逐步容錯，單步失敗不整組中止。
        /// </summary>
        private object CreateGradingPresentation(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            ClearActiveSelection();
            var records = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse)
                .ToList();
            if (records.Count == 0)
            {
                throw new InvalidOperationException(
                    "目前模型沒有整地方案記錄；請先以 grade_toposolid_to_floors 建立方案。");
            }

            var filter = parameters["designToposolidIds"] as JArray;
            if (filter != null && filter.Count > 0)
            {
                var wanted = new HashSet<long>(filter.Values<long>());
                records = records.Where(record => wanted.Contains(record.Value<long>("DesignToposolidId"))).ToList();
                if (records.Count == 0)
                {
                    throw new InvalidOperationException("指定的 designToposolidIds 在登記簿中找不到對應方案。");
                }
            }

            var export = parameters["export"]?.Value<bool?>() ?? true;
            var outputPath = parameters["outputPath"]?.Value<string>();
            var titleBlock = ResolveGradingTitleBlock(doc, parameters, preferA0: true);

            var schemeResults = new List<object>();
            var sheetIds = new List<ElementId>();
            foreach (var record in records)
            {
                var designId = record.Value<long>("DesignToposolidId");
                var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
                Logger.Info($"[整地出圖] 方案開始：{schemeName}（design {designId}）");
                var steps = new List<string>();
                try
                {
                    var focus = TryComputeComparisonFocus(doc, new[] { record });
                    var mainViewId = EnsureSchemeView(doc, designId, record, steps);
                    var viewIds = new List<long> { mainViewId };

                    // 挖填／坡度熱區各需獨立視圖（AVF 一視圖一顯示樣式，同圖會互相覆蓋）。
                    // 複合流程取樣放寬 3m（單跑預設 2m）：UI 執行緒凍結時間減半、視覺差異可忽略。
                    var cutfillViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-挖填");
                    TrySubStep(steps, "挖填熱區", () =>
                        CreateCutfillHeatmap(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = cutfillViewId,
                            ["sampleStepMeters"] = 3.0
                        }));
                    viewIds.Add(cutfillViewId);

                    var slopeViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-坡度");
                    TrySubStep(steps, "坡度熱區", () =>
                        CreateSlopeHeatmap(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = slopeViewId,
                            ["sampleStepMeters"] = 3.0
                        }));
                    viewIds.Add(slopeViewId);
                    ApplyFocusSectionBoxToViewId(doc, mainViewId, focus);
                    ApplyFocusSectionBoxToViewId(doc, cutfillViewId, focus);
                    ApplyFocusSectionBoxToViewId(doc, slopeViewId, focus);

                    TrySubStep(steps, "整地範圍線", () =>
                        CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = mainViewId }));
                    // 2026-07-10 D2/D3/D11：與 C-00c 同規——daylight 合併標籤＋坡率文字。
                    // 三輪：挖填/坡度視圖也標關鍵高程（拉線到圖外）。
                    // 2026-07-11 十四輪：十一輪移除強制單側、十二輪加 CropBox 硬裁切後，
                    // C-00c 端實機重驗證實 3D 視圖的 CropBox 不會真的裁掉超出範圍的
                    // TextNote（只決定 viewport 外框）——中間夾縫互撞是結構性問題，跟這裡
                    // 一橫排三格緊鄰同理，恢復強制單側：最左格只往左、最右格只往右，
                    // 中間格（挖填）維持自動雙分（兩側都有鄰格，效果一樣）。
                    // 2026-07-11 十五輪追修：v15 實機重驗發現十四輪的「最左格強制單側」對
                    // mainViewId 是錯誤類比——mainViewId 同時標樓板＋daylight＋坡率（三種標籤
                    // 疊加，項目數是 (b)(c) 純 daylight 欄的近兩倍），AssignSideColumnSlots
                    // 帶 itemHeights 時「允許溢出、不硬壓縮」（見 AnnotationLayoutMathTests
                    // 的 StacksWithoutOverlapEvenWhenForcedOneSide 測試，此為刻意設計，壓縮會讓
                    // 間距小於實際文字框而重疊），強制單側把全部項目擠一邊，堆疊總高度溢出
                    // 裁切框下緣，撞進 Revit 視埠自動標題（"1 整地-...-排圖副本 / Scale:"）。
                    // C-00c 的 (a) 欄本來就是自動雙分且項目量相同，沒有這個問題；本欄與右側
                    // 挖填欄之間留白夠寬（一般網格排版，非 C-00c 緊鄰比較欄），改回自動雙分
                    // 把項目數腰斬到單側可負荷的範圍，不會撞進挖填欄的標籤。
                    // 十七輪：標註帶 labelScaleHint——一橫排三格（A0 假設）每格約 380×790mm，
                    // 槽位高度按預估出圖比例換算實際字高（詳見 EstimateLabelScaleHint 註解）。
                    var presentationHint = EstimateLabelScaleHint(focus, 380.0, 790.0);
                    TrySubStep(steps, "標高標註", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = mainViewId,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = true,
                            ["labelScaleHint"] = presentationHint
                        }));
                    TrySubStep(steps, "標高標註(挖填)", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = cutfillViewId,
                            ["annotateFloors"] = false,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = false,
                            ["labelScaleHint"] = presentationHint
                        }));
                    TrySubStep(steps, "標高標註(坡度)", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = slopeViewId,
                            ["annotateFloors"] = false,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = false,
                            ["labelSide"] = 1,
                            ["labelScaleHint"] = presentationHint
                        }));

                    var sheetId = CreatePresentationSheet(doc, titleBlock.TitleBlockId, $"整地-{schemeName}");
                    sheetIds.Add(sheetId);
                    // D8 修正：書籤視圖被比較出圖先佔時，複製一份（含標高標註）再排，不再上半頁留白。
                    var placeableViewIds = viewIds;
                    using (var transaction = new Transaction(doc, "出圖視圖佔用解套"))
                    {
                        GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                        transaction.Start();
                        if (doc.GetElement(sheetId) is ViewSheet sheetElement)
                        {
                            placeableViewIds = viewIds
                                .Select(id => ResolvePlaceableViewId(doc, sheetElement, id, steps, "方案視圖"))
                                .ToList();
                        }

                        transaction.Commit();
                    }

                    PlaceViewsOnSheet(new JObject
                    {
                        ["sheetId"] = sheetId.GetIdValue(),
                        ["viewIds"] = new JArray(placeableViewIds),
                        ["columns"] = 3,
                        ["rows"] = 1
                    });
                    steps.Add("排上圖紙");

                    schemeResults.Add(new
                    {
                        SchemeName = schemeName,
                        SheetId = sheetId.GetIdValue(),
                        ViewIds = viewIds,
                        Steps = steps,
                        Success = true
                    });
                }
                catch (Exception ex)
                {
                    schemeResults.Add(new
                    {
                        SchemeName = schemeName,
                        Success = false,
                        Error = ex.Message,
                        Steps = steps
                    });
                }
            }

            object pdf = null;
            if (export && sheetIds.Count > 0)
            {
                Logger.Info($"[整地出圖] PDF 匯出開始（{sheetIds.Count} 張圖紙）");
                try
                {
                    pdf = ExportSheetsPdf(new JObject
                    {
                        ["sheetIds"] = new JArray(sheetIds.Select(id => id.GetIdValue())),
                        ["outputPath"] = outputPath,
                        ["combine"] = true
                    });
                    Logger.Info("[整地出圖] PDF 匯出完成");
                }
                catch (Exception ex)
                {
                    pdf = new { Success = false, Error = ex.Message };
                    Logger.Info($"[整地出圖] PDF 匯出失敗—{ex.GetType().Name}: {ex.Message}");
                }
            }

            object excel = null;
            try
            {
                excel = ExportGradingComparison(new JObject
                {
                    ["outputPath"] = DeriveGradingExcelOutputPath(doc, parameters, "整地各別出圖比較表"),
                    ["includeScreenshots"] = true
                });
            }
            catch (Exception ex)
            {
                excel = new { Success = false, Error = ex.Message };
            }

            return new
            {
                SchemeCount = records.Count,
                SheetCount = sheetIds.Count,
                Schemes = schemeResults,
                Pdf = pdf,
                Excel = excel,
                UsedBlankTitleBlock = titleBlock.UsedBlankSheet,
                TitleBlockMessage = titleBlock.Message,
                Message = $"各別出圖完成：{sheetIds.Count} 張圖紙"
                    + (export ? "，已嘗試匯出 PDF" : string.Empty)
                    + "，已嘗試匯出 Excel。"
                    + (titleBlock.UsedBlankSheet ? " 本次使用空白圖紙。" : string.Empty)
            };
        }

        private long EnsureSchemeView(Document doc, long designId, JObject record, List<string> steps)
        {
            var viewId = record.Value<long?>("SchemeViewId") ?? 0;
            if (viewId > 0 && doc.GetElement(new ElementId(checked((int)viewId))) is View view)
            {
                using (var transaction = new Transaction(doc, "更新方案視圖顯示"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    transaction.Start();
                    var hideIds = CollectOtherTerrainIds(doc, new ElementId(checked((int)designId)))
                        .Concat(CollectOtherSchemeFloorIds(doc, record))
                        .Distinct()
                        .ToList();
                    var ownFloorIds = ReadRecordFloorIds(record)
                        .Select(id => new ElementId(checked((int)id)))
                        .ToList();
                    ApplySchemeVisibility(
                        doc,
                        view,
                        new ElementId(checked((int)designId)),
                        hideIds,
                        ownFloorIds);
                    ClearAnalysisVisualization(view);
                    transaction.Commit();
                }

                steps.Add("沿用方案視圖");
                return viewId;
            }

            CreateGradingSchemeView(new JObject { ["designToposolidId"] = designId, ["exportPng"] = true });
            var refreshed = RevitToposolidGradingAdapter.TryReadSchemeRecord(doc, designId);
            viewId = refreshed == null
                ? 0
                : JObject.Parse(refreshed).Value<long?>("SchemeViewId") ?? 0;
            if (viewId <= 0)
            {
                throw new InvalidOperationException("方案視圖建立失敗。");
            }

            steps.Add("建立方案視圖");
            return viewId;
        }

        private static long DuplicateSchemeView(Document doc, long sourceViewId, string baseName)
        {
            long newId;
            using (var transaction = new Transaction(doc, "複製方案視圖"))
            {
                transaction.Start();
                var source = (View)doc.GetElement(new ElementId(checked((int)sourceViewId)));
                var duplicateId = source.Duplicate(ViewDuplicateOption.Duplicate);
                var duplicate = (View)doc.GetElement(duplicateId);
                duplicate.Name = UniqueViewName(doc, baseName);
                ClearAnalysisVisualization(duplicate);
                if (duplicate is View3D view3d && view3d.IsSectionBoxActive)
                {
                    view3d.IsSectionBoxActive = false;
                }
                newId = duplicate.Id.GetIdValue();
                transaction.Commit();
            }

            return newId;
        }

        private static ElementId CreatePresentationSheet(Document doc, ElementId titleBlockId, string baseName)
        {
            ElementId sheetId;
            using (var transaction = new Transaction(doc, "建立整地圖說圖紙"))
            {
                transaction.Start();
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.Name = UniqueSheetName(doc, baseName);
                sheetId = sheet.Id;
                transaction.Commit();
            }

            return sheetId;
        }

        private static string UniqueSheetName(Document doc, string baseName)
        {
            baseName = GradingNames.SanitizeElementName(baseName);
            var existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(sheet => sheet.Name));
            if (!existing.Contains(baseName))
            {
                return baseName;
            }

            for (var suffix = 2; suffix < 1000; suffix++)
            {
                var candidate = $"{baseName}({suffix})";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }

            return baseName;
        }

        /// <summary>
        /// 容錯子步驟＋進度日誌（2026-07-08 實機教訓：複合命令在 UI 執行緒同步跑數分鐘、
        /// Revit 全程「未回應」且日誌零輸出，使用者以為當機強制關閉——每步起訖必須落日誌）。
        /// </summary>
        private void TrySubStep(List<string> steps, string label, Func<object> action)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info($"[整地出圖] 步驟開始：{label}");
            try
            {
                action();
                steps.Add(label);
                Logger.Info($"[整地出圖] 步驟完成：{label}（{stopwatch.ElapsedMilliseconds} ms）");
            }
            catch (Exception ex)
            {
                steps.Add($"{label}（略過：{ex.Message}）");
                Logger.Info($"[整地出圖] 步驟略過：{label}（{stopwatch.ElapsedMilliseconds} ms）"
                    + $"—{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
#endif
