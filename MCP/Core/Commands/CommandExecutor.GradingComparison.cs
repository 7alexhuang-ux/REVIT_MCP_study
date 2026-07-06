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

        /// <summary>建立（或沿用）整地方案比較明細表：Toposolid 類別、欄位=共用參數、過濾方案名有值。</summary>
        private static ViewSchedule EnsureComparisonSchedule(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(candidate => candidate.Name == ComparisonScheduleName);
            if (existing != null)
            {
                return existing;
            }

            var schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_Toposolid));
            schedule.Name = ComparisonScheduleName;
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

        /// <summary>建方案正剖面：沿共同剖面線、階段過濾顯示拆除（原地形虛線疊比）、隱藏其他方案設計地形。</summary>
        private static ViewSection CreateSchemeSectionView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> otherDesignIds)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.Section)
                ?? throw new InvalidOperationException("找不到剖面視圖類型。");

            var mid = (start + end) / 2;
            var lineDirection = (end - start).Normalize();
            var viewDirection = lineDirection.CrossProduct(XYZ.BasisZ);
            var halfLength = start.DistanceTo(end) / 2;
            var depth = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Meters);
            var height = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);

            var transform = Transform.Identity;
            transform.Origin = new XYZ(mid.X, mid.Y, mid.Z);
            transform.BasisX = lineDirection;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = viewDirection;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, -height / 2, 0),
                Max = new XYZ(halfLength, height / 2, depth)
            };

            var section = ViewSection.CreateSection(doc, viewFamilyType.Id, sectionBox);
            section.Name = UniqueViewName(doc, $"整地-{schemeName}-剖面");
            // 顯示「拆除」的原地形（虛線疊比）：階段過濾用「全部顯示」
            var phaseFilter = new FilteredElementCollector(doc)
                .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .FirstOrDefault(filter => filter.Name == "全部顯示" || filter.Name == "Show All");
            if (phaseFilter != null)
            {
                section.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(phaseFilter.Id);
            }

            if (otherDesignIds.Count > 0)
            {
                section.HideElements(otherDesignIds.ToList());
            }

            return section;
        }

        /// <summary>建方案剖面立體圖：等角 3D＋剖面框沿剖切帶（規格風險節：不用透視，避免透視排圖限制）。</summary>
        private static View3D CreateSchemeSectionIsoView(
            Document doc, string schemeName, XYZ start, XYZ end,
            IReadOnlyList<ElementId> isolateIds)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("找不到 3D 視圖類型。");

            var view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            view.Name = UniqueViewName(doc, $"整地-{schemeName}-剖立體");
            view.IsolateElementsTemporary(isolateIds.ToList());
            view.ConvertTemporaryHideIsolateToPermanent();

            var mid = (start + end) / 2;
            var lineDirection = (end - start).Normalize();
            var viewDirection = lineDirection.CrossProduct(XYZ.BasisZ);
            var halfLength = start.DistanceTo(end) / 2;
            var halfBand = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Meters);
            var height = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);
            var transform = Transform.Identity;
            transform.Origin = mid;
            transform.BasisX = lineDirection;
            transform.BasisY = viewDirection;
            transform.BasisZ = XYZ.BasisZ;
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-halfLength, 0, -height / 2),   // 剖面框自剖切線起、只留一側 → 剖開效果
                Max = new XYZ(halfLength, halfBand, height / 2)
            });
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

            var titleBlockId = ResolveA0TitleBlock(doc, parameters);
            var export = parameters["export"]?.Value<bool?>() ?? true;

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

            // 2. 共同剖面線（取第一筆記錄的原地形）
            var originalId = records[0].Value<long>("OriginalToposolidId");
            var original = doc.GetElement(new ElementId(checked((int)originalId))) as Toposolid
                ?? throw new InvalidOperationException($"原地形 {originalId} 不存在。");
            var (sectionStart, sectionEnd) = ComputeMaxDropSectionLine(original);

            var allDesignIds = records
                .Select(r => new ElementId(checked((int)r.Value<long>("DesignToposolidId"))))
                .ToList();

            // 3. 每方案確保四個視圖（逐方案容錯，沿用 presentation 的 TrySubStep 模式）
            var schemeColumns = new List<(string Name, long[] ViewIds, List<string> Steps)>();
            foreach (var record in records)
            {
                var designId = record.Value<long>("DesignToposolidId");
                var designElementId = new ElementId(checked((int)designId));
                var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
                var steps = new List<string>();
                try
                {
                    var view3dId = EnsureSchemeView(doc, designId, record, steps);
                    var slopeViewId = DuplicateSchemeView(doc, view3dId, $"整地-{schemeName}-坡度比較");
                    TrySubStep(steps, "坡度熱區", () =>
                        CreateSlopeHeatmap(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));
                    TrySubStep(steps, "範圍線", () =>
                        CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = slopeViewId }));

                    var otherDesigns = allDesignIds.Where(id => id != designElementId).ToList();
                    long sectionId = 0, isoId = 0;
                    using (var transaction = new Transaction(doc, "建立比較剖面視圖"))
                    {
                        GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                        transaction.Start();
                        sectionId = CreateSchemeSectionView(doc, schemeName, sectionStart, sectionEnd, otherDesigns)
                            .Id.GetIdValue();
                        var isolate = new List<ElementId> { designElementId, original.Id };
                        foreach (var floorId in (record["FloorIds"] as JArray)?.Values<long>() ?? Enumerable.Empty<long>())
                        {
                            isolate.Add(new ElementId(checked((int)floorId)));
                        }

                        isoId = CreateSchemeSectionIsoView(doc, schemeName, sectionStart, sectionEnd, isolate)
                            .Id.GetIdValue();
                        transaction.Commit();
                    }

                    steps.Add("剖面×2");
                    schemeColumns.Add((schemeName, new[] { view3dId, slopeViewId, sectionId, isoId }, steps));
                }
                catch (Exception ex)
                {
                    steps.Add($"失敗：{ex.Message}");
                    schemeColumns.Add((schemeName, Array.Empty<long>(), steps));
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
            long sheetIdValue;
            using (var transaction = new Transaction(doc, "建立方案總比較圖紙"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.Name = UniqueSheetName(doc, "整地方案總比較");
                sheet.SheetNumber = NextComparisonSheetNumber(doc);
                sheetIdValue = sheet.Id.GetIdValue();
                doc.Regenerate();

                var titleBlockBox = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement()?.get_BoundingBox(sheet)
                    ?? throw new InvalidOperationException("比較圖紙沒有圖框。");
                var margin = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Millimeters);
                var originX = titleBlockBox.Min.X + margin;
                var topY = titleBlockBox.Max.Y - margin;
                var width = (titleBlockBox.Max.X - titleBlockBox.Min.X) - (2 * margin);
                var height = (titleBlockBox.Max.Y - titleBlockBox.Min.Y) - (2 * margin);
                var gap = UnitUtils.ConvertToInternalUnits(8, UnitTypeId.Millimeters);
                var labelWidth = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
                var layout = SheetLayoutMath.ComputeComparisonGrid(width, height, usable.Count, gap, labelWidth);

                string[] rowLabels = { "(a) 3D", "(b) 坡度", "(c) 剖面", "(c2) 剖立體", "(d) 明細" };
                var textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .FirstElementId();
                for (var row = 0; row < layout.RowLabelCells.Count; row++)
                {
                    var cell = layout.RowLabelCells[row];
                    TextNote.Create(doc, sheet.Id,
                        new XYZ(originX + cell.X, topY - cell.Y - (cell.Height / 2), 0),
                        rowLabels[row], textType);
                }

                for (var column = 0; column < usable.Count; column++)
                {
                    for (var row = 0; row < 4; row++)
                    {
                        var cell = layout.SchemeCells[row][column];
                        var center = new XYZ(
                            originX + cell.X + (cell.Width / 2),
                            topY - cell.Y - (cell.Height / 2), 0);
                        var viewElementId = new ElementId(checked((int)usable[column].ViewIds[row]));
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewElementId))
                        {
                            usable[column].Steps.Add($"第{row + 1}列視圖已在其他圖紙，略過");
                            continue;
                        }

                        var viewport = Viewport.Create(doc, sheet.Id, viewElementId, center);
                        if (doc.GetElement(viewElementId) is View placedView)
                        {
                            FitViewportToCell(doc, viewport, placedView, cell.Width, cell.Height, center);
                        }
                    }
                }

                var schedule = EnsureComparisonSchedule(doc);
                PlaceScheduleOnSheet(doc, sheet, schedule,
                    new XYZ(originX + layout.ScheduleCell.X, topY - layout.ScheduleCell.Y, 0));
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

            return new
            {
                SheetId = sheetIdValue,
                SchemeCount = usable.Count,
                Schemes = schemeColumns.Select(c => new { c.Name, c.ViewIds, c.Steps }),
                Pdf = pdf,
                Message = $"方案總比較圖紙完成（{usable.Count} 欄）。"
            };
        }

        /// <summary>找 A0 圖框：titleBlockId 參數優先；否則型別名或族名含「A0」者；找不到→引導。</summary>
        private static ElementId ResolveA0TitleBlock(Document doc, JObject parameters)
        {
            var explicitId = parameters["titleBlockId"]?.Value<long?>() ?? 0;
            if (explicitId > 0)
            {
                return new ElementId(checked((int)explicitId));
            }

            var a0 = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .FirstOrDefault(symbol =>
                    symbol.Name.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0
                    || symbol.FamilyName.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0);
            return a0?.Id ?? throw new InvalidOperationException(
                "模型沒有 A0 圖框；請載入 A0 公制圖框族群，或以 titleBlockId 指定圖框。");
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

        // Task 7 佔位：實作於後續任務替換為完整刪除方案邏輯。
        private object RemoveGradingScheme(JObject parameters)
        {
            throw new NotImplementedException("remove_grading_scheme 尚未實作");
        }
    }
}
#endif
