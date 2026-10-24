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

            var fitToCell = parameters["fitToCell"]?.Value<bool?>() ?? true;

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
                        if (fitToCell && doc.GetElement(viewElementId) is View placedView)
                        {
                            FitViewportToCell(doc, viewport, placedView,
                                (maxU - minU) / columns - margin, (maxV - minV) / rows - margin, center);
                        }

                        successCount++;
                        placed.Add(new
                        {
                            ViewId = viewIds[index],
                            ViewportId = viewport.Id.GetIdValue(),
                            Scale = (doc.GetElement(viewElementId) as View)?.Scale,
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
        /// 依格框可用尺寸自動調整視圖比例並置中：量測 viewport 外框 → 需求縮放比 →
        /// 視圖比例向上取標準比例 → 重新置中。圖表（Schedule）與透視視圖不適用，呼叫端排除。
        /// 修正 2026-07-06 實機缺陷：排圖不設比例導致視圖互相疊壓。
        /// </summary>
        private static void FitViewportToCell(
            Document doc, Viewport viewport, View view,
            double cellWidthFeet, double cellHeightFeet, XYZ center,
            bool snapToStandardScale = true, int? forceScale = null)
        {
            if (view is View3D perspective && perspective.IsPerspective)
            {
                return; // 透視圖比例無意義，僅置中。
            }

            try
            {
                doc.Regenerate();
                int targetScale;
                if (forceScale.HasValue)
                {
                    // 2026-07-10 六輪：C-00c 比較出圖同列（同案例類型）跨欄共用同一比例，
                    // 才是真正「同尺度可比較」——呼叫端已算好列內最大需求比例，這裡直接套用。
                    targetScale = forceScale.Value;
                }
                else
                {
                    var outline = viewport.GetBoxOutline();
                    var outlineWidth = outline.MaximumPoint.X - outline.MinimumPoint.X;
                    var outlineHeight = outline.MaximumPoint.Y - outline.MinimumPoint.Y;
                    if (outlineWidth <= 1e-9 || outlineHeight <= 1e-9 || view.Scale <= 0)
                    {
                        return;
                    }

                    var ratio = Math.Max(outlineWidth / cellWidthFeet, outlineHeight / cellHeightFeet);
                    // 2026-07-10 D6：C-00c 圖面撐滿格位——snap=false 用最小可容納整數比例（圖形最大化），
                    // 一般排圖維持標準比例。
                    targetScale = snapToStandardScale
                        ? RevitMCP.Core.Grading.SheetLayoutMath.SnapToStandardScale(view.Scale * ratio)
                        : Math.Max(1, (int)Math.Ceiling((view.Scale * ratio) - 1e-9));
                }

                if (targetScale != view.Scale)
                {
                    view.Scale = targetScale;
                    doc.Regenerate();
                }

                viewport.SetBoxCenter(center);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別視圖不支援改比例（例如鎖定狀態限制）時保持原樣，不讓整批失敗。
                viewport.SetBoxCenter(center);
            }
        }

        /// <summary>
        /// 2026-07-10 六輪：算出「這個 viewport 若要完整容納其內容，最小需要放大到幾比幾」，
        /// 但不套用——供呼叫端跨欄取最大值算出共用比例後再一次性套用（FitViewportToCell 的
        /// forceScale）。透視圖或退化外框回 null（呼叫端略過該格的比例投票，只置中）。
        /// </summary>
        private static int? ComputeRequiredScale(
            Document doc, Viewport viewport, View view, double cellWidthFeet, double cellHeightFeet)
        {
            if (view is View3D perspective && perspective.IsPerspective)
            {
                return null;
            }

            try
            {
                doc.Regenerate();
                var outline = viewport.GetBoxOutline();
                var outlineWidth = outline.MaximumPoint.X - outline.MinimumPoint.X;
                var outlineHeight = outline.MaximumPoint.Y - outline.MinimumPoint.Y;
                if (outlineWidth <= 1e-9 || outlineHeight <= 1e-9 || view.Scale <= 0)
                {
                    return null;
                }

                var ratio = Math.Max(outlineWidth / cellWidthFeet, outlineHeight / cellHeightFeet);
                return Math.Max(1, (int)Math.Ceiling((view.Scale * ratio) - 1e-9));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        /// <summary>
        /// PDF 匯出：把指定圖紙以 PDFExportOptions 批次匯出（Revit 2022+）；預設合併為單一 PDF。
        /// </summary>
        private object ExportSheetsPdf(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            ClearActiveSelection();
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

        private void ClearActiveSelection()
        {
            try
            {
                _uiApp.ActiveUIDocument?.Selection?.SetElementIds(new List<ElementId>());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 匯出時若目前文件狀態不允許改選取，不阻斷主流程。
            }
        }
    }
}
