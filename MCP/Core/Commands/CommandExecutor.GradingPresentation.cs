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
            var titleBlockId = ResolvePresentationTitleBlock(doc, parameters);

            var schemeResults = new List<object>();
            var sheetIds = new List<ElementId>();
            foreach (var record in records)
            {
                var designId = record.Value<long>("DesignToposolidId");
                var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
                var steps = new List<string>();
                try
                {
                    var mainViewId = EnsureSchemeView(doc, designId, record, steps);
                    var viewIds = new List<long> { mainViewId };

                    // 挖填／坡度熱區各需獨立視圖（AVF 一視圖一顯示樣式，同圖會互相覆蓋）。
                    var cutfillViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-挖填");
                    TrySubStep(steps, "挖填熱區", () =>
                        CreateCutfillHeatmap(new JObject { ["designToposolidId"] = designId, ["viewId"] = cutfillViewId }));
                    viewIds.Add(cutfillViewId);

                    var slopeViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-坡度");
                    TrySubStep(steps, "坡度熱區", () =>
                        CreateSlopeHeatmap(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));
                    viewIds.Add(slopeViewId);

                    TrySubStep(steps, "整地範圍線", () =>
                        CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = mainViewId }));
                    TrySubStep(steps, "標高標註", () =>
                        AnnotateGradingScheme(new JObject { ["designToposolidId"] = designId, ["viewId"] = mainViewId }));

                    var sheetId = CreatePresentationSheet(doc, titleBlockId, $"整地-{schemeName}");
                    sheetIds.Add(sheetId);
                    PlaceViewsOnSheet(new JObject
                    {
                        ["sheetId"] = sheetId.GetIdValue(),
                        ["viewIds"] = new JArray(viewIds),
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
                try
                {
                    pdf = ExportSheetsPdf(new JObject
                    {
                        ["sheetIds"] = new JArray(sheetIds.Select(id => id.GetIdValue())),
                        ["outputPath"] = outputPath,
                        ["combine"] = true
                    });
                }
                catch (Exception ex)
                {
                    pdf = new { Success = false, Error = ex.Message };
                }
            }

            return new
            {
                SchemeCount = records.Count,
                SheetCount = sheetIds.Count,
                Schemes = schemeResults,
                Pdf = pdf,
                Message = $"整地圖說出圖完成：{sheetIds.Count} 張圖紙"
                    + (export ? "，已嘗試匯出 PDF。" : "。")
            };
        }

        private long EnsureSchemeView(Document doc, long designId, JObject record, List<string> steps)
        {
            var viewId = record.Value<long?>("SchemeViewId") ?? 0;
            if (viewId > 0 && doc.GetElement(new ElementId(checked((int)viewId))) is View)
            {
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

        private static ElementId ResolvePresentationTitleBlock(Document doc, JObject parameters)
        {
            var explicitId = parameters["titleBlockId"]?.Value<long?>() ?? 0;
            if (explicitId > 0)
            {
                return new ElementId(checked((int)explicitId));
            }

            var titleBlock = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .FirstOrDefault();
            if (titleBlock == null)
            {
                throw new InvalidOperationException(
                    "模型沒有可用的圖框（TitleBlock）；請先載入圖框族群或提供 titleBlockId。");
            }

            return titleBlock.Id;
        }

        private void TrySubStep(List<string> steps, string label, Func<object> action)
        {
            try
            {
                action();
                steps.Add(label);
            }
            catch (Exception ex)
            {
                steps.Add($"{label}（略過：{ex.Message}）");
            }
        }
    }
}
#endif
