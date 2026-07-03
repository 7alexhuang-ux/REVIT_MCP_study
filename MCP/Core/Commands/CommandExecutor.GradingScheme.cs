#if REVIT2024_OR_GREATER
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>列出模型內全部整地方案記錄（方案登記簿）。唯讀，不開交易。</summary>
        private object ListGradingSchemes(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var schemes = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse)
                .ToArray();
            return new
            {
                Count = schemes.Length,
                Schemes = schemes,
                Message = $"目前模型共有 {schemes.Length} 筆整地方案記錄。"
            };
        }

        /// <summary>
        /// 把方案登記簿匯出為 Excel 比較表（一列一方案；數值版，截圖嵌入屬後續階段）。
        /// 唯讀，不開交易。
        /// </summary>
        private object ExportGradingComparison(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var outputPath = parameters["outputPath"]?.Value<string>();
            var schemes = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse)
                .ToList();
            if (schemes.Count == 0)
            {
                throw new InvalidOperationException("目前模型沒有整地方案記錄。");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                var projectPath = doc.PathName;
                var projectDir = string.IsNullOrEmpty(projectPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : System.IO.Path.GetDirectoryName(projectPath);
                outputPath = System.IO.Path.Combine(
                    projectDir, $"整地方案比較_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            }

            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var headerBg = ClosedXML.Excel.XLColor.FromHtml("#4472C4");
                var altRowBg = ClosedXML.Excel.XLColor.FromHtml("#F2F2F2");
                var sheet = workbook.Worksheets.Add("方案比較");
                var headers = new[]
                {
                    "方案名稱", "時間", "模式", "Offset(m)", "坡度", "放坡上限(m)",
                    "原地形ID", "設計地形ID", "樓板IDs", "CUT(m³)", "FILL(m³)", "淨土方(m³)",
                    "最大挖深(m)", "最大填高(m)", "擾動面積(m²)", "警告"
                };
                for (var column = 0; column < headers.Length; column++)
                {
                    var cell = sheet.Cell(1, column + 1);
                    cell.Value = headers[column];
                    cell.Style.Fill.SetBackgroundColor(headerBg);
                    cell.Style.Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                    cell.Style.Font.SetBold();
                }

                void SetNumber(int rowIndex, int columnIndex, double? value)
                {
                    if (value.HasValue)
                    {
                        sheet.Cell(rowIndex, columnIndex).Value = value.Value;
                    }
                }

                for (var index = 0; index < schemes.Count; index++)
                {
                    var scheme = schemes[index];
                    var row = index + 2;
                    sheet.Cell(row, 1).Value = scheme.Value<string>("SchemeName");
                    sheet.Cell(row, 2).Value = scheme.Value<string>("Timestamp");
                    sheet.Cell(row, 3).Value = scheme.Value<string>("Mode");
                    SetNumber(row, 4, scheme.Value<double?>("OffsetDistanceMeters"));
                    sheet.Cell(row, 5).Value = scheme.Value<string>("SlopeRatio");
                    SetNumber(row, 6, scheme.Value<double?>("MaxExtensionMeters"));
                    sheet.Cell(row, 7).Value = scheme.Value<long?>("OriginalToposolidId") ?? 0;
                    sheet.Cell(row, 8).Value = scheme.Value<long?>("DesignToposolidId") ?? 0;
                    sheet.Cell(row, 9).Value = string.Join(", ",
                        (scheme["FloorIds"] as JArray)?.Select(idToken => idToken.ToString())
                            ?? Enumerable.Empty<string>());
                    SetNumber(row, 10, scheme.Value<double?>("CutCubicMeters"));
                    SetNumber(row, 11, scheme.Value<double?>("FillCubicMeters"));
                    SetNumber(row, 12, scheme.Value<double?>("NetCubicMeters"));
                    SetNumber(row, 13, scheme.Value<double?>("MaxCutDepthMeters"));
                    SetNumber(row, 14, scheme.Value<double?>("MaxFillHeightMeters"));
                    var disturbedArea = scheme.Value<double?>("DisturbedAreaSquareMeters");
                    var isApproximate = scheme.Value<bool?>("DisturbedAreaIsApproximate") ?? false;
                    sheet.Cell(row, 15).Value = disturbedArea.HasValue
                        ? (isApproximate
                            ? $"≈{disturbedArea.Value:#,##0.00}"
                            : disturbedArea.Value.ToString("#,##0.00"))
                        : string.Empty;
                    sheet.Cell(row, 16).Value = string.Join("；",
                        (scheme["Warnings"] as JArray)?.Select(warning => warning.ToString())
                            ?? Enumerable.Empty<string>());
                    if (index % 2 == 1)
                    {
                        sheet.Range(row, 1, row, headers.Length).Style.Fill.SetBackgroundColor(altRowBg);
                    }
                }

                var used = sheet.Range(1, 1, schemes.Count + 1, headers.Length);
                used.Style.Border.SetOutsideBorder(ClosedXML.Excel.XLBorderStyleValues.Thin);
                used.Style.Border.SetInsideBorder(ClosedXML.Excel.XLBorderStyleValues.Thin);
                sheet.Range(2, 10, schemes.Count + 1, 14).Style.NumberFormat.SetFormat("#,##0.00");
                sheet.SheetView.FreezeRows(1);
                sheet.ColumnsUsed().AdjustToContents();
                workbook.SaveAs(outputPath);
            }

            return new
            {
                OutputPath = outputPath,
                SchemeCount = schemes.Count,
                Message = $"整地方案比較表已匯出 {schemes.Count} 筆方案。"
            };
        }
    }
}
#endif
