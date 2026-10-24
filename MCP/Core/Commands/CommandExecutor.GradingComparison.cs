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
        private const string ComparisonScheduleName = "整地方案比較表";

        /// <summary>建立整地方案比較明細表：Toposolid 類別、欄位=共用參數、過濾方案名有值。
        /// 2026-07-10 使用者裁決：內建明細表必須建好維護（找得到既有就沿用，不重複建）。</summary>
        private static ViewSchedule EnsureComparisonSchedule(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(candidate => !candidate.IsTemplate
                    && candidate.Name.StartsWith(ComparisonScheduleName, StringComparison.Ordinal));
            if (existing != null)
            {
                return existing;
            }

            var schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_Toposolid));
            schedule.Name = UniqueViewName(doc, ComparisonScheduleName);
            var definition = schedule.Definition;

            var schedulable = definition.GetSchedulableFields();
            ScheduleField nameField = null;
            foreach (var parameterName in GradingSharedParameters.OrderedNames)
            {
                var field = schedulable.FirstOrDefault(
                    candidate => candidate.GetName(doc) == parameterName);
                if (field == null)
                {
                    continue; // 未綁定的欄位跳過（EnsureBindings 應先跑）
                }

                var added = definition.AddField(field);
                added.ColumnHeading = FriendlyComparisonHeading(parameterName);
                if (parameterName == "RevitMCP方案名")
                {
                    nameField = added;
                }
            }

            if (nameField == null)
            {
                throw new InvalidOperationException(
                    "明細表缺「RevitMCP方案名」欄位；請先執行共用參數綁定。");
            }

            definition.AddFilter(new ScheduleFilter(
                nameField.FieldId, ScheduleFilterType.HasValue));
            definition.AddSortGroupField(new ScheduleSortGroupField(nameField.FieldId));
            return schedule;
        }

        private static string FriendlyComparisonHeading(string parameterName)
        {
            switch (parameterName)
            {
                case "RevitMCP方案名": return "方案名";
                case "RevitMCP模式": return "模式";
                case "RevitMCP參數": return "參數";
                case "RevitMCP挖方m3": return "挖方\nm3";
                case "RevitMCP填方m3": return "填方\nm3";
                case "RevitMCP淨土方m3": return "淨土方\nm3\n+餘/-缺";
                case "RevitMCP挖填總量m3": return "挖填\n總量\nm3";
                case "RevitMCP最大挖深m": return "最大\n挖深m";
                case "RevitMCP最大填高m": return "最大\n填高m";
                case "RevitMCP擾動面積m2": return "擾動\n面積m2";
                case "RevitMCP運土鬆方m3": return "運土\n鬆方m3";
                case "RevitMCP所需自然方m3": return "所需\n自然方m3";
                case "RevitMCP實質淨土方m3": return "實質\n淨土方\nm3\n+餘/-缺";
                default: return parameterName.Replace("RevitMCP", string.Empty);
            }
        }

        /// <summary>把明細表放上圖紙（topLeft 為圖紙座標、feet）。</summary>
        private static ScheduleSheetInstance PlaceScheduleOnSheet(
            Document doc, ViewSheet sheet, ViewSchedule schedule, XYZ topLeft)
        {
            var existing = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .FirstOrDefault(instance => instance.ScheduleId == schedule.Id);
            if (existing != null)
            {
                doc.Delete(existing.Id); // 重排：舊實例移除重放
            }

            return ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, topLeft);
        }

        /// <summary>比較表 13 欄寬度權重（自訂排版與內建明細表欄寬共用同一組）。</summary>
        private static readonly double[] ComparisonColumnWeights =
            { 1.7, 1.0, 1.1, 0.9, 0.9, 1.05, 0.95, 0.9, 0.9, 1.0, 0.95, 0.95, 1.1 };

        /// <summary>內建明細表欄寬：按共用權重分掉指定總寬（feet）。</summary>
        private static void ApplyComparisonScheduleColumnWidths(ViewSchedule schedule, double totalWidth)
        {
            var definition = schedule.Definition;
            var fieldCount = definition.GetFieldCount();
            var weightSum = ComparisonColumnWeights.Sum();
            for (var index = 0; index < fieldCount; index++)
            {
                var weight = index < ComparisonColumnWeights.Length
                    ? ComparisonColumnWeights[index]
                    : 1.0;
                try
                {
                    definition.GetField(index).GridColumnWidth = totalWidth * weight / weightSum;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別欄不允許設寬（如計算欄）時略過。
                }
            }
        }

        private static readonly string[] ComparisonTableHeaders =
        {
            "方案名", "模式", "參數",
            "挖方\nm3", "填方\nm3", "淨土方\nm3\n+餘/-缺", "挖填\n總量\nm3",
            "最大\n挖深\nm", "最大\n填高\nm", "擾動\n面積\nm2",
            "運土\n鬆方\nm3", "所需\n自然方\nm3", "實質\n淨土方\nm3\n+餘/-缺"
        };

        // 2026-07-10 D7：fit-to-content 常數——與 SheetLayoutMath.ComputeFitScheduleHeight 的
        // 呼叫端參數一致（表頭 7.5mm／內文 9mm／標題帶 20mm／列留白 3.5mm）。
        private const double ComparisonHeaderTextMm = 7.5;
        private const double ComparisonBodyTextMm = 9.0;
        private const double ComparisonTitleBandMm = 20.0;
        private const double ComparisonRowPaddingMm = 3.5;

        private static string[] BuildComparisonRowValues(JObject record)
        {
            var cut = record.Value<double?>("CutCubicMeters") ?? 0.0;
            var fill = record.Value<double?>("FillCubicMeters") ?? 0.0;
            var ledger = record["Ledger"] as JObject;
            var requiredBank = ledger?.Value<double?>("RequiredBankForFillCubicMeters") ?? 0.0;
            return new[]
            {
                WrapSchemeNameForTable(record.Value<string>("SchemeName") ?? string.Empty),
                WrapModeForTable(record.Value<string>("Mode") ?? string.Empty),
                WrapParameterForTable(DescribeComparisonParameters(record)),
                FormatNumber(cut),
                FormatNumber(fill),
                FormatSignedNumber(cut - fill),
                FormatNumber(cut + fill),
                FormatNumber(record.Value<double?>("MaxCutDepthMeters") ?? 0.0),
                FormatNumber(record.Value<double?>("MaxFillHeightMeters") ?? 0.0),
                FormatNumber(record.Value<double?>("DisturbedAreaSquareMeters") ?? 0.0),
                FormatNumber(ledger?.Value<double?>("HaulVolumeLooseCubicMeters") ?? 0.0),
                FormatNumber(requiredBank),
                FormatSignedNumber(cut - requiredBank)
            };
        }

        private static int MaxLineCount(IReadOnlyList<string> values)
        {
            return values.Max(value => (value ?? string.Empty).Split('\n').Length);
        }

        private static void DrawComparisonTableOnSheet(
            Document doc, ViewSheet sheet, IReadOnlyList<JObject> records,
            XYZ topLeft, double width, double height, double textScale = 1.0)
        {
            if (records.Count == 0)
            {
                return;
            }

            // 2026-07-10 D7 使用者裁決：列高剛好容納文字，省下高度讓給上方圖區。
            // 字級隨圖紙縮放（textScale 須與 ComputeFitScheduleHeight 呼叫端一致）。
            var headerTextMm = ComparisonHeaderTextMm * textScale;
            var bodyTextMm = ComparisonBodyTextMm * textScale;
            var rowPaddingMm = ComparisonRowPaddingMm * textScale;
            var titleTypeId = EnsureScaledTextNoteType(doc, "整地比較表-標題", 10.0, textScale);
            var titleHeight = UnitUtils.ConvertToInternalUnits(
                ComparisonTitleBandMm * textScale, UnitTypeId.Millimeters);
            var tableTop = topLeft.Y - titleHeight;
            TextNote.Create(doc, sheet.Id,
                new XYZ(topLeft.X + (width / 2), topLeft.Y - UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Millimeters), 0),
                "整地方案比較表", new TextNoteOptions
                {
                    TypeId = titleTypeId,
                    HorizontalAlignment = HorizontalTextAlignment.Center
                });

            var headers = ComparisonTableHeaders;
            var weights = ComparisonColumnWeights;
            var weightSum = weights.Sum();
            var rowsContent = records.Select(BuildComparisonRowValues).ToList();

            var headerTypeId = EnsureScaledTextNoteType(doc, "整地比較表-表頭大字", ComparisonHeaderTextMm, textScale);
            var bodyTypeId = EnsureScaledTextNoteType(doc, "整地比較表-數值大字", ComparisonBodyTextMm, textScale);
            var columns = new double[headers.Length + 1];
            columns[0] = topLeft.X;
            for (var i = 0; i < headers.Length; i++)
            {
                columns[i + 1] = columns[i] + (width * weights[i] / weightSum);
            }
            columns[headers.Length] = topLeft.X + width;

            // 2026-07-11 十七輪（使用者回報六：兩行以上文字幾乎都超出表格）：
            // 舊做法用「行數×字高×1.55」估文字塊高度定列高與置中，但微軟正黑的實際行距
            // 大於 1.55×字高，估短的量隨行數累積，兩行折行就壓線、表頭三四行直接跨列。
            // 改為自校準三段式：①先在暫定位置建立全部儲存格文字 → ②Regenerate 後量每個
            // TextNote 的實際高度，列高＝該列最高文字＋上下留白 → ③按實測列界把文字
            // 移到正中（Coord 為文字塊頂緣，置中＝列中心＋實高/2）。不再依賴字體行距常數。
            var pendingTexts = new List<(TextNote Note, int RowIndex, double X)>();
            TextNote CreateCellText(ElementId typeId, double leftEdge, double rightEdge, string text, bool center)
            {
                var x = center
                    ? (leftEdge + rightEdge) / 2
                    : leftEdge + UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Millimeters);
                return TextNote.Create(doc, sheet.Id, new XYZ(x, tableTop, 0), text ?? string.Empty,
                    new TextNoteOptions
                    {
                        TypeId = typeId,
                        HorizontalAlignment = center ? HorizontalTextAlignment.Center : HorizontalTextAlignment.Left
                    });
            }

            for (var column = 0; column < headers.Length; column++)
            {
                var note = CreateCellText(headerTypeId, columns[column], columns[column + 1], headers[column], center: true);
                pendingTexts.Add((note, -1, note.Coord.X));
            }

            for (var index = 0; index < rowsContent.Count; index++)
            {
                var values = rowsContent[index];
                for (var column = 0; column < values.Length; column++)
                {
                    var note = CreateCellText(bodyTypeId, columns[column], columns[column + 1],
                        values[column], center: column > 0);
                    pendingTexts.Add((note, index, note.Coord.X));
                }
            }

            doc.Regenerate();

            var rowPadding = UnitUtils.ConvertToInternalUnits(rowPaddingMm, UnitTypeId.Millimeters);
            double MeasuredRowHeight(int rowIndex, double fallbackTextMm)
            {
                var heights = pendingTexts
                    .Where(entry => entry.RowIndex == rowIndex)
                    .Select(entry => entry.Note.Height)
                    .Where(h => h > 1e-9)
                    .ToList();
                var textBlock = heights.Count > 0
                    ? heights.Max()
                    : UnitUtils.ConvertToInternalUnits(fallbackTextMm * 1.9, UnitTypeId.Millimeters);
                return textBlock + (2 * rowPadding);
            }

            var headerRowHeight = MeasuredRowHeight(-1, headerTextMm);
            var rowHeights = Enumerable.Range(0, rowsContent.Count)
                .Select(index => MeasuredRowHeight(index, bodyTextMm))
                .ToList();

            var bottomY = tableTop - headerRowHeight - rowHeights.Sum();
            DrawTableGroupBackgrounds(doc, sheet, columns, tableTop, bottomY);

            var rowBoundaries = new List<double> { tableTop, tableTop - headerRowHeight };
            foreach (var rowHeight in rowHeights)
            {
                rowBoundaries.Add(rowBoundaries[rowBoundaries.Count - 1] - rowHeight);
            }

            foreach (var y in rowBoundaries)
            {
                DrawSheetDetailLine(doc, sheet, new XYZ(topLeft.X, y, 0), new XYZ(topLeft.X + width, y, 0));
            }

            for (var column = 0; column <= headers.Length; column++)
            {
                DrawSheetDetailLine(doc, sheet, new XYZ(columns[column], tableTop, 0), new XYZ(columns[column], bottomY, 0));
            }

            foreach (var boundary in new[] { 3, 7, 10 })
            {
                DrawSheetDetailLine(doc, sheet, new XYZ(columns[boundary], tableTop, 0), new XYZ(columns[boundary], bottomY, 0));
                DrawSheetDetailLine(doc, sheet,
                    new XYZ(columns[boundary] + UnitUtils.ConvertToInternalUnits(0.6, UnitTypeId.Millimeters), tableTop, 0),
                    new XYZ(columns[boundary] + UnitUtils.ConvertToInternalUnits(0.6, UnitTypeId.Millimeters), bottomY, 0));
            }

            foreach (var (note, rowIndex, x) in pendingTexts)
            {
                var rowTop = rowBoundaries[rowIndex + 1];
                var rowBottom = rowBoundaries[rowIndex + 2];
                var centerY = (rowTop + rowBottom) / 2;
                try
                {
                    note.Coord = new XYZ(x, centerY + (note.Height / 2), 0);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別文字移動失敗維持暫定位置，不阻斷出圖。
                }
            }
        }

        private static string WrapSchemeNameForTable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var normalized = name.Trim();
            var validationIndex = normalized.IndexOf("驗證", StringComparison.Ordinal);
            if (validationIndex > 0)
            {
                return normalized.Substring(0, validationIndex).TrimEnd('-', ' ', '_')
                    + "\n" + normalized.Substring(validationIndex);
            }

            var dashIndex = normalized.LastIndexOf('-');
            if (dashIndex > 0 && dashIndex < normalized.Length - 1)
            {
                return normalized.Substring(0, dashIndex) + "\n" + normalized.Substring(dashIndex + 1);
            }

            return normalized;
        }

        private static string WrapModeForTable(string mode)
        {
            return string.IsNullOrWhiteSpace(mode)
                ? string.Empty
                : mode.Trim().Replace("_", "\n");
        }

        private static string WrapParameterForTable(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace(" ", "\n");
        }

        private static string DescribeComparisonParameters(JObject record)
        {
            var mode = record.Value<string>("Mode");
            if (mode == "offset_transition")
            {
                return $"外延 {record.Value<double?>("OffsetDistanceMeters") ?? 0:0.##}m";
            }

            if (mode == "slope_transition")
            {
                return $"坡率 {record.Value<string>("SlopeRatio") ?? "?"}";
            }

            return "直貼";
        }

        private static string FormatNumber(double value)
        {
            return Math.Round(value, 2).ToString("#,##0.00");
        }

        private static string FormatSignedNumber(double value)
        {
            var rounded = Math.Round(value, 2);
            if (rounded > 0)
            {
                return "+" + rounded.ToString("#,##0.00");
            }

            return rounded.ToString("#,##0.00");
        }

        private static void DrawSheetDetailLine(Document doc, ViewSheet sheet, XYZ start, XYZ end)
        {
            if (start.DistanceTo(end) < 1e-6)
            {
                return;
            }

            doc.Create.NewDetailCurve(sheet, Line.CreateBound(start, end));
        }

        private static void DrawSheetStyledLine(
            Document doc, ViewSheet sheet, XYZ start, XYZ end, GraphicsStyle style)
        {
            if (start.DistanceTo(end) < 1e-6)
            {
                return;
            }

            var curve = doc.Create.NewDetailCurve(sheet, Line.CreateBound(start, end));
            if (style != null)
            {
                curve.LineStyle = style;
            }
        }

        private static void DrawSheetRectangleOutline(
            Document doc, ViewSheet sheet, XYZ min, XYZ max, GraphicsStyle style)
        {
            DrawSheetStyledLine(doc, sheet, new XYZ(min.X, min.Y, 0), new XYZ(max.X, min.Y, 0), style);
            DrawSheetStyledLine(doc, sheet, new XYZ(max.X, min.Y, 0), new XYZ(max.X, max.Y, 0), style);
            DrawSheetStyledLine(doc, sheet, new XYZ(max.X, max.Y, 0), new XYZ(min.X, max.Y, 0), style);
            DrawSheetStyledLine(doc, sheet, new XYZ(min.X, max.Y, 0), new XYZ(min.X, min.Y, 0), style);
        }

        private static void DrawRowLabelCard(
            Document doc, ViewSheet sheet, LayoutCell cell, double originX, double topY,
            string label, ElementId textTypeId, Color accentColor)
        {
            var minHeight = UnitUtils.ConvertToInternalUnits(14.0, UnitTypeId.Millimeters);
            var cardHeight = Math.Min(
                Math.Max(minHeight, cell.Height * 0.34),
                UnitUtils.ConvertToInternalUnits(22.0, UnitTypeId.Millimeters));
            var verticalCenter = topY - cell.Y - (cell.Height / 2.0);
            var yMax = verticalCenter + (cardHeight / 2.0);
            var yMin = verticalCenter - (cardHeight / 2.0);
            var xMin = originX + cell.X + UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var xMax = originX + cell.X + cell.Width - UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            var border = EnsureSheetLineStyle(doc, "整地標籤卡-外框", new Color(205, 213, 222), 1);
            var accent = EnsureSheetLineStyle(
                doc,
                $"整地標籤卡-色條-{accentColor.Red}-{accentColor.Green}-{accentColor.Blue}",
                accentColor,
                5);
            DrawSheetRectangleOutline(doc, sheet, new XYZ(xMin, yMin, 0), new XYZ(xMax, yMax, 0), border);
            DrawSheetStyledLine(doc, sheet,
                new XYZ(xMin + UnitUtils.ConvertToInternalUnits(2.2, UnitTypeId.Millimeters), yMin, 0),
                new XYZ(xMin + UnitUtils.ConvertToInternalUnits(2.2, UnitTypeId.Millimeters), yMax, 0),
                accent);

            var lineCount = Math.Max(1, (label ?? string.Empty).Split('\n').Length);
            var textX = xMin + UnitUtils.ConvertToInternalUnits(6.0, UnitTypeId.Millimeters);
            var textY = verticalCenter + UnitUtils.ConvertToInternalUnits(lineCount > 1 ? 5.4 : 3.2, UnitTypeId.Millimeters);
            TextNote.Create(doc, sheet.Id, new XYZ(textX, textY, 0), label, new TextNoteOptions
            {
                TypeId = textTypeId,
                HorizontalAlignment = HorizontalTextAlignment.Left
            });
        }

        private static void DrawTableGroupBackgrounds(
            Document doc, ViewSheet sheet, double[] columns, double tableTop, double bottomY)
        {
            var groups = new[]
            {
                (Start: 3, End: 7, Color: new Color(232, 242, 255)),
                (Start: 7, End: 10, Color: new Color(236, 248, 236)),
                (Start: 10, End: 13, Color: new Color(255, 246, 229))
            };

            foreach (var group in groups)
            {
                var typeId = EnsureSectionRegionType(doc, $"整地表格分組-{group.Start}", group.Color);
                DrawSheetFilledRectangle(
                    doc, sheet, typeId,
                    new XYZ(columns[group.Start], bottomY, 0),
                    new XYZ(columns[group.End], tableTop, 0));
            }
        }

        private static void DrawSheetFilledRectangle(
            Document doc, ViewSheet sheet, ElementId regionTypeId, XYZ min, XYZ max)
        {
            if (regionTypeId == ElementId.InvalidElementId)
            {
                return;
            }

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(new XYZ(min.X, min.Y, 0), new XYZ(max.X, min.Y, 0)));
            loop.Append(Line.CreateBound(new XYZ(max.X, min.Y, 0), new XYZ(max.X, max.Y, 0)));
            loop.Append(Line.CreateBound(new XYZ(max.X, max.Y, 0), new XYZ(min.X, max.Y, 0)));
            loop.Append(Line.CreateBound(new XYZ(min.X, max.Y, 0), new XYZ(min.X, min.Y, 0)));
            try
            {
                FilledRegion.Create(doc, regionTypeId, sheet.Id, new List<CurveLoop> { loop });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別樣板若不允許在圖紙上放 filled region，就略過淡色背景，不阻斷出圖。
                if (doc.GetElement(regionTypeId) is FilledRegionType regionType)
                {
                    DrawSheetHatchedRectangle(doc, sheet, regionType.ForegroundPatternColor, min, max);
                }
            }
        }

        private static void DrawSheetHatchedRectangle(Document doc, ViewSheet sheet, Color color, XYZ min, XYZ max)
        {
            var width = Math.Abs(max.X - min.X);
            var height = Math.Abs(max.Y - min.Y);
            if (width < 1e-6 || height < 1e-6)
            {
                return;
            }

            var style = EnsureSheetLineStyle(doc, $"整地淡色底-{color.Red}-{color.Green}-{color.Blue}", color);
            var spacing = UnitUtils.ConvertToInternalUnits(height < UnitUtils.ConvertToInternalUnits(8.0, UnitTypeId.Millimeters)
                ? 0.45
                : 1.6, UnitTypeId.Millimeters);
            for (var y = min.Y + spacing; y < max.Y - (spacing / 2); y += spacing)
            {
                var curve = doc.Create.NewDetailCurve(sheet, Line.CreateBound(
                    new XYZ(min.X, y, 0),
                    new XYZ(max.X, y, 0)));
                if (style != null)
                {
                    curve.LineStyle = style;
                }
            }
        }

        /// <summary>2026-07-10 D12：坡度圖例改畫進版面數學保留的 LegendCell（(b)(c) 卡下方），
        /// 尺寸縮小、行距依格高自動壓縮，不再與標籤卡互疊。</summary>
        private static void DrawSlopeLegendOnSheet(
            Document doc, ViewSheet sheet, LayoutCell legendCell, double originX, double topY,
            double mmScale = 1.0)
        {
            var inset = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
            var panelLeft = originX + legendCell.X + inset;
            var panelRight = originX + legendCell.X + legendCell.Width - inset;
            var panelTop = topY - legendCell.Y - inset;
            var panelBottom = topY - legendCell.Y - legendCell.Height + inset;
            if (panelBottom >= panelTop)
            {
                return; // 保留區退化（極端小圖紙），略過圖例不阻斷出圖。
            }

            var border = EnsureSheetLineStyle(doc, "整地坡度圖例-外框", new Color(205, 213, 222), 1);
            DrawSheetRectangleOutline(doc, sheet,
                new XYZ(panelLeft, panelBottom, 0),
                new XYZ(panelRight, panelTop, 0),
                border);

            // 2026-07-10 二輪：行距/字級隨圖紙縮放；空間不足時「字跟著行距縮」而不是互疊。
            var titleSize = 6.2 * mmScale;
            var titleTypeId = EnsureScaledTextNoteType(doc, "整地坡度圖例標題", 6.2, mmScale);
            var x = panelLeft + UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters);
            var titleY = panelTop - UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
            TextNote.Create(doc, sheet.Id, new XYZ(x, titleY, 0), "坡度分級圖例", new TextNoteOptions
            {
                TypeId = titleTypeId,
                HorizontalAlignment = HorizontalTextAlignment.Left
            });

            var listTop = titleY - UnitUtils.ConvertToInternalUnits(titleSize + 3.0, UnitTypeId.Millimeters);
            var available = listTop - panelBottom - UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
            var rowStep = Math.Min(
                UnitUtils.ConvertToInternalUnits(9.0 * mmScale, UnitTypeId.Millimeters),
                available / Math.Max(1, SlopeClassifier.Classes.Count));
            var rowStepMm = UnitUtils.ConvertFromInternalUnits(rowStep, UnitTypeId.Millimeters);
            var textSizeMm = Math.Max(2.5, Math.Min(5.4 * mmScale, rowStepMm / 1.5));
            var textTypeId = EnsureTextNoteType(doc, $"整地坡度圖例文字-{Math.Round(textSizeMm, 1):0.#}", textSizeMm);
            var swatch = Math.Min(
                UnitUtils.ConvertToInternalUnits(6.0 * mmScale, UnitTypeId.Millimeters),
                rowStep * 0.75);
            for (var index = 0; index < SlopeClassifier.Classes.Count; index++)
            {
                var slopeClass = SlopeClassifier.Classes[index];
                var rowY = listTop - (index * rowStep);
                var typeId = EnsureSectionRegionType(
                    doc,
                    $"整地坡度圖例-{slopeClass.Level}",
                    new Color((byte)slopeClass.Red, (byte)slopeClass.Green, (byte)slopeClass.Blue));
                DrawSheetFilledRectangle(
                    doc, sheet, typeId,
                    new XYZ(x, rowY - swatch, 0),
                    new XYZ(x + swatch, rowY, 0));
                TextNote.Create(doc, sheet.Id,
                    new XYZ(x + swatch + UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters), rowY, 0),
                    $"{slopeClass.Level}. {slopeClass.RangeText}",
                    new TextNoteOptions
                    {
                        TypeId = textTypeId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    });
            }
        }

        /// <summary>D10：(d) 剖面標籤卡下方的挖/填小圖例（紅=挖、藍=填、虛線=原地形）。</summary>
        private static void DrawCutFillLegendChip(
            Document doc, ViewSheet sheet, LayoutCell sectionLabelCell, double originX, double topY,
            double mmScale = 1.0)
        {
            var textTypeId = EnsureScaledTextNoteType(doc, "整地坡度圖例文字", 5.4, mmScale);
            var x = originX + sectionLabelCell.X + UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Millimeters);
            // 標籤卡垂直置中（DrawRowLabelCard 同式），圖例排在卡下緣以下。
            var cardHeight = Math.Min(
                Math.Max(UnitUtils.ConvertToInternalUnits(14.0, UnitTypeId.Millimeters), sectionLabelCell.Height * 0.34),
                UnitUtils.ConvertToInternalUnits(22.0, UnitTypeId.Millimeters));
            var cardBottom = topY - sectionLabelCell.Y - (sectionLabelCell.Height / 2.0) - (cardHeight / 2.0);
            var swatch = UnitUtils.ConvertToInternalUnits(6.0 * mmScale, UnitTypeId.Millimeters);
            var rowStep = UnitUtils.ConvertToInternalUnits(9.0 * mmScale, UnitTypeId.Millimeters);
            var entries = new[]
            {
                (Label: "挖方", Color: new Color(220, 45, 45)),
                (Label: "填方", Color: new Color(0, 102, 204))
            };
            for (var index = 0; index < entries.Length; index++)
            {
                var rowY = cardBottom - UnitUtils.ConvertToInternalUnits(4.0, UnitTypeId.Millimeters) - (index * rowStep);
                var typeId = EnsureSectionRegionType(doc, $"整地剖面圖例-{entries[index].Label}", entries[index].Color);
                DrawSheetFilledRectangle(
                    doc, sheet, typeId,
                    new XYZ(x, rowY - swatch, 0),
                    new XYZ(x + swatch, rowY, 0));
                TextNote.Create(doc, sheet.Id,
                    new XYZ(x + swatch + UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters), rowY, 0),
                    entries[index].Label, new TextNoteOptions
                    {
                        TypeId = textTypeId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    });
            }

            var dashY = cardBottom - UnitUtils.ConvertToInternalUnits(4.0, UnitTypeId.Millimeters) - (entries.Length * rowStep);
            var dashStyle = EnsureDashedOriginalProfileStyle(doc);
            DrawSheetStyledLine(doc, sheet,
                new XYZ(x, dashY - (swatch / 2), 0),
                new XYZ(x + swatch, dashY - (swatch / 2), 0),
                dashStyle);
            TextNote.Create(doc, sheet.Id,
                new XYZ(x + swatch + UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters), dashY, 0),
                "原地形(虛線)", new TextNoteOptions
                {
                    TypeId = textTypeId,
                    HorizontalAlignment = HorizontalTextAlignment.Left
                });

            // 十七輪（使用者回報三）：剖面輪廓全面改為同源繪製後，實線＝整地後地形，
            // 圖例補上這一項，虛實兩種線都有名字。
            var solidY = dashY - rowStep;
            var solidStyle = EnsureSolidDesignProfileStyle(doc);
            DrawSheetStyledLine(doc, sheet,
                new XYZ(x, solidY - (swatch / 2), 0),
                new XYZ(x + swatch, solidY - (swatch / 2), 0),
                solidStyle);
            TextNote.Create(doc, sheet.Id,
                new XYZ(x + swatch + UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters), solidY, 0),
                "整地後(實線)", new TextNoteOptions
                {
                    TypeId = textTypeId,
                    HorizontalAlignment = HorizontalTextAlignment.Left
                });
        }

        /// <summary>縮放字級的文字型式：型式名帶字級（如「整地欄大標-7.1」），
        /// 避免同名型式被改字級而動到既有圖紙外觀。</summary>
        private static ElementId EnsureScaledTextNoteType(
            Document doc, string baseName, double baseSizeMillimeters, double scale)
        {
            var size = Math.Round(baseSizeMillimeters * scale, 1);
            return EnsureTextNoteType(doc, $"{baseName}-{size:0.#}", size);
        }

        private static ElementId EnsureTextNoteType(
            Document doc, string name, double sizeMillimeters, bool withBorder = false)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(type => type.Name == name);
            if (existing == null)
            {
                var template = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("專案中找不到任何文字型式。");
                existing = template.Duplicate(name) as TextNoteType;
            }

            existing?.get_Parameter(BuiltInParameter.TEXT_SIZE)
                ?.Set(UnitUtils.ConvertToInternalUnits(sizeMillimeters, UnitTypeId.Millimeters));
            existing?.get_Parameter(BuiltInParameter.LINE_COLOR)?.Set(0);
            // 2026-07-10 D5 使用者裁決：圖紙文字一律黑體；冪等建立時同步校正字體。
            existing?.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("Microsoft JhengHei");
            if (withBorder)
            {
                // 2026-07-10 九輪使用者裁決：標籤各自加外框，避免看起來像連續一整段文字。
                // TEXT_BOX_VISIBILITY 為 Revit 內建「顯示框線」型式參數（1=顯示、0=不顯示）；
                // 個別版本若無此參數，get_Parameter 回 null、經 ?. 靜默略過，不阻斷出圖。
                existing?.get_Parameter(BuiltInParameter.TEXT_BOX_VISIBILITY)?.Set(1);
            }

            return existing?.Id ?? doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        }

        private static ElementId EnsureNoTitleViewportType(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .Cast<ElementType>()
                .FirstOrDefault(type => type.Name == "整地-無圖名"
                    && type.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null);
            if (existing == null)
            {
                var template = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElementType))
                    .Cast<ElementType>()
                    .FirstOrDefault(type => type.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null);
                if (template == null)
                {
                    return ElementId.InvalidElementId;
                }

                existing = template.Duplicate("整地-無圖名") as ElementType;
            }

            try
            {
                existing?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.Set(0);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return existing?.Id ?? ElementId.InvalidElementId;
            }

            return existing?.Id ?? ElementId.InvalidElementId;
        }

        private static GraphicsStyle EnsureSheetLineStyle(Document doc, string styleName, Color color, int lineWeight = 1)
        {
            var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var subCategory = linesCategory.SubCategories
                .Cast<Category>()
                .FirstOrDefault(category => category.Name == styleName);
            if (subCategory == null)
            {
                subCategory = doc.Settings.Categories.NewSubcategory(linesCategory, styleName);
            }

            subCategory.SetLineWeight(lineWeight, GraphicsStyleType.Projection);
            subCategory.LineColor = color;
            return subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        /// <summary>沿原地形最大高差方向定共同剖面線：8×8 粗網格取樣頂面 Z，最高點連最低點、外延 20%。</summary>
        private static (XYZ Start, XYZ End) ComputeMaxDropSectionLine(Toposolid original)
        {
            var solids = RevitToposolidGradingAdapter.CollectSolids(original);
            var box = original.get_BoundingBox(null)
                ?? throw new InvalidOperationException("原地形沒有範圍盒。");
            var bottomZ = box.Min.Z - 10.0;
            var topZ = box.Max.Z + 10.0;

            XYZ highest = null, lowest = null;
            double highestZ = double.MinValue, lowestZ = double.MaxValue;
            const int steps = 8;
            for (var i = 0; i <= steps; i++)
            {
                for (var j = 0; j <= steps; j++)
                {
                    var x = box.Min.X + ((box.Max.X - box.Min.X) * i / steps);
                    var y = box.Min.Y + ((box.Max.Y - box.Min.Y) * j / steps);
                    var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        solids, new Point2D(x, y), bottomZ, topZ);
                    if (!z.HasValue) { continue; }
                    if (z.Value > highestZ) { highestZ = z.Value; highest = new XYZ(x, y, z.Value); }
                    if (z.Value < lowestZ) { lowestZ = z.Value; lowest = new XYZ(x, y, z.Value); }
                }
            }

            if (highest == null || lowest == null || highest.DistanceTo(lowest) < 1e-6)
            {
                throw new InvalidOperationException("取樣不到原地形高差方向，無法決定剖面線。");
            }

            var direction = (lowest - highest);
            var extend = 0.2;
            return (highest - (direction * extend), lowest + (direction * extend));
        }

        private static GradingComparisonFocusBounds? TryComputeComparisonFocus(
            Document doc, IReadOnlyList<JObject> records)
        {
            var snapshots = new List<FloorGeometrySnapshot>();
            var horizontalPaddingMeters = 8.0;
            foreach (var record in records)
            {
                snapshots.AddRange(ReadFloorSnapshotsForComparison(doc, record));
                var mode = record.Value<string>("Mode");
                var transitionMeters = mode == "offset_transition"
                    ? record.Value<double?>("OffsetDistanceMeters") ?? 0.0
                    : mode == "slope_transition"
                        ? record.Value<double?>("MaxExtensionMeters") ?? 0.0
                        : 0.0;
                horizontalPaddingMeters = Math.Max(horizontalPaddingMeters, transitionMeters + 4.0);
            }

            if (snapshots.Count == 0)
            {
                return null;
            }

            return GradingComparisonFocusBounds.FromFloorSnapshots(
                snapshots,
                horizontalPaddingMeters: horizontalPaddingMeters,
                minimumPlanSpanMeters: 24.0,
                verticalPaddingMeters: 3.0,
                minimumHeightMeters: 8.0);
        }

        private static IReadOnlyList<FloorGeometrySnapshot> ReadFloorSnapshotsForComparison(
            Document doc, JObject record)
        {
            var snapshots = record["FloorSnapshots"]?.ToObject<List<FloorGeometrySnapshot>>()
                ?? new List<FloorGeometrySnapshot>();
            if (snapshots.Any(IsUsableFloorSnapshot))
            {
                return snapshots.Where(IsUsableFloorSnapshot).ToList();
            }

            var fallback = new List<FloorGeometrySnapshot>();
            foreach (var floorId in ReadRecordFloorIds(record))
            {
                if (!(doc.GetElement(new ElementId(checked((int)floorId))) is Floor floor))
                {
                    continue;
                }

                var box = floor.get_BoundingBox(null);
                if (box == null)
                {
                    continue;
                }

                fallback.Add(new FloorGeometrySnapshot
                {
                    FloorId = floorId,
                    BottomZMinMeters = FeetToMeters(box.Min.Z),
                    BottomZMaxMeters = FeetToMeters(box.Max.Z),
                    OuterLoopMeters = new[]
                    {
                        new FloorSnapshotPoint { XMeters = FeetToMeters(box.Min.X), YMeters = FeetToMeters(box.Min.Y) },
                        new FloorSnapshotPoint { XMeters = FeetToMeters(box.Max.X), YMeters = FeetToMeters(box.Min.Y) },
                        new FloorSnapshotPoint { XMeters = FeetToMeters(box.Max.X), YMeters = FeetToMeters(box.Max.Y) },
                        new FloorSnapshotPoint { XMeters = FeetToMeters(box.Min.X), YMeters = FeetToMeters(box.Max.Y) },
                    }
                });
            }

            return fallback;
        }

        private static bool IsUsableFloorSnapshot(FloorGeometrySnapshot snapshot)
        {
            return snapshot?.OuterLoopMeters != null
                && snapshot.OuterLoopMeters.Count >= 3;
        }

        private static (XYZ Start, XYZ End) ComputeFocusedSectionLine(
            Toposolid original, GradingComparisonFocusBounds focus)
        {
            var terrainLine = TryComputeFocusedTerrainDropLine(original, focus);
            if (terrainLine.HasValue)
            {
                return terrainLine.Value;
            }

            var z = MetersToFeet(focus.CenterZMeters);
            if (focus.WidthMeters >= focus.DepthMeters)
            {
                return (
                    new XYZ(MetersToFeet(focus.MinXMeters), MetersToFeet(focus.CenterYMeters), z),
                    new XYZ(MetersToFeet(focus.MaxXMeters), MetersToFeet(focus.CenterYMeters), z));
            }

            return (
                new XYZ(MetersToFeet(focus.CenterXMeters), MetersToFeet(focus.MinYMeters), z),
                    new XYZ(MetersToFeet(focus.CenterXMeters), MetersToFeet(focus.MaxYMeters), z));
        }

        private static (XYZ Start, XYZ End)? TryComputeFocusedTerrainDropLine(
            Toposolid original, GradingComparisonFocusBounds focus)
        {
            var solids = RevitToposolidGradingAdapter.CollectSolids(original);
            if (solids.Count == 0)
            {
                return null;
            }

            var box = original.get_BoundingBox(null);
            if (box == null)
            {
                return null;
            }

            var bottomZ = box.Min.Z - 10.0;
            var topZ = box.Max.Z + 10.0;
            XYZ highest = null;
            XYZ lowest = null;
            var highestZ = double.MinValue;
            var lowestZ = double.MaxValue;
            const int steps = 8;
            for (var ix = 0; ix <= steps; ix++)
            {
                for (var iy = 0; iy <= steps; iy++)
                {
                    var xMeters = focus.MinXMeters + ((focus.MaxXMeters - focus.MinXMeters) * ix / steps);
                    var yMeters = focus.MinYMeters + ((focus.MaxYMeters - focus.MinYMeters) * iy / steps);
                    var x = MetersToFeet(xMeters);
                    var y = MetersToFeet(yMeters);
                    var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        solids, new Point2D(x, y), bottomZ, topZ);
                    if (!z.HasValue)
                    {
                        continue;
                    }

                    if (z.Value > highestZ)
                    {
                        highestZ = z.Value;
                        highest = new XYZ(x, y, z.Value);
                    }

                    if (z.Value < lowestZ)
                    {
                        lowestZ = z.Value;
                        lowest = new XYZ(x, y, z.Value);
                    }
                }
            }

            if (highest == null || lowest == null || HorizontalDistance(highest, lowest) < MetersToFeet(1.0))
            {
                return null;
            }

            var direction = HorizontalDirection(lowest, highest);
            var length = HorizontalDistance(lowest, highest);
            var extend = Math.Max(MetersToFeet(4.0), length * 0.25);
            return (lowest - (direction * extend), highest + (direction * extend));
        }

        private static double MetersToFeet(double meters)
        {
            return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        }

        /// <summary>
        /// D1（2026-07-10 使用者裁決）：剖切線方向對齊 3D 等角視角——把參考 3D 視圖的螢幕「右」
        /// 向量壓平後投影到剖線方向，若反向就把剖線頭尾對調，讓 (d) 剖面左右與 (a)/(c) 一致。
        /// 找不到參考視圖或近垂直（點積≈0）時維持原方向。
        /// </summary>
        private static (XYZ Start, XYZ End) AlignSectionLineToViewRight(
            XYZ start, XYZ end, View referenceView)
        {
            if (referenceView == null)
            {
                return (start, end);
            }

            XYZ right;
            try
            {
                right = referenceView.RightDirection;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return (start, end);
            }

            var flatRight = new XYZ(right.X, right.Y, 0);
            if (flatRight.GetLength() < 1e-9)
            {
                return (start, end);
            }

            var direction = HorizontalDirection(start, end);
            return direction.DotProduct(flatRight.Normalize()) < -1e-9
                ? (end, start)
                : (start, end);
        }

        /// <summary>剖面方向的參考 3D 視圖：作用視圖是 3D 就用它（基準欄視角本來就承接它）；
        /// 否則退第一個方案的書籤視圖；都沒有回 null（維持原剖線方向）。</summary>
        private View ResolveComparisonOrientationView(Document doc, IReadOnlyList<JObject> records)
        {
            if (_uiApp.ActiveUIDocument?.ActiveView is View3D active && !active.IsPerspective)
            {
                return active;
            }

            foreach (var record in records)
            {
                var viewId = record.Value<long?>("SchemeViewId") ?? 0;
                if (viewId > 0
                    && doc.GetElement(new ElementId(checked((int)viewId))) is View3D schemeView)
                {
                    return schemeView;
                }
            }

            return null;
        }

        /// <summary>
        /// 建方案正剖面：沿共同剖面線、隱藏 otherDesignIds 中的地形。
        /// revealDemolished=false（方案欄）：Show All 讓拆除的原地形以半色調疊比；
        /// revealDemolished=true（原始地形基準欄）：用「整地-原地形顯示」篩選讓原地形正常顯示。
        /// </summary>
        private static ViewSection CreateSchemeSectionView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> otherDesignIds,
            bool revealDemolished = false,
            GradingComparisonFocusBounds? focus = null,
            (double MinZ, double MaxZ)? zRange = null)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.Section)
                ?? throw new InvalidOperationException("找不到剖面視圖類型。");

            var mid = focus.HasValue
                ? new XYZ(
                    MetersToFeet(focus.Value.CenterXMeters),
                    MetersToFeet(focus.Value.CenterYMeters),
                    MetersToFeet(focus.Value.CenterZMeters))
                : (start + end) / 2;
            // 2026-07-09 文件實錘（BoundingBoxXYZ.Transform）：「The transform must always be
            // right-handed and orthonormal.」——盒座標系必須右手系：BasisX=d、BasisY=ẑ、
            // BasisZ=d×ẑ（X×Y=Z ✓）。CreateSection 文件的「(right,up,view) 左手系」描述的是
            // Revit 內部由 up/view 推算 right 向量的方式，與盒 Transform 手性無關（前輪誤讀）。
            // 方向並須壓平（帶 Z 分量會破壞正交性）。
            var lineDirection = HorizontalDirection(start, end);
            var viewDirection = lineDirection.CrossProduct(XYZ.BasisZ);
            var halfLength = HorizontalDistance(start, end) / 2;
            var depth = focus.HasValue
                ? MetersToFeet(Math.Max(Math.Min(focus.Value.WidthMeters, focus.Value.DepthMeters), 8.0))
                : UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Meters);
            var height = focus.HasValue
                ? MetersToFeet(focus.Value.HeightMeters)
                : UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);

            // 2026-07-11 十七輪（使用者回報四：挖方上緣被剖切）：剖面框高度先前只吃 focus
            // 的高度（樓板＋設計地形附近），原地形山頂高於 focus 上緣時整段被切平——紅色
            // 挖方色塊看起來像被砍頭。呼叫端現在沿剖線實際取樣原/設計頂面 Z 範圍傳入：
            // 下緣取樣最低點－3m、上緣取樣最高點＋12m（站點標籤排 topZ+4m 起、兩層交錯
            // 再各加一個標籤高，12m 淨空涵蓋標籤與引線）。無 zRange 時維持 focus 舊邏輯。
            var minY = zRange.HasValue ? zRange.Value.MinZ - MetersToFeet(3.0) - mid.Z : -height / 2;
            var maxY = zRange.HasValue ? zRange.Value.MaxZ + MetersToFeet(12.0) - mid.Z : height / 2;
            var transform = Transform.Identity;
            transform.Origin = new XYZ(mid.X, mid.Y, mid.Z);
            transform.BasisX = lineDirection;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = viewDirection;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, minY, 0),
                Max = new XYZ(halfLength, maxY, depth)
            };
            // CreateSection 文件例外清單：「MinEnabled and MaxEnabled must be set to true
            // for all three directions」——明確全開，不賭建構子預設。
            for (var dimension = 0; dimension < 3; dimension++)
            {
                sectionBox.set_MinEnabled(dimension, true);
                sectionBox.set_MaxEnabled(dimension, true);
            }

            var section = ViewSection.CreateSection(doc, viewFamilyType.Id, sectionBox);
            section.Name = UniqueViewName(doc, $"整地-{schemeName}-剖面");
            ApplyComparisonPhaseFilter(doc, section, revealDemolished);
            HideElementsSafely(doc, section, otherDesignIds);
            ApplyExtentLineCategoryVisibility(doc, section, null); // 剖面不顯示範圍線
            // 三輪使用者裁決：剖面要有樓層——保留 Levels、標頭逐層錯開；其餘雜訊照關。
            HideDatumClutter(section, keepLevels: true);
            StaggerLevelHeadsInSection(doc, section);
            // 2026-07-11 十七輪（使用者回報二/三：色塊與剖面線不貼合、虛線意義不明）：
            // Toposolid 剖切會畫「頂面線＋板厚底線」兩條，挖填色塊貼的是頂面（語意正確），
            // 但視覺上粗黑的板厚底線讀起來像「剖面線」，色塊便顯得懸空；且底線與我們畫的
            // 原地形虛線交錯，讀不出誰是誰。改法：(d) 剖面一律隱藏 Toposolid 模型剖切，
            // 地形輪廓全部改由「與色塊同一套取樣資料」畫的輪廓線呈現（設計＝實線、原地形＝
            // 虛線），色塊與線同源、天生零縫；樓板（Floor）維持模型顯示。
            try
            {
                section.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Toposolid), true);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別文件類別不可控時維持模型顯示，寧多線不缺圖。
            }

            return section;
        }

        /// <summary>沿剖線取樣原/設計地形頂面的 Z 範圍（供剖面框高度涵蓋全部地形＋標籤）。</summary>
        private static (double MinZ, double MaxZ)? ComputeSectionZRange(
            IReadOnlyList<IReadOnlyList<Solid>> surfaceSolidSets, XYZ start, XYZ end)
        {
            var usable = surfaceSolidSets?.Where(set => set != null && set.Count > 0).ToList();
            if (usable == null || usable.Count == 0)
            {
                return null;
            }

            var allBoxes = usable
                .SelectMany(set => set)
                .Select(solid => solid.GetBoundingBox())
                .ToList();
            var rayBottomZ = allBoxes.Min(box => box.Transform.Origin.Z + box.Min.Z) - 10.0;
            var rayTopZ = allBoxes.Max(box => box.Transform.Origin.Z + box.Max.Z) + 10.0;
            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var sampleCount = Math.Max(32, Math.Min(120, (int)Math.Ceiling(length / MetersToFeet(1.0))));
            var minZ = double.MaxValue;
            var maxZ = double.MinValue;
            for (var i = 0; i <= sampleCount; i++)
            {
                var distance = length * i / sampleCount;
                var point = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                foreach (var set in usable)
                {
                    var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        set.ToList(), point, rayBottomZ, rayTopZ);
                    if (z.HasValue)
                    {
                        minZ = Math.Min(minZ, z.Value);
                        maxZ = Math.Max(maxZ, z.Value);
                    }
                }
            }

            return maxZ > minZ ? (minZ, maxZ) : ((double, double)?)null;
        }

        /// <summary>
        /// 2026-07-11 十七輪（使用者回報一：標籤仍互疊）：外拉標籤的槽位高度先前用
        /// 「裁切框範圍的比例」估，但標註文字的模型高度＝紙面字高×最終出圖比例，
        /// 與框範圍無關——(b)(c) 半寬格的最終比例大，實際字高可達槽高兩倍，框線互切。
        /// 此函式在標註前「預估」該視圖排圖後的比例分母（A0 版面假設＋等角投影粗估），
        /// AnnotateGradingScheme 以 max(比例槽高, 字高×預估比例×1.25) 定槽高，量綱對齊。
        /// 回傳 0 = 無法預估（呼叫端退回純比例槽高）。
        /// </summary>
        private static double EstimateLabelScaleHint(
            GradingComparisonFocusBounds? focus, double cellWidthMm, double cellHeightMm)
        {
            if (!focus.HasValue || cellWidthMm <= 1 || cellHeightMm <= 1)
            {
                return 0;
            }

            var w = focus.Value.WidthMeters;
            var d = focus.Value.DepthMeters;
            var h = focus.Value.HeightMeters;
            // 鎖定等角視角的投影外接粗估：寬≈(W+D)·cos30°、高≈H＋(W+D)·sin30°·0.7。
            var projectedWidthMeters = (w + d) * 0.87;
            var projectedHeightMeters = (h * 0.9) + ((w + d) * 0.35);
            var scale = Math.Max(
                projectedWidthMeters * 1000 / cellWidthMm,
                projectedHeightMeters * 1000 / cellHeightMm);
            return Math.Max(50, Math.Ceiling(scale / 50) * 50);
        }

        /// <summary>C-00c 格位公釐尺寸粗估（A0 假設；圖框解析本來就 preferA0）。
        /// halfWidthCell=true 回 (b)(c) 半寬格。</summary>
        private static (double CellWMm, double CellHMm) EstimateComparisonCellMm(
            int columnCount, bool halfWidthCell)
        {
            var contentWidthMm = 1189.0 - 24 - 64 - 9; // 左右邊界 12×2、標籤欄 64、欄前帶距 9。
            var columnWidthMm = (contentWidthMm - (9.0 * Math.Max(0, columnCount - 1))) / Math.Max(1, columnCount);
            var cellWMm = halfWidthCell ? (columnWidthMm - 4.95) / 2 : columnWidthMm;
            // 高度：841 − 上下邊界 24 − 標題帶 16 − 表格帶（經驗 ≈0.30 版高）− 帶距 9×4。
            var contentHeightMm = 841.0 - 24 - 16 - (841.0 * 0.30) - 36;
            var weightSum = 1.05 + 0.75 + 0.72; // SheetLayoutMath.ContentBandWeights。
            var cellHMm = contentHeightMm * (halfWidthCell ? 0.75 : 1.05) / weightSum;
            return (cellWMm, cellHMm);
        }

        private static void AddCutFillRegionsToSection(
            Document doc, ViewSection section, JObject record, XYZ start, XYZ end)
        {
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            var designId = record.Value<long?>("DesignToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original)
                || !(doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design))
            {
                return;
            }

            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
            var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
            if (originalSolids.Count == 0 || designSolids.Count == 0)
            {
                return;
            }

            var originalBox = original.get_BoundingBox(null);
            var designBox = design.get_BoundingBox(null);
            if (originalBox == null || designBox == null)
            {
                return;
            }

            var cutTypeId = EnsureSectionRegionType(doc, "整地剖面-挖方紅", new Color(220, 45, 45));
            var fillTypeId = EnsureSectionRegionType(doc, "整地剖面-填方藍", new Color(0, 102, 204));
            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var rayBottomZ = Math.Min(originalBox.Min.Z, designBox.Min.Z) - 10.0;
            var rayTopZ = Math.Max(originalBox.Max.Z, designBox.Max.Z) + 10.0;
            // 2026-07-11 十七輪（使用者回報二）：1m 步距＋100 上限在放坡折點附近弦切明顯，
            // 色塊邊與輪廓線讀得出落差——加密到 0.5m、上限 400（單一剖線，成本可忽略）。
            var sampleCount = Math.Max(48, Math.Min(400, (int)Math.Ceiling(length / MetersToFeet(0.5))));

            var samples = new List<(double Distance, double OriginalZ, double DesignZ)>();
            for (var i = 0; i <= sampleCount; i++)
            {
                var distance = length * i / sampleCount;
                var point = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                    originalSolids, point, rayBottomZ, rayTopZ);
                var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                    designSolids, point, rayBottomZ, rayTopZ);
                if (originalZ.HasValue && designZ.HasValue)
                {
                    samples.Add((distance, originalZ.Value, designZ.Value));
                }
            }

            // 2026-07-10 十三輪修正：舊版差值低於 5cm 門檻或正負號跨越時整段跳過——
            // 使用者回報色塊與剖面線之間有小縫隙，根因正是這裡：挖填交接處差值本就會
            // 趨近於零，逐區間門檻判斷把這些「真實但很薄」的區段整段丟棄，留下可見縫隙。
            // 改為：不再用門檻略過（薄色塊照實際差值畫，Revit 對退化四邊形自然拋例外，
            // 已有 catch 兜底）；正負號跨越時內插零交點拆成兩段，兩段共用交點，無縫銜接。
            for (var i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                var diffA = a.OriginalZ - a.DesignZ;
                var diffB = b.OriginalZ - b.DesignZ;
                if (diffA == 0 && diffB == 0)
                {
                    continue; // 整段原地形＝設計地形，真正無挖填，不是門檻誤判。
                }

                if (diffA != 0 && diffB != 0 && (diffA > 0) != (diffB > 0))
                {
                    var t = diffA / (diffA - diffB);
                    var crossDistance = a.Distance + ((b.Distance - a.Distance) * t);
                    var crossZ = a.OriginalZ + ((b.OriginalZ - a.OriginalZ) * t); // 交點原＝設計地形。
                    DrawCutFillQuad(doc, section, cutTypeId, fillTypeId, start, direction,
                        a.Distance, a.OriginalZ, a.DesignZ, crossDistance, crossZ, crossZ, diffA);
                    DrawCutFillQuad(doc, section, cutTypeId, fillTypeId, start, direction,
                        crossDistance, crossZ, crossZ, b.Distance, b.OriginalZ, b.DesignZ, diffB);
                    continue;
                }

                DrawCutFillQuad(doc, section, cutTypeId, fillTypeId, start, direction,
                    a.Distance, a.OriginalZ, a.DesignZ, b.Distance, b.OriginalZ, b.DesignZ,
                    diffA != 0 ? diffA : diffB);
            }
        }

        /// <summary>單一取樣區間的挖／填色塊四邊形（頂邊原地形、底邊設計地形）。signedDiff 決定紅/藍。</summary>
        private static void DrawCutFillQuad(
            Document doc, ViewSection section, ElementId cutTypeId, ElementId fillTypeId,
            XYZ start, XYZ direction,
            double distanceA, double originalZA, double designZA,
            double distanceB, double originalZB, double designZB,
            double signedDiff)
        {
            var regionTypeId = signedDiff > 0 ? cutTypeId : fillTypeId;
            var p0Original = SectionPoint(start, direction, distanceA, originalZA);
            var p1Original = SectionPoint(start, direction, distanceB, originalZB);
            var p1Design = SectionPoint(start, direction, distanceB, designZB);
            var p0Design = SectionPoint(start, direction, distanceA, designZA);
            var loop = new CurveLoop();
            try
            {
                if (p0Original.DistanceTo(p1Original) > 1e-9) { loop.Append(Line.CreateBound(p0Original, p1Original)); }
                if (p1Original.DistanceTo(p1Design) > 1e-9) { loop.Append(Line.CreateBound(p1Original, p1Design)); }
                if (p1Design.DistanceTo(p0Design) > 1e-9) { loop.Append(Line.CreateBound(p1Design, p0Design)); }
                if (p0Design.DistanceTo(p0Original) > 1e-9) { loop.Append(Line.CreateBound(p0Design, p0Original)); }
                if (loop.Count() >= 3)
                {
                    var region = FilledRegion.Create(doc, regionTypeId, section.Id, new List<CurveLoop> { loop });
                    // 十七輪追修（v17 實測）：色塊自帶的深色邊框把「原地形虛線」蓋住——
                    // 圖例寫虛線、圖上讀不到。邊框改隱形線，上下輪廓交給同源畫的
                    // 設計實線／原地形虛線表達。
                    try
                    {
                        var invisible = doc.Settings.Categories
                            .get_Item(BuiltInCategory.OST_InvisibleLines)
                            ?.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (invisible != null)
                        {
                            region.SetLineStyleId(invisible.Id);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 個別文件無隱形線型式時保留預設邊框，不阻斷出圖。
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 太薄或退化的剖面色塊略過，不阻斷出圖。
            }
        }

        private static XYZ SectionPoint(XYZ start, XYZ direction, double distance, double z)
        {
            return new XYZ(start.X + (direction.X * distance), start.Y + (direction.Y * distance), z);
        }

        private static ElementId EnsureSectionRegionType(Document doc, string name, Color color)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(type => type.Name == name);
            if (existing == null)
            {
                var template = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("專案中沒有任何 FilledRegionType 可作為模板。");
                existing = template.Duplicate(name) as FilledRegionType;
            }

            var solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);
            if (existing != null)
            {
                existing.ForegroundPatternColor = color;
                if (solidFill != null)
                {
                    existing.ForegroundPatternId = solidFill.Id;
                }
            }

            return existing?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>
        /// 2026-07-10 二輪視覺檢查：出圖視圖的基準面雜訊——樓層線（Levels）標頭在剖面右側
        /// 互疊成一坨、裁切框線露出。三輪使用者裁決：剖面「要有樓層」→ keepLevels=true 保留
        /// Levels（另以 StaggerLevelHeadsInSection 錯開標頭），其餘視圖照舊隱藏；
        /// Grids 一律隱藏、裁切框顯示一律關（裁切仍生效）。
        /// </summary>
        private static void HideDatumClutter(View view, bool keepLevels = false)
        {
            var categories = keepLevels
                ? new[] { BuiltInCategory.OST_Grids }
                : new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids };
            foreach (var category in categories)
            {
                try
                {
                    view.SetCategoryHidden(new ElementId(category), true);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別視圖不支援該類別可見性時略過。
                }
            }

            try
            {
                view.CropBoxVisible = false;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 不支援裁切框屬性的視圖略過。
            }
        }

        /// <summary>
        /// 三輪：剖面保留樓層線但標頭會在同側疊字（本模型樓層 0/0.6/1.2m 靠很近）——
        /// 逐層把右端縮短一階，讓標頭沿剖線錯開。逐層容錯，失敗維持原長。
        /// </summary>
        private static void StaggerLevelHeadsInSection(Document doc, ViewSection section)
        {
            try
            {
                doc.Regenerate();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return;
            }

            var stagger = MetersToFeet(7.0);
            var rank = 0;
            foreach (var level in new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(candidate => candidate.Elevation))
            {
                try
                {
                    var curves = level.GetCurvesInView(DatumExtentType.ViewSpecific, section);
                    if (curves == null || curves.Count == 0 || !(curves[0] is Line line))
                    {
                        continue;
                    }

                    var start = line.GetEndPoint(0);
                    var end = line.GetEndPoint(1);
                    var direction = (end - start).Normalize();
                    var trimmed = end - (direction * stagger * rank);
                    if (trimmed.DistanceTo(start) < MetersToFeet(3.0))
                    {
                        trimmed = start + (direction * MetersToFeet(3.0));
                    }

                    level.SetDatumExtentType(DatumEnds.End1, section, DatumExtentType.ViewSpecific);
                    level.SetCurveInView(DatumExtentType.ViewSpecific, section, Line.CreateBound(start, trimmed));
                    rank++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別樓層不支援視圖特定範圍時略過。
                }
            }
        }

        /// <summary>
        /// 三輪使用者裁決：剖面裁到「主要挖填範圍＋周遭一點」——沿剖線取樣所有方案的
        /// 原/設計高差，取聯集擾動區間外擴 15%（至少 6m），回傳裁短後的剖線端點。
        /// 全欄共用同一裁切，維持跨方案可比性；量不到擾動時維持原線。
        /// </summary>
        private (XYZ Start, XYZ End) TrimSectionLineToDisturbance(
            Document doc, IReadOnlyList<JObject> records, Toposolid original, XYZ start, XYZ end)
        {
            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
            var box = original.get_BoundingBox(null);
            if (originalSolids.Count == 0 || box == null)
            {
                return (start, end);
            }

            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var rayBottomZ = box.Min.Z - 10.0;
            var rayTopZ = box.Max.Z + 10.0;
            var threshold = MetersToFeet(0.15);
            var sampleCount = Math.Max(32, Math.Min(200, (int)Math.Ceiling(length / MetersToFeet(1.0))));

            var minDisturbed = double.MaxValue;
            var maxDisturbed = double.MinValue;
            foreach (var record in records)
            {
                var designId = record.Value<long?>("DesignToposolidId") ?? 0;
                if (!(doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design))
                {
                    continue;
                }

                var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
                if (designSolids.Count == 0)
                {
                    continue;
                }

                for (var i = 0; i <= sampleCount; i++)
                {
                    var distance = length * i / sampleCount;
                    var point = new Point2D(
                        start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                    var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        originalSolids, point, rayBottomZ, rayTopZ);
                    var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                        designSolids, point, rayBottomZ, rayTopZ);
                    if (originalZ.HasValue && designZ.HasValue
                        && Math.Abs(originalZ.Value - designZ.Value) > threshold)
                    {
                        minDisturbed = Math.Min(minDisturbed, distance);
                        maxDisturbed = Math.Max(maxDisturbed, distance);
                    }
                }
            }

            if (maxDisturbed <= minDisturbed)
            {
                return (start, end);
            }

            var padding = Math.Max(MetersToFeet(6.0), (maxDisturbed - minDisturbed) * 0.15);
            var fromDistance = Math.Max(0, minDisturbed - padding);
            var toDistance = Math.Min(length, maxDisturbed + padding);
            return (
                new XYZ(start.X + (direction.X * fromDistance), start.Y + (direction.Y * fromDistance), start.Z),
                new XYZ(start.X + (direction.X * toDistance), start.Y + (direction.Y * toDistance), end.Z));
        }

        /// <summary>
        /// 三輪使用者裁決：剖面上標「原地形／整地後」站點標高——站點取剖線兩端內縮、
        /// 樓板兩緣與中點；標籤排圖面上方、細線下引到設計地形面，不壓剖面內容。
        /// </summary>
        private static void AddSectionStationLabels(
            Document doc, ViewSection section, JObject record, XYZ start, XYZ end)
        {
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            var designId = record.Value<long?>("DesignToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original)
                || !(doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design))
            {
                return;
            }

            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
            var designSolids = RevitToposolidGradingAdapter.CollectSolids(design);
            var box = original.get_BoundingBox(null);
            if (originalSolids.Count == 0 || designSolids.Count == 0 || box == null)
            {
                return;
            }

            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var rayBottomZ = box.Min.Z - 10.0;
            var rayTopZ = box.Max.Z + 10.0;

            // 2026-07-11 十七輪（使用者回報五：站點都落在轉折點、原/後數值看起來差不多）：
            // 站點改「資訊量優先」選點——先沿線密取樣算 原-設 差值，
            // 優先序：P0 最大挖深點、最大填高點（差值極值＝最有故事的位置）→
            // P1 樓板投影兩緣與中點 → P2 剖線兩端內縮 → P3 大間隙中點補點。
            // 依優先序貪婪收點（間距門檻內讓位給高優先），最後標籤高低兩排交錯，
            // 同樣版面能容納更密的站點。
            var denseCount = Math.Max(48, Math.Min(400, (int)Math.Ceiling(length / MetersToFeet(0.5))));
            var maxCutStation = (Distance: (double?)null, Diff: 0.0);
            var maxFillStation = (Distance: (double?)null, Diff: 0.0);
            for (var i = 0; i <= denseCount; i++)
            {
                var distance = length * i / denseCount;
                var point2d = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(originalSolids, point2d, rayBottomZ, rayTopZ);
                var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(designSolids, point2d, rayBottomZ, rayTopZ);
                if (!originalZ.HasValue || !designZ.HasValue)
                {
                    continue;
                }

                var diff = originalZ.Value - designZ.Value;
                if (diff > maxCutStation.Diff) { maxCutStation = (distance, diff); }
                if (-diff > maxFillStation.Diff) { maxFillStation = (distance, -diff); }
            }

            var candidates = new List<(int Priority, double Distance)>();
            if (maxCutStation.Distance.HasValue) { candidates.Add((0, maxCutStation.Distance.Value)); }
            if (maxFillStation.Distance.HasValue) { candidates.Add((0, maxFillStation.Distance.Value)); }
            var snapshots = record["FloorSnapshots"]?.ToObject<List<FloorGeometrySnapshot>>();
            if (snapshots != null && snapshots.Count > 0)
            {
                var minPad = double.MaxValue;
                var maxPad = double.MinValue;
                foreach (var point in snapshots
                    .Where(snapshot => snapshot?.OuterLoopMeters != null)
                    .SelectMany(snapshot => snapshot.OuterLoopMeters))
                {
                    var world = new XYZ(MetersToFeet(point.XMeters), MetersToFeet(point.YMeters), 0);
                    var distance = (world - new XYZ(start.X, start.Y, 0)).DotProduct(direction);
                    minPad = Math.Min(minPad, distance);
                    maxPad = Math.Max(maxPad, distance);
                }

                if (maxPad > minPad)
                {
                    candidates.Add((1, Math.Max(0, Math.Min(length, minPad))));
                    candidates.Add((1, Math.Max(0, Math.Min(length, maxPad))));
                    candidates.Add((1, Math.Max(0, Math.Min(length, (minPad + maxPad) / 2))));
                }
            }

            candidates.Add((2, length * 0.06));
            candidates.Add((2, length * 0.94));

            // 兩排交錯後同排間距＝2×收點門檻，門檻可比單排時代（12%）收緊到 7%。
            var minStationGap = Math.Max(MetersToFeet(4.0), length * 0.07);
            var deduped = new List<double>();
            foreach (var candidate in candidates.OrderBy(c => c.Priority).ThenBy(c => c.Distance))
            {
                if (deduped.All(kept => Math.Abs(candidate.Distance - kept) > minStationGap))
                {
                    deduped.Add(candidate.Distance);
                }
            }

            // P3：大間隙補中點（同門檻），讓長段落不至於整段沒有讀數。
            var sorted = deduped.OrderBy(value => value).ToList();
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var gapMid = (sorted[i] + sorted[i + 1]) / 2;
                if (sorted[i + 1] - sorted[i] > length * 0.24
                    && deduped.All(kept => Math.Abs(gapMid - kept) > minStationGap))
                {
                    deduped.Add(gapMid);
                }
            }

            deduped = deduped.OrderBy(value => value).ToList();

            // 標籤頂線：取全線設計/原地形最高點再加淨空。
            var topZ = double.MinValue;
            var samplesForTop = 24;
            for (var i = 0; i <= samplesForTop; i++)
            {
                var distance = length * i / samplesForTop;
                var point = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(originalSolids, point, rayBottomZ, rayTopZ);
                var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(designSolids, point, rayBottomZ, rayTopZ);
                if (originalZ.HasValue) { topZ = Math.Max(topZ, originalZ.Value); }
                if (designZ.HasValue) { topZ = Math.Max(topZ, designZ.Value); }
            }

            if (topZ <= double.MinValue)
            {
                return;
            }

            var labelZ = topZ + MetersToFeet(4.0);
            // 十七輪：標籤高低兩排交錯（相鄰站點分屬不同排）。v17 實測 4.5m 排距在 1:350+
            // 比例下仍與兩行標籤實高（≈12.6mm×比例）擦邊——鄰站不同排的框角仍互切，
            // 拉大到 6m（(d) 為全寬格、比例通常 ≤1:350，6m > 4.4m 實高有餘裕）。
            var tierStep = MetersToFeet(6.0);
            // 九輪使用者裁決：標籤加外框，避免看起來像連續一整段文字。
            var textTypeId = EnsureTextNoteType(doc, "整地標高標籤", 2.4, withBorder: true);
            var leaderStyle = EnsureSheetLineStyle(doc, "整地站點引線", new Color(120, 120, 120), 1);
            for (var stationIndex = 0; stationIndex < deduped.Count; stationIndex++)
            {
                var station = deduped[stationIndex];
                var stationLabelZ = labelZ + ((stationIndex % 2) * tierStep);
                var point = new Point2D(start.X + (direction.X * station), start.Y + (direction.Y * station));
                var originalZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(originalSolids, point, rayBottomZ, rayTopZ);
                var designZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(designSolids, point, rayBottomZ, rayTopZ);
                if (!designZ.HasValue && !originalZ.HasValue)
                {
                    continue;
                }

                var anchorZ = designZ ?? originalZ.Value;
                var labelPosition = SectionPoint(start, direction, station, stationLabelZ);
                var target = SectionPoint(start, direction, station, anchorZ);
                var lines = new List<string>();
                if (originalZ.HasValue)
                {
                    lines.Add($"原GL {UnitUtils.ConvertFromInternalUnits(originalZ.Value, UnitTypeId.Meters):F1}");
                }

                if (designZ.HasValue)
                {
                    lines.Add($"整地後 {UnitUtils.ConvertFromInternalUnits(designZ.Value, UnitTypeId.Meters):F1}");
                }

                try
                {
                    TextNote.Create(doc, section.Id, labelPosition, string.Join("\n", lines),
                        new TextNoteOptions
                        {
                            TypeId = textTypeId,
                            HorizontalAlignment = HorizontalTextAlignment.Center
                        });
                    var curve = doc.Create.NewDetailCurve(section, Line.CreateBound(
                        labelPosition - new XYZ(0, 0, MetersToFeet(1.2)), target));
                    curve.LineStyle = leaderStyle;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別站點退化（引線過短等）略過。
                }
            }
        }

        /// <summary>D9：3D 視圖表現法承接來源視圖（MODEL_GRAPHICS_STYLE）；無來源退著色＋邊線，
        /// 消除新建視圖預設隱藏線的黑白線稿。</summary>
        private static void ApplyDisplayStyleFrom(View view, View styleSource)
        {
            try
            {
                var target = view.get_Parameter(BuiltInParameter.MODEL_GRAPHICS_STYLE);
                if (target == null || target.IsReadOnly)
                {
                    return;
                }

                var sourceValue = styleSource?.get_Parameter(BuiltInParameter.MODEL_GRAPHICS_STYLE)?.AsInteger();
                target.Set(sourceValue ?? (int)DisplayStyle.ShadingWithEdges);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別視圖不支援表現法設定時維持預設，不阻斷出圖。
            }
        }

        /// <summary>D10：原地形剖面輪廓虛線線型式（深灰、粗、虛線圖樣冪等建立）。</summary>
        private static GraphicsStyle EnsureDashedOriginalProfileStyle(Document doc)
        {
            const string patternName = "整地原地形虛線圖樣";
            var pattern = LinePatternElement.GetLinePatternElementByName(doc, patternName);
            if (pattern == null)
            {
                var definition = new LinePattern(patternName);
                definition.SetSegments(new List<LinePatternSegment>
                {
                    new LinePatternSegment(LinePatternSegmentType.Dash,
                        UnitUtils.ConvertToInternalUnits(4.0, UnitTypeId.Millimeters)),
                    new LinePatternSegment(LinePatternSegmentType.Space,
                        UnitUtils.ConvertToInternalUnits(2.5, UnitTypeId.Millimeters))
                });
                pattern = LinePatternElement.Create(doc, definition);
            }

            var style = EnsureSheetLineStyle(doc, "整地原地形輪廓", new Color(70, 70, 70), 5);
            style.GraphicsStyleCategory.SetLinePatternId(pattern.Id, GraphicsStyleType.Projection);
            return style;
        }

        /// <summary>設計地形（整地後）剖面輪廓實線線型式（近黑、粗、實線）。</summary>
        private static GraphicsStyle EnsureSolidDesignProfileStyle(Document doc)
        {
            return EnsureSheetLineStyle(doc, "整地設計地形輪廓", new Color(35, 35, 35), 5);
        }

        /// <summary>
        /// 沿剖線把指定地形頂面輪廓畫成 detail 線（只在該剖面視圖）。十七輪起 (d) 剖面隱藏
        /// Toposolid 模型剖切，設計（實線）與原地形（虛線）輪廓都由這裡的取樣資料畫——
        /// 與挖填色塊同一套取樣，線與色塊天生貼合。
        /// </summary>
        private static void AddSurfaceProfileToSection(
            Document doc, ViewSection section, IReadOnlyList<Solid> solids, XYZ start, XYZ end,
            GraphicsStyle style)
        {
            if (solids == null || solids.Count == 0)
            {
                return;
            }

            var boxes = solids.Select(solid => solid.GetBoundingBox()).ToList();
            var rayBottomZ = boxes.Min(box => box.Transform.Origin.Z + box.Min.Z) - 10.0;
            var rayTopZ = boxes.Max(box => box.Transform.Origin.Z + box.Max.Z) + 10.0;
            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var sampleCount = Math.Max(48, Math.Min(400, (int)Math.Ceiling(length / MetersToFeet(0.5))));

            XYZ previous = null;
            for (var i = 0; i <= sampleCount; i++)
            {
                var distance = length * i / sampleCount;
                var point2d = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                    solids.ToList(), point2d, rayBottomZ, rayTopZ);
                if (!z.HasValue)
                {
                    previous = null; // 出地形邊界斷線，不連弦。
                    continue;
                }

                var current = SectionPoint(start, direction, distance, z.Value);
                if (previous != null && previous.DistanceTo(current) > 1e-6)
                {
                    try
                    {
                        var curve = doc.Create.NewDetailCurve(section, Line.CreateBound(previous, current));
                        curve.LineStyle = style;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 個別段退化略過。
                    }
                }

                previous = current;
            }
        }

        /// <summary>
        /// D10：沿剖線把「原地形頂面輪廓」畫成醒目虛線（detail curve，只在該剖面視圖）。
        /// 2026-07-11 十七輪（使用者回報三：虛線意義不明）：擾動範圍外原地形＝設計地形，
        /// 兩線重疊只剩取樣抖動的鋸齒，讀起來像第三種不明線——改為只在「原≠設計」的擾動
        /// 段落畫虛線（各留 2m 收尾），範圍外由設計實線單獨代表地表。
        /// </summary>
        private static void AddOriginalProfileToSection(
            Document doc, ViewSection section, JObject record, XYZ start, XYZ end)
        {
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            var designId = record.Value<long?>("DesignToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original))
            {
                return;
            }

            var solids = RevitToposolidGradingAdapter.CollectSolids(original);
            var designSolids = doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design
                ? RevitToposolidGradingAdapter.CollectSolids(design)
                : new List<Solid>();
            var box = original.get_BoundingBox(null);
            if (solids.Count == 0 || box == null)
            {
                return;
            }

            var direction = HorizontalDirection(start, end);
            var length = HorizontalDistance(start, end);
            var rayBottomZ = box.Min.Z - 10.0;
            var rayTopZ = box.Max.Z + 10.0;
            var sampleCount = Math.Max(48, Math.Min(400, (int)Math.Ceiling(length / MetersToFeet(0.5))));
            var style = EnsureDashedOriginalProfileStyle(doc);
            var runOut = MetersToFeet(2.0);
            var diffThreshold = MetersToFeet(0.02);

            // 先取樣全線，找出擾動段（|原-設| > 2cm）±2m 收尾，只在這些段落畫虛線。
            var points = new List<(double Distance, double? Z, bool Disturbed)>();
            for (var i = 0; i <= sampleCount; i++)
            {
                var distance = length * i / sampleCount;
                var point2d = new Point2D(start.X + (direction.X * distance), start.Y + (direction.Y * distance));
                var z = RevitToposolidGradingAdapter.IntersectTerrainTopZ(solids, point2d, rayBottomZ, rayTopZ);
                if (!z.HasValue)
                {
                    points.Add((distance, null, false));
                    continue;
                }

                var designZ = designSolids.Count > 0
                    ? RevitToposolidGradingAdapter.IntersectTerrainTopZ(designSolids, point2d, rayBottomZ, rayTopZ)
                    : null;
                var disturbed = designZ.HasValue
                    ? Math.Abs(z.Value - designZ.Value) > diffThreshold
                    : true; // 無設計地形（基準欄不會走到這）退回全畫。
                points.Add((distance, z.Value, disturbed));
            }

            var drawFrom = new List<double>();
            var drawTo = new List<double>();
            foreach (var point in points.Where(p => p.Disturbed))
            {
                if (drawTo.Count > 0 && point.Distance - runOut <= drawTo[drawTo.Count - 1])
                {
                    drawTo[drawTo.Count - 1] = point.Distance + runOut; // 相鄰擾動段合併。
                }
                else
                {
                    drawFrom.Add(point.Distance - runOut);
                    drawTo.Add(point.Distance + runOut);
                }
            }

            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (!a.Z.HasValue || !b.Z.HasValue) { continue; } // 出地形邊界斷線。
                var inDrawRange = false;
                for (var seg = 0; seg < drawFrom.Count; seg++)
                {
                    if (a.Distance >= drawFrom[seg] && b.Distance <= drawTo[seg])
                    {
                        inDrawRange = true;
                        break;
                    }
                }

                if (!inDrawRange)
                {
                    continue;
                }

                var p0 = SectionPoint(start, direction, a.Distance, a.Z.Value);
                var p1 = SectionPoint(start, direction, b.Distance, b.Z.Value);
                if (p0.DistanceTo(p1) <= 1e-6)
                {
                    continue;
                }

                try
                {
                    var curve = doc.Create.NewDetailCurve(section, Line.CreateBound(p0, p1));
                    curve.LineStyle = style;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 個別段退化略過。
                }
            }
        }

        /// <summary>比較視圖階段篩選：reveal=true 用「整地-原地形顯示」（拆除正常色），否則 Show All（拆除半色調疊比）。</summary>
        private static void ApplyComparisonPhaseFilter(Document doc, View view, bool revealDemolished)
        {
            var filterId = revealDemolished
                ? EnsureRevealPhaseFilter(doc)
                : new FilteredElementCollector(doc)
                    .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                    .FirstOrDefault(filter => filter.Name == "全部顯示" || filter.Name == "Show All")
                    ?.Id ?? ElementId.InvalidElementId;
            if (filterId != ElementId.InvalidElementId)
            {
                view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(filterId);
            }
        }

        /// <summary>剖面線的水平方向（Z 壓平＋正規化）；退化（近垂直線）時拒絕。</summary>
        private static XYZ HorizontalDirection(XYZ start, XYZ end)
        {
            var flat = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (flat.GetLength() < 1e-9)
            {
                throw new InvalidOperationException("剖面線水平投影長度為零，無法決定剖面方向。");
            }

            return flat.Normalize();
        }

        /// <summary>剖面線的水平投影長度。</summary>
        private static double HorizontalDistance(XYZ start, XYZ end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>逐元素過濾後隱藏（不可隱藏／已隱藏者略過），避免整批 HideElements 因單一元素炸掉。</summary>
        private static void HideElementsSafely(Document doc, View view, IReadOnlyList<ElementId> ids)
        {
            var toHide = ids
                .Select(id => doc.GetElement(id))
                .Where(element => element != null && element.CanBeHidden(view) && !element.IsHidden(view))
                .Select(element => element.Id)
                .ToList();
            if (toHide.Count > 0)
            {
                view.HideElements(toHide);
            }
        }

        /// <summary>
        /// 建方案剖面立體圖：等角 3D＋剖面框沿剖切帶（規格風險節：不用透視，避免透視排圖限制）。
        /// 2026-07-08 使用者裁決：不隔離——柱樑、梁板、植栽等既有元素全部保留。
        /// 2026-07-10 D9 使用者裁決：改彩色表現法（styleSource 承接 (a) 欄視圖，取不到退著色＋邊線）；
        /// 方案欄只顯示本案設計地形——原地形由呼叫端一併放進 otherDesignIds 隱藏，
        /// 前後對比職責交給 (d) 正剖面。
        /// </summary>
        private static View3D CreateSchemeSectionIsoView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> otherDesignIds,
            bool revealDemolished = false,
            GradingComparisonFocusBounds? focus = null,
            View styleSource = null)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("找不到 3D 視圖類型。");

            var view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            view.Name = UniqueViewName(doc, $"整地-{schemeName}-剖立體");
            ApplyDisplayStyleFrom(view, styleSource);
            ApplyComparisonPhaseFilter(doc, view, revealDemolished);
            HideElementsSafely(doc, view, otherDesignIds);
            ApplyExtentLineCategoryVisibility(doc, view, null); // 剖立體不顯示範圍線
            HideDatumClutter(view);

            var mid = focus.HasValue
                ? new XYZ(
                    MetersToFeet(focus.Value.CenterXMeters),
                    MetersToFeet(focus.Value.CenterYMeters),
                    MetersToFeet(focus.Value.CenterZMeters))
                : (start + end) / 2;
            // 同正剖面：方向壓平；BoundingBoxXYZ.Transform 須右手系——BasisX=d、BasisY=ẑ×d、
            // BasisZ=ẑ（X×Y＝d×(ẑ×d)＝ẑ ✓）。
            var lineDirection = HorizontalDirection(start, end);
            var viewDirection = XYZ.BasisZ.CrossProduct(lineDirection);
            var halfLength = HorizontalDistance(start, end) / 2;
            var band = focus.HasValue
                ? MetersToFeet(Math.Max(Math.Min(focus.Value.WidthMeters, focus.Value.DepthMeters), 8.0))
                : UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Meters);
            var height = focus.HasValue
                ? MetersToFeet(focus.Value.HeightMeters)
                : UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);
            var transform = Transform.Identity;
            transform.Origin = mid;
            transform.BasisX = lineDirection;
            transform.BasisY = viewDirection;
            transform.BasisZ = XYZ.BasisZ;
            var isoSectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, -MetersToFeet(0.5), -height / 2),
                Max = new XYZ(halfLength, band, height / 2)
            };
            view.SetSectionBox(isoSectionBox);
            // 十二輪：(c) 剖立體同步硬裁切，標籤才能安全用滿兩側不穿到隔壁欄。
            SyncCropBoxToSectionBox(view, isoSectionBox);
            view.SaveOrientationAndLock();
            return view;
        }

        /// <summary>
        /// C-00c 單張總比較圖紙：一欄一方案（3D／坡度熱區／正剖面／剖面立體），
        /// 底部整地方案比較明細表；A0 圖框；可選匯 PDF。逐方案容錯。
        /// </summary>
        private object CreateGradingComparisonSheet(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            ClearActiveSelection();
            var records = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse).ToList();
            var filter = parameters["designToposolidIds"] as JArray;
            if (filter != null && filter.Count > 0)
            {
                var wanted = new HashSet<long>(filter.Values<long>());
                records = records.Where(r => wanted.Contains(r.Value<long>("DesignToposolidId"))).ToList();
            }

            if (records.Count == 0)
            {
                throw new InvalidOperationException("登記簿沒有可比較的方案。");
            }

            var titleBlock = ResolveGradingTitleBlock(doc, parameters, preferA0: true);
            var export = parameters["export"]?.Value<bool?>() ?? true;
            // 2026-07-10 使用者裁決：比較表可選「native=內建明細表」或「custom=自訂排版」；
            // 不論選哪個，內建明細表都要建好維護。
            var scheduleStyle = parameters["scheduleStyle"]?.Value<string>() == "native"
                ? "native"
                : "custom";

            // 1. 共用參數綁定＋指標回寫（單一交易）
            using (var transaction = new Transaction(doc, "回寫方案比較指標"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                GradingSharedParameters.EnsureBindings(doc);
                foreach (var record in records)
                {
                    var designId = record.Value<long>("DesignToposolidId");
                    if (doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design)
                    {
                        GradingSharedParameters.WriteSchemeMetrics(doc, design, record);
                    }
                }

                transaction.Commit();
            }

            // 2. 共同剖面線：優先用各方案「樓板幾何快照」聚焦，舊記錄無快照才退回原地形最大高差。
            var originalId = records[0].Value<long>("OriginalToposolidId");
            var original = doc.GetElement(new ElementId(checked((int)originalId))) as Toposolid
                ?? throw new InvalidOperationException($"原地形 {originalId} 不存在。");
            var comparisonFocus = TryComputeComparisonFocus(doc, records);
            var (sectionStart, sectionEnd) = comparisonFocus.HasValue
                ? ComputeFocusedSectionLine(original, comparisonFocus.Value)
                : ComputeMaxDropSectionLine(original);
            // D1（2026-07-10）：剖面左右向與 (a)/(c) 的 3D 視角一致。
            var orientationView = ResolveComparisonOrientationView(doc, records);
            (sectionStart, sectionEnd) = AlignSectionLineToViewRight(sectionStart, sectionEnd, orientationView);
            // 三輪：剖面裁到主要挖填範圍＋周遭，全欄共用同一裁切、比例自動放大。
            (sectionStart, sectionEnd) = TrimSectionLineToDisturbance(
                doc, records, original, sectionStart, sectionEnd);

            var allDesignIds = records
                .Select(r => new ElementId(checked((int)r.Value<long>("DesignToposolidId"))))
                .ToList();
            var allSchemeFloorIds = CollectAllSchemeFloorIds(doc);

            // 3-0. 原始地形基準欄（使用者需求 2026-07-08：最前面加原始地形，方案才有比較基準）。
            // 原地形已被整地標記拆除，3D/坡度用「整地-原地形顯示」篩選現形；隱藏全部方案設計地形。
            var schemeColumns = new List<(string Name, long[] ViewIds, List<string> Steps, long? DesignId)>();
            var baselineSteps = new List<string>();
            Logger.Info("[整地比較] 原始地形基準欄開始");
            try
            {
                long base3dId;
                using (var transaction = new Transaction(doc, "建立原始地形基準視圖"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    transaction.Start();
                    var baseView = CreateContextSchemeView3D(doc, _uiApp.ActiveUIDocument.ActiveView);
                    baseView.Name = UniqueViewName(doc, "整地-原始地形");
                    var revealId = EnsureRevealPhaseFilter(doc);
                    if (revealId != ElementId.InvalidElementId)
                    {
                        baseView.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(revealId);
                    }

                    if (doc.GetElement(original.Id) is Element originalElement
                        && originalElement.IsHidden(baseView))
                    {
                        baseView.UnhideElements(new List<ElementId> { original.Id });
                    }

                    HideElementsSafely(doc, baseView, allDesignIds.Concat(allSchemeFloorIds).ToList());
                    ApplyExtentLineCategoryVisibility(doc, baseView, null); // 原始地形欄不顯示任何範圍線
                    baseView.SaveOrientationAndLock();
                    base3dId = baseView.Id.GetIdValue();
                    transaction.Commit();
                }

                baselineSteps.Add("基準視圖");
                var baseSlopeId = DuplicateSchemeView(doc, base3dId, "整地-原始地形-坡度");
                TrySubStep(baselineSteps, "坡度熱區", () =>
                    CreateSlopeHeatmapForTerrain(doc, original, baseSlopeId, sampleStepMeters: 3.0));
                ApplyFocusSectionBoxToViewId(doc, base3dId, comparisonFocus);
                ApplyFocusSectionBoxToViewId(doc, baseSlopeId, comparisonFocus);

                long baseSectionId, baseIsoId;
                using (var transaction = new Transaction(doc, "建立原始地形基準剖面"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    transaction.Start();
                    // 十七輪：剖面框高度按實際取樣 Z 範圍；Toposolid 剖切已隱藏（見
                    // CreateSchemeSectionView），基準欄的地表輪廓以同源取樣實線呈現。
                    var baseOriginalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
                    var baseZRange = ComputeSectionZRange(
                        new IReadOnlyList<Solid>[] { baseOriginalSolids }, sectionStart, sectionEnd);
                    var baseSection = CreateSchemeSectionView(
                        doc,
                        "原始地形",
                        sectionStart,
                        sectionEnd,
                        allDesignIds.Concat(allSchemeFloorIds).ToList(),
                        revealDemolished: true,
                        focus: comparisonFocus,
                        zRange: baseZRange);
                    AddSurfaceProfileToSection(doc, baseSection, baseOriginalSolids,
                        sectionStart, sectionEnd, EnsureSolidDesignProfileStyle(doc));
                    baseSectionId = baseSection.Id.GetIdValue();
                    baseIsoId = CreateSchemeSectionIsoView(
                        doc,
                        "原始地形",
                        sectionStart,
                        sectionEnd,
                        allDesignIds.Concat(allSchemeFloorIds).ToList(),
                        revealDemolished: true,
                        focus: comparisonFocus,
                        styleSource: doc.GetElement(new ElementId(checked((int)base3dId))) as View)
                        .Id.GetIdValue();
                    transaction.Commit();
                }

                baselineSteps.Add("剖面×2");
                schemeColumns.Add(("原始地形", new[] { base3dId, baseSlopeId, baseSectionId, baseIsoId }, baselineSteps, null));
            }
            catch (Exception ex)
            {
                baselineSteps.Add($"失敗：{ex.GetType().Name}: {ex.Message}");
                Logger.Error("[整地比較] 原始地形基準欄失敗", ex);
                schemeColumns.Add(("原始地形", Array.Empty<long>(), baselineSteps, null));
            }

            // 3. 每方案確保四個視圖（逐方案容錯，沿用 presentation 的 TrySubStep 模式）
            foreach (var record in records)
            {
                var designId = record.Value<long>("DesignToposolidId");
                var designElementId = new ElementId(checked((int)designId));
                var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
                Logger.Info($"[整地比較] 方案欄開始：{schemeName}（design {designId}）");
                var steps = new List<string>();
                try
                {
                    var view3dId = EnsureSchemeView(doc, designId, record, steps);
                    var slopeViewId = DuplicateSchemeView(doc, view3dId, $"整地-{schemeName}-坡度比較");
                    TrySubStep(steps, "坡度熱區", () =>
                        CreateSlopeHeatmap(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = slopeViewId,
                            ["sampleStepMeters"] = 3.0
                        }));
                    ApplyFocusSectionBoxToViewId(doc, view3dId, comparisonFocus);
                    ApplyFocusSectionBoxToViewId(doc, slopeViewId, comparisonFocus);
                    TrySubStep(steps, "範圍線", () =>
                        CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));
                    // D2/D3/D11（2026-07-10）：每個方案欄都標高標註＋原GL/整地後合併標籤＋坡率文字。
                    // 三輪：坡度熱區 (b) 也要關鍵高程（同樣拉線到圖外）。
                    // 十七輪：全部標註帶 labelScaleHint——槽位高度按預估出圖比例換算實際字高。
                    var fullCell = EstimateComparisonCellMm(records.Count + 1, halfWidthCell: false);
                    var halfCell = EstimateComparisonCellMm(records.Count + 1, halfWidthCell: true);
                    var fullCellHint = EstimateLabelScaleHint(comparisonFocus, fullCell.CellWMm, fullCell.CellHMm);
                    var halfCellHint = EstimateLabelScaleHint(comparisonFocus, halfCell.CellWMm, halfCell.CellHMm);
                    TrySubStep(steps, "標高標註", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = view3dId,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = true,
                            ["labelScaleHint"] = fullCellHint
                        }));
                    // 2026-07-11 十四輪：十一輪移除強制單側後、十二輪加 CropBox 硬裁切後，
                    // 實機重驗仍見 (b)(c) 中間夾縫互撞——CropBox 對等角 3D 視圖的裁切效果
                    // 與 2D 視圖不同（3D 視圖的 crop 只決定 viewport 外框，不會真的裁掉超出
                    // 範圍的 TextNote），六輪／十二輪的假設都不成立。這是結構性問題：(b)(c)
                    // 是緊貼並排的半寬視圖，唯一保證不互撞的做法是各自只往「遠離對方」的方向
                    // 拉標籤；七~十輪已把「強制單側時項目變多」的擁擠問題用高度堆疊解決，
                    // 恢復強制單側不會走回舊路的疊字問題。(b) 是左半，強制只往左。
                    TrySubStep(steps, "標高標註(坡度)", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = slopeViewId,
                            ["annotateFloors"] = false,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = true,
                            ["labelSide"] = -1,
                            ["labelScaleHint"] = halfCellHint
                        }));

                    var otherDesigns = allDesignIds
                        .Where(id => id != designElementId)
                        .Concat(CollectOtherSchemeFloorIds(doc, record))
                        .ToList();
                    long sectionId = 0, isoId = 0;
                    using (var transaction = new Transaction(doc, "建立比較剖面視圖"))
                    {
                        GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                        transaction.Start();
                        // 十七輪：剖面框高度按原＋設計實際取樣 Z 範圍（挖方上緣不再被切）；
                        // Toposolid 剖切已隱藏，設計實線＋原地形虛線（限擾動段）同源繪製。
                        var schemeOriginalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
                        var schemeDesignSolids =
                            doc.GetElement(designElementId) is Toposolid designToposolid
                                ? RevitToposolidGradingAdapter.CollectSolids(designToposolid)
                                : new List<Solid>();
                        var schemeZRange = ComputeSectionZRange(
                            new IReadOnlyList<Solid>[] { schemeOriginalSolids, schemeDesignSolids },
                            sectionStart, sectionEnd);
                        var section = CreateSchemeSectionView(
                            doc, schemeName, sectionStart, sectionEnd, otherDesigns,
                            focus: comparisonFocus, zRange: schemeZRange);
                        AddCutFillRegionsToSection(doc, section, record, sectionStart, sectionEnd);
                        AddSurfaceProfileToSection(doc, section, schemeDesignSolids,
                            sectionStart, sectionEnd, EnsureSolidDesignProfileStyle(doc));
                        AddOriginalProfileToSection(doc, section, record, sectionStart, sectionEnd);
                        // 三輪：剖面站點標高——原GL／整地後，標籤在圖上方、細線下引。
                        AddSectionStationLabels(doc, section, record, sectionStart, sectionEnd);
                        sectionId = section.Id.GetIdValue();
                        // D9：剖立體只顯示本案設計地形——原地形一併隱藏；表現法承接 (a) 欄視圖。
                        isoId = CreateSchemeSectionIsoView(
                            doc, schemeName, sectionStart, sectionEnd,
                            otherDesigns.Concat(new[] { original.Id }).ToList(),
                            focus: comparisonFocus,
                            styleSource: doc.GetElement(new ElementId(checked((int)view3dId))) as View)
                            .Id.GetIdValue();
                        transaction.Commit();
                    }

                    // 三輪：剖立體 (c) 也要關鍵高程（拉線到圖外）；十四輪：(c) 是右半，強制只往右
                    // 拉（理由同上——CropBox 對 3D 視圖無法硬裁 TextNote，恢復強制單側）。
                    TrySubStep(steps, "標高標註(剖立體)", () =>
                        AnnotateGradingScheme(new JObject
                        {
                            ["designToposolidId"] = designId,
                            ["viewId"] = isoId,
                            ["annotateFloors"] = false,
                            ["annotateDaylight"] = true,
                            ["annotateSlopeLabel"] = false,
                            ["labelSide"] = 1,
                            ["labelScaleHint"] = halfCellHint
                        }));

                    steps.Add("剖面×2");
                    schemeColumns.Add((schemeName, new[] { view3dId, slopeViewId, sectionId, isoId }, steps, designId));
                }
                catch (Exception ex)
                {
                    // 帶例外型別：Revit 例外常見空訊息，只印 Message 會把根因吃掉（2026-07-08 實機教訓）。
                    steps.Add($"失敗：{ex.GetType().Name}: {ex.Message}");
                    Logger.Error($"[整地比較] 方案欄失敗：{schemeName}", ex);
                    schemeColumns.Add((schemeName, Array.Empty<long>(), steps, designId));
                }
            }

            var usable = schemeColumns.Where(c => c.ViewIds.Length == 4).ToList();
            if (usable.Count == 0)
            {
                throw new InvalidOperationException(
                    "沒有任何方案成功備妥四個視圖：" + string.Join("；",
                        schemeColumns.Select(c => $"{c.Name}[{string.Join(",", c.Steps)}]")));
            }

            // 4. 建圖紙＋格局排版＋明細表（單一交易）
            Logger.Info($"[整地比較] 圖紙排版開始（{usable.Count} 欄）");
            long sheetIdValue;
            using (var transaction = new Transaction(doc, "建立方案總比較圖紙"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                var sheet = ViewSheet.Create(doc, titleBlock.TitleBlockId);
                sheet.Name = UniqueSheetName(doc, "整地方案總比較");
                sheet.SheetNumber = NextComparisonSheetNumber(doc);
                sheetIdValue = sheet.Id.GetIdValue();
                doc.Regenerate();

                var titleBlockBox = GetSheetBoundsOrDefault(doc, sheet, 1189, 841);
                var margin = UnitUtils.ConvertToInternalUnits(12, UnitTypeId.Millimeters);
                var originX = titleBlockBox.Min.X + margin;
                var topY = titleBlockBox.Max.Y - margin;
                var width = (titleBlockBox.Max.X - titleBlockBox.Min.X) - (2 * margin);
                var height = (titleBlockBox.Max.Y - titleBlockBox.Min.Y) - (2 * margin);
                // 2026-07-10 五輪：4→9mm——(a) 列底部標籤與 (b)(c) 列頂部標籤太近會互撞，加大帶距。
                var gap = UnitUtils.ConvertToInternalUnits(9, UnitTypeId.Millimeters);
                // 2026-07-10 二輪：版面公釐常數按圖紙高度對 A0 縮放（A2≈0.71），
                // 否則小圖框上表格與圖例吃掉過多高度、行距被壓爆。
                var mmScale = Math.Max(0.6, Math.Min(1.0, height / MetersToFeet(0.817)));
                // 2026-07-10 D6/D7/D12：標籤欄縮窄 118→64mm、欄頂加標題帶、明細表 fit 內容高。
                var labelWidth = UnitUtils.ConvertToInternalUnits(64 * mmScale, UnitTypeId.Millimeters);
                var titleBandHeight = UnitUtils.ConvertToInternalUnits(16 * mmScale, UnitTypeId.Millimeters);
                var fitScheduleMm = SheetLayoutMath.ComputeFitScheduleHeight(
                    MaxLineCount(ComparisonTableHeaders),
                    records.Select(r => MaxLineCount(BuildComparisonRowValues(r))).ToList(),
                    ComparisonHeaderTextMm * mmScale, ComparisonBodyTextMm * mmScale,
                    ComparisonTitleBandMm * mmScale, ComparisonRowPaddingMm * mmScale);
                var scheduleHeight = Math.Min(
                    UnitUtils.ConvertToInternalUnits(fitScheduleMm, UnitTypeId.Millimeters),
                    height * 0.4);
                var layout = SheetLayoutMath.ComputeComparisonGridV2(
                    width, height, usable.Count, gap, labelWidth, titleBandHeight, scheduleHeight);

                // 欄頂方案名稱大標（D6）。字級隨圖紙縮放，型式名帶字級避免污染舊圖紙。
                var columnTitleType = EnsureScaledTextNoteType(doc, "整地欄大標", 10.0, mmScale);
                for (var column = 0; column < usable.Count; column++)
                {
                    var titleCell = layout.TitleCells[column];
                    TextNote.Create(doc, sheet.Id,
                        new XYZ(
                            originX + titleCell.X + (titleCell.Width / 2),
                            topY - titleCell.Y - (titleCell.Height * 0.12), 0),
                        usable[column].Name, new TextNoteOptions
                        {
                            TypeId = columnTitleType,
                            HorizontalAlignment = HorizontalTextAlignment.Center
                        });
                }

                string[] rowLabels = { "(a) 3D", "(b) 坡度分級", "(c) 剖面3D", "(d) 剖面" };
                var rowLabelType = EnsureScaledTextNoteType(doc, "整地圖說列標籤", 9.0, mmScale);
                var pairedRowLabelType = EnsureScaledTextNoteType(doc, "整地圖說並排列標籤", 7.0, mmScale);
                var labelColors = new[]
                {
                    new Color(96, 119, 92),
                    new Color(56, 132, 200),
                    new Color(120, 84, 160),
                    new Color(176, 80, 68)
                };
                foreach (var column in usable)
                {
                    for (var row = 0; row < 4; row++)
                    {
                        if (!(doc.GetElement(new ElementId(checked((int)column.ViewIds[row]))) is View view))
                        {
                            continue;
                        }

                        var owner = row == 1 && column.DesignId.HasValue
                            ? new ElementId(checked((int)column.DesignId.Value))
                            : null;
                        ApplyExtentLineCategoryVisibility(doc, view, owner);
                    }
                }

                for (var row = 0; row < layout.RowLabelCells.Count; row++)
                {
                    var cell = layout.RowLabelCells[row];
                    DrawRowLabelCard(
                        doc, sheet, cell, originX, topY, rowLabels[row],
                        row == 1 || row == 2 ? pairedRowLabelType : rowLabelType,
                        labelColors[row]);
                }
                DrawSlopeLegendOnSheet(doc, sheet, layout.LegendCell, originX, topY, mmScale);
                DrawCutFillLegendChip(doc, sheet, layout.RowLabelCells[3], originX, topY, mmScale);

                // 2026-07-10 六輪使用者裁決：同一列（同案例類型，如全部方案的 (a)3D）跨欄
                // 必須套同一比例，「同尺度、同位置」才是真的可比較——逐欄各自 fit-to-cell
                // 只保證各自撐滿格子，比例會因內容差異各自不同。改列優先兩段式：
                // 第一段每格建 viewport 但只投票各自的「需求比例」；取列內最大值為共用比例；
                // 第二段全部套用同一比例＋置中。
                var noTitleViewportTypeId = EnsureNoTitleViewportType(doc);
                for (var row = 0; row < 4; row++)
                {
                    var rowViewports = new Viewport[usable.Count];
                    var rowViews = new View[usable.Count];
                    var rowCenters = new XYZ[usable.Count];
                    var requiredScales = new List<int>();
                    for (var column = 0; column < usable.Count; column++)
                    {
                        var cell = layout.SchemeCells[row][column];
                        var center = new XYZ(
                            originX + cell.X + (cell.Width / 2),
                            topY - cell.Y - (cell.Height / 2), 0);
                        rowCenters[column] = center;
                        var placeableId = ResolvePlaceableViewId(
                            doc, sheet, usable[column].ViewIds[row], usable[column].Steps, $"第{row + 1}列視圖");
                        var viewElementId = new ElementId(checked((int)placeableId));
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewElementId))
                        {
                            usable[column].Steps.Add($"第{row + 1}列視圖無法排入（複製亦失敗），略過");
                            continue;
                        }

                        var viewport = Viewport.Create(doc, sheet.Id, viewElementId, center);
                        if (noTitleViewportTypeId != ElementId.InvalidElementId)
                        {
                            viewport.ChangeTypeId(noTitleViewportTypeId);
                        }

                        rowViewports[column] = viewport;
                        if (doc.GetElement(viewElementId) is View placedView)
                        {
                            rowViews[column] = placedView;
                            var required = ComputeRequiredScale(doc, viewport, placedView, cell.Width, cell.Height);
                            if (required.HasValue)
                            {
                                requiredScales.Add(required.Value);
                            }
                        }
                    }

                    int? sharedScale = requiredScales.Count > 0 ? requiredScales.Max() : (int?)null;
                    for (var column = 0; column < usable.Count; column++)
                    {
                        if (rowViewports[column] == null || rowViews[column] == null)
                        {
                            continue;
                        }

                        var cell = layout.SchemeCells[row][column];
                        FitViewportToCell(doc, rowViewports[column], rowViews[column],
                            cell.Width, cell.Height, rowCenters[column],
                            snapToStandardScale: false, forceScale: sharedScale);
                    }
                }

                // 內建明細表一律建好（使用者裁決）；native 模式排明細表、custom 模式排自訂表格。
                var comparisonSchedule = EnsureComparisonSchedule(doc);
                if (scheduleStyle == "native")
                {
                    ApplyComparisonScheduleColumnWidths(comparisonSchedule, width);
                    PlaceScheduleOnSheet(doc, sheet, comparisonSchedule,
                        new XYZ(originX, topY - layout.ScheduleCell.Y, 0));
                }
                else
                {
                    DrawComparisonTableOnSheet(doc, sheet, records,
                        new XYZ(originX, topY - layout.ScheduleCell.Y, 0),
                        width,
                        layout.ScheduleCell.Height,
                        mmScale);
                }

                transaction.Commit();
            }

            // 5. PDF
            object pdf = null;
            if (export)
            {
                try
                {
                    pdf = ExportSheetsPdf(new JObject
                    {
                        ["sheetIds"] = new JArray(new[] { sheetIdValue }),
                        ["outputPath"] = parameters["outputPath"]?.Value<string>(),
                        ["combine"] = true
                    });
                }
                catch (Exception ex) { pdf = new { Success = false, Error = ex.Message }; }
            }

            object excel = null;
            try
            {
                excel = ExportGradingComparison(new JObject
                {
                    ["outputPath"] = DeriveGradingExcelOutputPath(doc, parameters, "整地方案總比較"),
                    ["includeScreenshots"] = true
                });
            }
            catch (Exception ex)
            {
                excel = new { Success = false, Error = ex.Message };
            }

            return new
            {
                SheetId = sheetIdValue,
                SchemeCount = usable.Count,
                Schemes = schemeColumns.Select(c => new { c.Name, c.ViewIds, c.Steps }),
                Pdf = pdf,
                Excel = excel,
                ScheduleStyle = scheduleStyle,
                UsedBlankTitleBlock = titleBlock.UsedBlankSheet,
                TitleBlockMessage = titleBlock.Message,
                Message = $"方案總比較圖紙完成（{usable.Count} 欄），已嘗試匯出 PDF 與 Excel。"
                    + (titleBlock.UsedBlankSheet ? " 本次使用空白圖紙。" : string.Empty)
            };
        }

        /// <summary>
        /// D8 修正（2026-07-10 二輪）：Revit 一個視圖只能排一張圖紙——書籤視圖被
        /// 先跑的出圖佔走時，複製一份（含標註）再排，不再靜默缺格。
        /// 呼叫端須已在交易內。回傳可排的視圖 ID（複製失敗時回原 ID，讓後續檢查記失敗）。
        /// </summary>
        private static long ResolvePlaceableViewId(
            Document doc, ViewSheet sheet, long viewId, List<string> steps, string label)
        {
            var elementId = new ElementId(checked((int)viewId));
            if (Viewport.CanAddViewToSheet(doc, sheet.Id, elementId))
            {
                return viewId;
            }

            if (doc.GetElement(elementId) is View view)
            {
                try
                {
                    var duplicateId = view.Duplicate(ViewDuplicateOption.WithDetailing);
                    if (doc.GetElement(duplicateId) is View duplicate)
                    {
                        duplicate.Name = UniqueViewName(doc, $"{view.Name}-排圖副本");
                    }

                    steps?.Add($"{label}已在其他圖紙→複製排圖");
                    return duplicateId.GetIdValue();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 複製失敗維持原 ID，由後續 CanAddViewToSheet 檢查記錄略過。
                }
            }

            return viewId;
        }

        /// <summary>比較圖紙編號 GRD-C01 起流水。</summary>
        private static string NextComparisonSheetNumber(Document doc)
        {
            var numbers = new HashSet<string>(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(sheet => sheet.SheetNumber));
            for (var index = 1; index < 100; index++)
            {
                var candidate = $"GRD-C{index:00}";
                if (!numbers.Contains(candidate)) { return candidate; }
            }

            return $"GRD-C{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }

        /// <summary>
        /// 刪除整地方案（破壞性）：刪設計地形（記錄隨 ES 一併消失）＋方案相關視圖；
        /// 若刪後登記簿為空，解除原地形的「拆除」階段設定使其復原顯示。
        /// </summary>
        private object RemoveGradingScheme(JObject parameters)
        {
            if (parameters["confirm"]?.Value<bool?>() != true)
            {
                throw new InvalidOperationException(
                    "remove_grading_scheme 為破壞性操作，須帶 confirm=true。");
            }

            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var schemeName = record.Value<string>("SchemeName") ?? design.Id.GetIdValue().ToString();
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;

            // 蒐集要刪的視圖：記錄的方案視圖＋名稱前綴「整地-{方案名}」的衍生視圖
            var prefix = GradingNames.SanitizeElementName($"整地-{schemeName}");
            var viewIds = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(view => !view.IsTemplate && view.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(view => view.Id)
                .ToList();

            var deleted = new List<long>();
            using (var transaction = new Transaction(doc, $"刪除整地方案 {schemeName}"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                foreach (var viewId in viewIds)
                {
                    try { doc.Delete(viewId); deleted.Add(viewId.GetIdValue()); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* 已被圖紙相依刪除 */ }
                }

                deleted.Add(design.Id.GetIdValue());
                doc.Delete(design.Id); // 記錄在設計地形的 ES 上，隨元素一併刪除
                doc.Regenerate();

                var remaining = RevitToposolidGradingAdapter.ReadSchemeRecords(doc).Count;
                if (remaining == 0 && originalId > 0
                    && doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original)
                {
                    original.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(ElementId.InvalidElementId);
                }

                transaction.Commit();
                return new
                {
                    SchemeName = schemeName,
                    DeletedElementIds = deleted,
                    RemainingSchemes = remaining,
                    Message = $"方案「{schemeName}」已刪除（{deleted.Count} 個元素）；剩餘 {remaining} 筆方案。"
                };
            }
        }
    }
}
#endif
