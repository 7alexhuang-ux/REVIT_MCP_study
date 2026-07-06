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
    }
}
#endif
