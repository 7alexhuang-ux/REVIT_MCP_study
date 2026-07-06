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
    }
}
#endif
