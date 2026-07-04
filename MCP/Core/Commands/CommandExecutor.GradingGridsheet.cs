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
        /// 方格法土方計算書：以固定格徑對原/設計地形雙射線取樣，輸出原GL、設計GL、
        /// 挖填深三張矩陣工作表與總表；方格法加總與 Revit 內建 CUT/FILL 並列、差額揭露
        /// （差額即方格離散誤差，方格法不取代正式報表值）。唯讀，不開交易。
        /// </summary>
        private object ExportEarthworkGridsheet(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var cellSizeMeters = parameters["cellSizeMeters"]?.Value<double?>() ?? 10.0;
            if (cellSizeMeters <= 0)
            {
                throw new ArgumentException("cellSizeMeters 必須大於 0。");
            }

            var outputPath = parameters["outputPath"]?.Value<string>();
            var schemeName = record.Value<string>("SchemeName") ?? "未命名方案";
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid originalToposolid))
            {
                throw new InvalidOperationException($"原地形 ID {originalId} 不存在或已被刪除。");
            }

            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(originalToposolid);
            var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                throw new InvalidOperationException("原地形或設計地形沒有可用的實體幾何。");
            }

            var designBox = design.get_BoundingBox(null);
            var originalBox = originalToposolid.get_BoundingBox(null);
            if (designBox == null || originalBox == null)
            {
                throw new InvalidOperationException("無法取得地形範圍盒。");
            }

            var rayBottomZ = Math.Min(designBox.Min.Z, originalBox.Min.Z) - 10.0;
            var rayTopZ = Math.Max(designBox.Max.Z, originalBox.Max.Z) + 10.0;
            var cellFeet = UnitUtils.ConvertToInternalUnits(cellSizeMeters, UnitTypeId.Meters);
            var minX = Math.Max(designBox.Min.X, originalBox.Min.X);
            var minY = Math.Max(designBox.Min.Y, originalBox.Min.Y);
            var maxX = Math.Min(designBox.Max.X, originalBox.Max.X);
            var maxY = Math.Min(designBox.Max.Y, originalBox.Max.Y);
            if (minX >= maxX || minY >= maxY)
            {
                throw new InvalidOperationException("原地形與設計地形沒有水平重疊範圍，無法建立方格。");
            }

            var columnCount = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellFeet));
            var rowCount = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellFeet));
            if (columnCount * rowCount > 40000)
            {
                throw new InvalidOperationException(
                    $"方格數 {columnCount * rowCount} 超過 40000 上限，請放大 cellSizeMeters。");
            }

            var cells = new List<GridCell>(columnCount * rowCount);
            var originalGL = new double?[rowCount, columnCount];
            var designGL = new double?[rowCount, columnCount];
            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < columnCount; column++)
                {
                    var center = new Point2D(
                        minX + ((column + 0.5) * cellFeet),
                        minY + ((row + 0.5) * cellFeet));
                    var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        originalSolids, center, rayBottomZ, rayTopZ);
                    var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        designSolids, center, rayBottomZ, rayTopZ);
                    double? depthMeters = null;
                    if (originalZ.HasValue && designZ.HasValue)
                    {
                        originalGL[row, column] = UnitUtils.ConvertFromInternalUnits(
                            originalZ.Value, UnitTypeId.Meters);
                        designGL[row, column] = UnitUtils.ConvertFromInternalUnits(
                            designZ.Value, UnitTypeId.Meters);
                        depthMeters = originalGL[row, column] - designGL[row, column];
                    }

                    cells.Add(new GridCell(row, column, depthMeters));
                }
            }

            var gridResult = EarthworkGrid.Compute(cells, cellSizeMeters * cellSizeMeters);
            var officialCut = record.Value<double?>("CutCubicMeters") ?? 0.0;
            var officialFill = record.Value<double?>("FillCubicMeters") ?? 0.0;

            if (string.IsNullOrEmpty(outputPath))
            {
                var projectPath = doc.PathName;
                var projectDir = string.IsNullOrEmpty(projectPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : System.IO.Path.GetDirectoryName(projectPath);
                outputPath = System.IO.Path.Combine(
                    projectDir, $"土方計算書_方格法_{schemeName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            }

            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                WriteGridMatrixSheet(workbook, "原地形GL", originalGL, rowCount, columnCount, null);
                WriteGridMatrixSheet(workbook, "設計GL", designGL, rowCount, columnCount, null);
                var depthMatrix = new double?[rowCount, columnCount];
                foreach (var cell in cells)
                {
                    depthMatrix[cell.Row, cell.Column] = cell.DepthMeters;
                }

                WriteGridMatrixSheet(workbook, "挖填深", depthMatrix, rowCount, columnCount, colorByCutFill: true);

                var summary = workbook.Worksheets.Add("總表");
                summary.Cell(1, 1).Value = "方格法土方計算總表";
                summary.Range(1, 1, 1, 4).Merge().Style.Font.SetBold().Font.SetFontSize(14);
                var rows = new (string Label, string Value)[]
                {
                    ("方案名稱", schemeName),
                    ("模型", record.Value<string>("DocumentTitle") ?? doc.Title),
                    ("方案時間", record.Value<string>("Timestamp") ?? string.Empty),
                    ("格徑 (m)", cellSizeMeters.ToString("0.##")),
                    ("方格數（有取樣/總數）", $"{gridResult.SampledCellCount} / {cells.Count}"),
                    ("方格法 CUT (m³)", gridResult.CutCubicMeters.ToString("#,##0.00")),
                    ("方格法 FILL (m³)", gridResult.FillCubicMeters.ToString("#,##0.00")),
                    ("Revit 正式 CUT (m³)", officialCut.ToString("#,##0.00")),
                    ("Revit 正式 FILL (m³)", officialFill.ToString("#,##0.00")),
                    ("CUT 差額 (m³ / %)", FormatDifference(gridResult.CutCubicMeters, officialCut)),
                    ("FILL 差額 (m³ / %)", FormatDifference(gridResult.FillCubicMeters, officialFill)),
                    ("差額性質", "方格離散誤差；正式土方量以 Revit 內建 CUT/FILL（登記簿）為準")
                };
                for (var index = 0; index < rows.Length; index++)
                {
                    summary.Cell(index + 2, 1).Value = rows[index].Label;
                    summary.Cell(index + 2, 1).Style.Font.SetBold();
                    summary.Cell(index + 2, 2).Value = rows[index].Value;
                }

                summary.ColumnsUsed().AdjustToContents();
                workbook.SaveAs(outputPath);
            }

            return new
            {
                OutputPath = outputPath,
                CellSizeMeters = cellSizeMeters,
                SampledCellCount = gridResult.SampledCellCount,
                GridCutCubicMeters = gridResult.CutCubicMeters,
                GridFillCubicMeters = gridResult.FillCubicMeters,
                OfficialCutCubicMeters = officialCut,
                OfficialFillCubicMeters = officialFill,
                Message = $"方格法土方計算書已輸出（{gridResult.SampledCellCount} 格取樣；"
                    + "方格法為呈現值，正式土方量以 Revit CUT/FILL 為準）。"
            };
        }

        private static string FormatDifference(double gridValue, double officialValue)
        {
            var difference = gridValue - officialValue;
            if (Math.Abs(officialValue) < 1e-9)
            {
                return $"{difference:+#,##0.00;-#,##0.00} / —";
            }

            return $"{difference:+#,##0.00;-#,##0.00} / {difference / officialValue * 100:+0.0;-0.0}%";
        }

        private static void WriteGridMatrixSheet(
            ClosedXML.Excel.XLWorkbook workbook,
            string sheetName,
            double?[,] matrix,
            int rowCount,
            int columnCount,
            bool? colorByCutFill)
        {
            var sheet = workbook.Worksheets.Add(sheetName);
            var cutBg = ClosedXML.Excel.XLColor.FromHtml("#FCE4EC");
            var fillBg = ClosedXML.Excel.XLColor.FromHtml("#E3F2FD");
            for (var column = 0; column < columnCount; column++)
            {
                sheet.Cell(1, column + 2).Value = $"C{column + 1}";
                sheet.Cell(1, column + 2).Style.Font.SetBold();
            }

            for (var row = 0; row < rowCount; row++)
            {
                // 列由南（minY）往北排；Excel 第一列放最北列，貼近平面圖方位。
                var matrixRow = rowCount - 1 - row;
                sheet.Cell(row + 2, 1).Value = $"R{matrixRow + 1}";
                sheet.Cell(row + 2, 1).Style.Font.SetBold();
                for (var column = 0; column < columnCount; column++)
                {
                    var value = matrix[matrixRow, column];
                    var cell = sheet.Cell(row + 2, column + 2);
                    if (!value.HasValue)
                    {
                        cell.Value = "—";
                        continue;
                    }

                    cell.Value = value.Value;
                    cell.Style.NumberFormat.SetFormat("0.00");
                    if (colorByCutFill == true)
                    {
                        if (value.Value > 0.005)
                        {
                            cell.Style.Fill.SetBackgroundColor(cutBg);
                        }
                        else if (value.Value < -0.005)
                        {
                            cell.Style.Fill.SetBackgroundColor(fillBg);
                        }
                    }
                }
            }

            sheet.SheetView.FreezeRows(1);
            sheet.SheetView.FreezeColumns(1);
        }
    }
}
#endif
