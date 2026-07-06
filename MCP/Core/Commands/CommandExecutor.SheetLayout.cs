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
        /// <summary>
        /// 自動排圖：把多個視圖以格狀排上一張圖紙（依圖框可用區計算格位中心）。
        /// 視圖已在其他圖紙者回報略過（一視圖僅能放一張圖紙）。
        /// </summary>
        private object PlaceViewsOnSheet(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType sheetId = parameters["sheetId"]?.Value<IdType>() ?? 0;
            if (sheetId == 0)
            {
                throw new Exception("請提供圖紙 ID (sheetId)");
            }

            var viewIdTokens = parameters["viewIds"] as JArray;
            if (viewIdTokens == null || viewIdTokens.Count == 0)
            {
                throw new Exception("請提供要放置的視圖 ID 清單 (viewIds)");
            }

            if (!(doc.GetElement(sheetId.ToElementId()) is ViewSheet sheet))
            {
                throw new Exception($"找不到圖紙 ID: {sheetId}");
            }

            var viewIds = viewIdTokens.Select(token => token.Value<IdType>()).ToList();
            int columns = parameters["columns"]?.Value<int?>() ?? 0;
            int rows = parameters["rows"]?.Value<int?>() ?? 0;
            if (columns <= 0 || rows <= 0)
            {
                columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(viewIds.Count)));
                rows = Math.Max(1, (int)Math.Ceiling((double)viewIds.Count / columns));
            }

            // 可用區取圖框外框；無圖框時以 A1 橫幅預設。
            double minU, minV, maxU, maxV;
            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();
            var titleBlockBox = titleBlock?.get_BoundingBox(sheet);
            if (titleBlockBox != null)
            {
                minU = titleBlockBox.Min.X;
                minV = titleBlockBox.Min.Y;
                maxU = titleBlockBox.Max.X;
                maxV = titleBlockBox.Max.Y;
            }
            else
            {
                minU = 0;
                minV = 0;
                maxU = UnitUtils.ConvertToInternalUnits(841, UnitTypeId.Millimeters);
                maxV = UnitUtils.ConvertToInternalUnits(594, UnitTypeId.Millimeters);
            }

            var margin = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Millimeters);
            minU += margin;
            minV += margin;
            maxU -= margin;
            maxV -= margin;

            var placed = new List<object>();
            var successCount = 0;
            using (var transaction = new Transaction(doc, "排視圖上圖紙"))
            {
                transaction.Start();
                for (var index = 0; index < viewIds.Count; index++)
                {
                    var column = index % columns;
                    var row = index / columns;
                    var centerU = minU + ((maxU - minU) * (column + 0.5) / columns);
                    var centerV = maxV - ((maxV - minV) * (row + 0.5) / rows); // 由上而下排列
                    var center = new XYZ(centerU, centerV, 0);
                    var viewElementId = viewIds[index].ToElementId();
                    try
                    {
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewElementId))
                        {
                            placed.Add(new
                            {
                                ViewId = viewIds[index],
                                Success = false,
                                Error = "視圖無法加入此圖紙（可能已放置於其他圖紙或類型不支援）"
                            });
                            continue;
                        }

                        var viewport = Viewport.Create(doc, sheet.Id, viewElementId, center);
                        successCount++;
                        placed.Add(new
                        {
                            ViewId = viewIds[index],
                            ViewportId = viewport.Id.GetIdValue(),
                            Success = true
                        });
                    }
                    catch (Exception ex)
                    {
                        placed.Add(new { ViewId = viewIds[index], Success = false, Error = ex.Message });
                    }
                }

                transaction.Commit();
            }

            return new
            {
                SheetId = sheet.Id.GetIdValue(),
                Columns = columns,
                Rows = rows,
                Placed = placed,
                Message = $"已於圖紙排入 {successCount} 個視圖（{columns}×{rows} 格）。"
            };
        }

        /// <summary>
        /// PDF 匯出：把指定圖紙以 PDFExportOptions 批次匯出（Revit 2022+）；預設合併為單一 PDF。
        /// </summary>
        private object ExportSheetsPdf(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            var sheetIdTokens = parameters["sheetIds"] as JArray;
            if (sheetIdTokens == null || sheetIdTokens.Count == 0)
            {
                throw new Exception("請提供要匯出的圖紙 ID 清單 (sheetIds)");
            }

            var combine = parameters["combine"]?.Value<bool?>() ?? true;
            var outputPath = parameters["outputPath"]?.Value<string>();

            var sheetIds = new List<ElementId>();
            foreach (var token in sheetIdTokens)
            {
                var elementId = token.Value<IdType>().ToElementId();
                if (doc.GetElement(elementId) is ViewSheet)
                {
                    sheetIds.Add(elementId);
                }
            }

            if (sheetIds.Count == 0)
            {
                throw new Exception("清單中沒有有效的圖紙。");
            }

            string directory;
            string fileName;
            if (string.IsNullOrEmpty(outputPath))
            {
                var projectPath = doc.PathName;
                directory = string.IsNullOrEmpty(projectPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : Path.GetDirectoryName(projectPath);
                fileName = $"整地圖說_{DateTime.Now:yyyyMMdd_HHmmss}";
            }
            else
            {
                directory = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new Exception("outputPath 必須包含資料夾路徑。");
                }

                fileName = Path.GetFileNameWithoutExtension(outputPath);
            }

            Directory.CreateDirectory(directory);
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '_');
            }

            var options = new PDFExportOptions
            {
                Combine = combine,
                FileName = fileName
            };

            var succeeded = doc.Export(directory, sheetIds, options);
            if (!succeeded)
            {
                throw new InvalidOperationException(
                    "PDF 匯出未成功；若環境限制 PDFExportOptions，可改用 Revit 內建列印為 PDF。");
            }

            return new
            {
                OutputDirectory = directory,
                FileName = combine ? $"{fileName}.pdf" : "(逐張，各圖紙自動命名)",
                SheetCount = sheetIds.Count,
                Combined = combine,
                Message = $"已匯出 {sheetIds.Count} 張圖紙為 PDF（{(combine ? "合併單檔" : "逐張")}）：{directory}"
            };
        }
    }
}
