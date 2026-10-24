#if REVIT2024_OR_GREATER
using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private sealed class GradingTitleBlockResolution
        {
            public ElementId TitleBlockId { get; set; }

            public bool UsedBlankSheet { get; set; }

            public string Message { get; set; }
        }

        private static GradingTitleBlockResolution ResolveGradingTitleBlock(
            Document doc, JObject parameters, bool preferA0)
        {
            var choice = TitleBlockChoice.FromParameter(parameters["titleBlockId"]?.Value<long?>());
            if (choice.IsBlank)
            {
                return new GradingTitleBlockResolution
                {
                    TitleBlockId = ElementId.InvalidElementId,
                    UsedBlankSheet = true,
                    Message = "使用者選擇空白圖紙。"
                };
            }

            if (choice.ExplicitId.HasValue)
            {
                var id = new ElementId(checked((int)choice.ExplicitId.Value));
                if (doc.GetElement(id) == null)
                {
                    throw new InvalidOperationException($"找不到指定圖框 ID：{choice.ExplicitId.Value}。");
                }

                return new GradingTitleBlockResolution
                {
                    TitleBlockId = id,
                    UsedBlankSheet = false,
                    Message = "使用使用者選擇的圖框。"
                };
            }

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .ToList();
            var selected = preferA0
                ? symbols.FirstOrDefault(symbol =>
                    symbol.Name.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0
                    || symbol.FamilyName.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0)
                : null;
            selected = selected ?? symbols.FirstOrDefault();
            if (selected != null)
            {
                return new GradingTitleBlockResolution
                {
                    TitleBlockId = selected.Id,
                    UsedBlankSheet = false,
                    Message = preferA0
                        ? "自動選擇 A0 或第一個可用圖框。"
                        : "自動選擇第一個可用圖框。"
                };
            }

            return new GradingTitleBlockResolution
            {
                TitleBlockId = ElementId.InvalidElementId,
                UsedBlankSheet = true,
                Message = "模型沒有可用圖框，已使用空白圖紙。"
            };
        }

        private static BoundingBoxXYZ GetSheetBoundsOrDefault(
            Document doc, ViewSheet sheet, double defaultWidthMillimeters, double defaultHeightMillimeters)
        {
            var titleBlockBox = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement()?.get_BoundingBox(sheet);
            if (titleBlockBox != null)
            {
                return titleBlockBox;
            }

            return new BoundingBoxXYZ
            {
                Min = XYZ.Zero,
                Max = new XYZ(
                    UnitUtils.ConvertToInternalUnits(defaultWidthMillimeters, UnitTypeId.Millimeters),
                    UnitUtils.ConvertToInternalUnits(defaultHeightMillimeters, UnitTypeId.Millimeters),
                    0)
            };
        }

        private static string DeriveGradingExcelOutputPath(Document doc, JObject parameters, string defaultBaseName)
        {
            var explicitExcel = parameters["excelOutputPath"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(explicitExcel))
            {
                return explicitExcel;
            }

            var outputPath = parameters["outputPath"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    return Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".xlsx");
                }
            }

            var projectPath = doc.PathName;
            var projectDir = string.IsNullOrEmpty(projectPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : Path.GetDirectoryName(projectPath);
            return Path.Combine(projectDir, $"{defaultBaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }
    }
}
#endif
