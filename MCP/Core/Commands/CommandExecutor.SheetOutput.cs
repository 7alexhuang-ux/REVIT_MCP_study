using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const double SheetMillimetersPerFoot = 304.8;

        /// <summary>
        /// 批次將一般 View 以 Viewport 放到圖紙上。已在同一張圖紙上的視圖可選擇只移動，
        /// 已放在其他圖紙且 Revit 不允許重複放置時則回報失敗，不會刪除既有視埠。
        /// </summary>
        private object PlaceViewsOnSheets(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            var placements = parameters?["placements"] as JArray;
            bool moveExisting = parameters?["moveExisting"]?.Value<bool>() ?? false;

            if (placements == null || placements.Count == 0)
                throw new Exception("請提供 placements，至少包含一組 sheetId 與 viewId");

            var results = new List<object>();
            int createdCount = 0;
            int movedCount = 0;

            using (Transaction transaction = new Transaction(doc, "批次放置視圖到圖紙"))
            {
                transaction.Start();

                foreach (JToken item in placements)
                {
                    IdType sheetIdValue = item["sheetId"]?.Value<IdType>() ?? 0;
                    IdType viewIdValue = item["viewId"]?.Value<IdType>() ?? 0;

                    try
                    {
                        if (sheetIdValue == 0 || viewIdValue == 0)
                            throw new Exception("sheetId 與 viewId 都必須是有效的 Element ID");

                        ElementId sheetId = sheetIdValue.ToElementId();
                        ElementId viewId = viewIdValue.ToElementId();
                        ViewSheet sheet = doc.GetElement(sheetId) as ViewSheet;
                        View view = doc.GetElement(viewId) as View;

                        if (sheet == null)
                            throw new Exception($"找不到圖紙 ID: {sheetIdValue}");
                        if (view == null || view is ViewSheet || view.IsTemplate)
                            throw new Exception($"找不到可放置的視圖 ID: {viewIdValue}");

                        XYZ target = ResolveViewportCenter(doc, sheet, item);
                        Viewport existing = sheet.GetAllViewports()
                            .Select(id => doc.GetElement(id) as Viewport)
                            .FirstOrDefault(vp => vp != null && vp.ViewId == viewId);

                        if (existing != null)
                        {
                            if (moveExisting)
                            {
                                existing.SetBoxCenter(target);
                                movedCount++;
                                results.Add(new
                                {
                                    Success = true,
                                    Action = "Moved",
                                    SheetId = sheetIdValue,
                                    SheetNumber = sheet.SheetNumber,
                                    ViewId = viewIdValue,
                                    ViewName = view.Name,
                                    ViewportId = existing.Id.GetIdValue(),
                                    Xmm = target.X * SheetMillimetersPerFoot,
                                    Ymm = target.Y * SheetMillimetersPerFoot
                                });
                            }
                            else
                            {
                                results.Add(new
                                {
                                    Success = true,
                                    Action = "Skipped",
                                    Reason = "視圖已在此圖紙上",
                                    SheetId = sheetIdValue,
                                    SheetNumber = sheet.SheetNumber,
                                    ViewId = viewIdValue,
                                    ViewName = view.Name,
                                    ViewportId = existing.Id.GetIdValue()
                                });
                            }
                            continue;
                        }

                        if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
                            throw new Exception("Revit 不允許將此視圖放到目標圖紙；視圖可能已放在其他圖紙，或類型不支援 Viewport");

                        Viewport viewport = Viewport.Create(doc, sheetId, viewId, target);
                        createdCount++;
                        results.Add(new
                        {
                            Success = true,
                            Action = "Created",
                            SheetId = sheetIdValue,
                            SheetNumber = sheet.SheetNumber,
                            ViewId = viewIdValue,
                            ViewName = view.Name,
                            ViewportId = viewport.Id.GetIdValue(),
                            Xmm = target.X * SheetMillimetersPerFoot,
                            Ymm = target.Y * SheetMillimetersPerFoot
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            Success = false,
                            SheetId = sheetIdValue,
                            ViewId = viewIdValue,
                            Error = ex.Message
                        });
                    }
                }

                transaction.Commit();
            }

            int failedCount = results.Count - results.Count(r =>
            {
                var property = r.GetType().GetProperty("Success");
                return property != null && (bool)property.GetValue(r);
            });

            return new
            {
                Success = failedCount == 0,
                Total = placements.Count,
                CreatedCount = createdCount,
                MovedCount = movedCount,
                FailedCount = failedCount,
                Results = results
            };
        }

        private XYZ ResolveViewportCenter(Document doc, ViewSheet sheet, JToken placement)
        {
            JToken xToken = placement["xMm"];
            JToken yToken = placement["yMm"];
            if (xToken != null && yToken != null)
            {
                return new XYZ(
                    xToken.Value<double>() / SheetMillimetersPerFoot,
                    yToken.Value<double>() / SheetMillimetersPerFoot,
                    0);
            }

            BoundingBoxXYZ combined = null;
            var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element titleBlock in titleBlocks)
            {
                BoundingBoxXYZ box = titleBlock.get_BoundingBox(sheet);
                if (box == null) continue;
                if (combined == null)
                {
                    combined = new BoundingBoxXYZ { Min = box.Min, Max = box.Max };
                }
                else
                {
                    combined.Min = new XYZ(
                        Math.Min(combined.Min.X, box.Min.X),
                        Math.Min(combined.Min.Y, box.Min.Y),
                        0);
                    combined.Max = new XYZ(
                        Math.Max(combined.Max.X, box.Max.X),
                        Math.Max(combined.Max.Y, box.Max.Y),
                        0);
                }
            }

            if (combined == null)
                return XYZ.Zero;

            return new XYZ(
                (combined.Min.X + combined.Max.X) / 2.0,
                (combined.Min.Y + combined.Max.Y) / 2.0,
                0);
        }

        /// <summary>
        /// 匯出指定圖紙為 PDF / DWG。匯出本身不修改 Revit 模型，也不需要 Transaction。
        /// </summary>
        private object ExportSheets(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string outputFolder = parameters?["outputFolder"]?.Value<string>();
            bool combinePdf = parameters?["combinePdf"]?.Value<bool>() ?? true;
            bool overwrite = parameters?["overwrite"]?.Value<bool>() ?? false;
            string combinedPdfName = parameters?["combinedPdfName"]?.Value<string>() ?? "Structural_Review.pdf";
            string dwgSetupName = parameters?["dwgSetupName"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(outputFolder) || !Path.IsPathRooted(outputFolder))
                throw new Exception("outputFolder 必須是絕對路徑");

            HashSet<string> formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var formatArray = parameters?["formats"] as JArray;
            if (formatArray == null || formatArray.Count == 0)
            {
                formats.Add("pdf");
                formats.Add("dwg");
            }
            else
            {
                foreach (JToken token in formatArray)
                    formats.Add(token.Value<string>() ?? string.Empty);
            }

            if (formats.Any(f => f != "pdf" && f != "dwg"))
                throw new Exception("formats 僅支援 pdf、dwg");

            List<ViewSheet> sheets = ResolveExportSheets(doc, parameters);
            if (sheets.Count == 0)
                throw new Exception("找不到要匯出的圖紙；請提供 sheetIds 或 sheetNumbers");

            Directory.CreateDirectory(outputFolder);
            var results = new List<object>();

            if (formats.Contains("pdf"))
            {
                if (combinePdf)
                {
                    string safePdfBaseName = Path.GetFileNameWithoutExtension(SanitizeSheetOutputFileName(combinedPdfName));
                    string pdfPath = Path.Combine(outputFolder, safePdfBaseName + ".pdf");
                    if (File.Exists(pdfPath) && !overwrite)
                    {
                        results.Add(new { Format = "PDF", Success = false, Skipped = true, File = pdfPath, Error = "檔案已存在；請設定 overwrite=true" });
                    }
                    else
                    {
                        if (File.Exists(pdfPath)) File.Delete(pdfPath);
                        PDFExportOptions options = new PDFExportOptions
                        {
                            Combine = true,
                            FileName = safePdfBaseName,
                            StopOnError = false
                        };
                        bool exported = doc.Export(outputFolder, sheets.Select(s => s.Id).ToList(), options);
                        results.Add(new { Format = "PDF", Success = exported && File.Exists(pdfPath), File = pdfPath, SheetCount = sheets.Count });
                    }
                }
                else
                {
                    foreach (ViewSheet sheet in sheets)
                    {
                        string pdfBaseName = Path.GetFileNameWithoutExtension(BuildSheetBaseName(sheet));
                        string pdfPath = Path.Combine(outputFolder, pdfBaseName + ".pdf");
                        if (File.Exists(pdfPath) && !overwrite)
                        {
                            results.Add(new { Format = "PDF", Success = false, Skipped = true, SheetNumber = sheet.SheetNumber, File = pdfPath, Error = "檔案已存在；請設定 overwrite=true" });
                            continue;
                        }
                        if (File.Exists(pdfPath)) File.Delete(pdfPath);
                        PDFExportOptions options = new PDFExportOptions
                        {
                            Combine = true,
                            FileName = pdfBaseName,
                            StopOnError = false
                        };
                        bool exported = doc.Export(outputFolder, new List<ElementId> { sheet.Id }, options);
                        results.Add(new { Format = "PDF", Success = exported && File.Exists(pdfPath), SheetNumber = sheet.SheetNumber, File = pdfPath });
                    }
                }
            }

            if (formats.Contains("dwg"))
            {
                DWGExportOptions dwgOptions = string.IsNullOrWhiteSpace(dwgSetupName)
                    ? new DWGExportOptions()
                    : DWGExportOptions.GetPredefinedOptions(doc, dwgSetupName);

                if (dwgOptions == null)
                    throw new Exception($"找不到 DWG 匯出設定: {dwgSetupName}");

                dwgOptions.MergedViews = false;
                foreach (ViewSheet sheet in sheets)
                {
                    string baseName = BuildSheetBaseName(sheet);
                    string expectedPath = Path.Combine(outputFolder, baseName + ".dwg");
                    if (File.Exists(expectedPath) && !overwrite)
                    {
                        results.Add(new { Format = "DWG", Success = false, Skipped = true, SheetNumber = sheet.SheetNumber, File = expectedPath, Error = "檔案已存在；請設定 overwrite=true" });
                        continue;
                    }
                    if (File.Exists(expectedPath)) File.Delete(expectedPath);

                    bool exported = doc.Export(
                        outputFolder,
                        baseName,
                        new List<ElementId> { sheet.Id },
                        dwgOptions);

                    string[] produced = Directory.GetFiles(outputFolder, baseName + "*.dwg", SearchOption.TopDirectoryOnly);
                    results.Add(new
                    {
                        Format = "DWG",
                        Success = exported && produced.Length > 0,
                        SheetNumber = sheet.SheetNumber,
                        Files = produced
                    });
                }
            }

            return new
            {
                Success = results.All(r =>
                {
                    var property = r.GetType().GetProperty("Success");
                    return property != null && (bool)property.GetValue(r);
                }),
                OutputFolder = outputFolder,
                SheetCount = sheets.Count,
                Formats = formats.OrderBy(f => f).ToArray(),
                Results = results
            };
        }

        private List<ViewSheet> ResolveExportSheets(Document doc, JObject parameters)
        {
            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();
            var resolved = new Dictionary<IdType, ViewSheet>();

            var sheetIds = parameters?["sheetIds"] as JArray;
            if (sheetIds != null)
            {
                foreach (JToken token in sheetIds)
                {
                    IdType value = token.Value<IdType>();
                    ViewSheet sheet = doc.GetElement(value.ToElementId()) as ViewSheet;
                    if (sheet == null) throw new Exception($"找不到圖紙 ID: {value}");
                    resolved[sheet.Id.GetIdValue()] = sheet;
                }
            }

            var sheetNumbers = parameters?["sheetNumbers"] as JArray;
            if (sheetNumbers != null)
            {
                foreach (JToken token in sheetNumbers)
                {
                    string number = token.Value<string>();
                    ViewSheet sheet = allSheets.FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
                    if (sheet == null) throw new Exception($"找不到圖紙編號: {number}");
                    resolved[sheet.Id.GetIdValue()] = sheet;
                }
            }

            return resolved.Values.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private string BuildSheetBaseName(ViewSheet sheet)
        {
            return SanitizeSheetOutputFileName($"{sheet.SheetNumber}_{sheet.Name}");
        }

        private string SanitizeSheetOutputFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "export" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                result = result.Replace(c, '_');
            return result.TrimEnd('.', ' ');
        }

        private string EnsureExtension(string fileName, string extension)
        {
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName + extension;
        }
    }
}
