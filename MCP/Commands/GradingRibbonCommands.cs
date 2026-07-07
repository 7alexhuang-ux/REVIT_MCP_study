#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json.Linq;
using RevitMCP.Core;
using RevitMCP.Core.Grading;
using RevitMCP.Models;

namespace RevitMCP.Commands
{
    /// <summary>整地 ribbon 按鈕共用邏輯：讀選取集合、組請求、經 CommandExecutor 執行、以 TaskDialog 回報。</summary>
    internal static class GradingRibbonHelper
    {
        /// <summary>以目前選取的地形＋樓板執行整地（mode 指定模式，extra 帶模式參數如 offsetDistance/slopeRatio）。</summary>
        public static Result RunGrade(ExternalCommandData commandData, string mode, JObject extra)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                TaskDialog.Show("整地", "沒有作用中的文件。");
                return Result.Cancelled;
            }

            var doc = uiDoc.Document;
            long toposolidId = 0;
            var floorIds = new List<long>();
            foreach (var id in uiDoc.Selection.GetElementIds())
            {
                var element = doc.GetElement(id);
                if (element is Toposolid && toposolidId == 0)
                {
                    toposolidId = element.Id.GetIdValue();
                }
                else if (element is Floor)
                {
                    floorIds.Add(element.Id.GetIdValue());
                }
            }

            if (toposolidId == 0 || floorIds.Count == 0)
            {
                TaskDialog.Show("整地",
                    "請先在模型中選取「一片地形（Toposolid）」與「至少一片控制樓板（Floor）」，再點此按鈕。");
                return Result.Cancelled;
            }

            var parameters = new JObject
            {
                ["toposolidId"] = toposolidId,
                ["floorIds"] = new JArray(floorIds),
                ["mode"] = mode
            };
            if (extra != null)
            {
                foreach (var property in extra.Properties())
                {
                    parameters[property.Name] = property.Value;
                }
            }

            return Run(uiApp, "grade_toposolid_to_floors", parameters, "整地");
        }

        /// <summary>經 CommandExecutor 執行任意 MCP 命令並回報結果。</summary>
        public static Result Run(UIApplication uiApp, string commandName, JObject parameters, string title)
        {
            try
            {
                var executor = new CommandExecutor(uiApp);
                var response = executor.ExecuteCommand(new RevitCommandRequest
                {
                    CommandName = commandName,
                    Parameters = parameters,
                    RequestId = "ribbon-" + Guid.NewGuid().ToString("N")
                });

                if (response.Success)
                {
                    var message = ExtractMessage(response.Data);
                    TaskDialog.Show(title, string.IsNullOrEmpty(message) ? "完成。" : message);
                    return Result.Succeeded;
                }

                TaskDialog.Show(title, "失敗：" + response.Error);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "例外：" + ex.Message);
                return Result.Failed;
            }
        }

        private static string ExtractMessage(object data)
        {
            if (data == null)
            {
                return null;
            }

            try
            {
                var token = JToken.FromObject(data);
                return token["Message"]?.Value<string>() ?? token.ToString();
            }
            catch (Exception)
            {
                return data.ToString();
            }
        }

        /// <summary>目前選取的摘要字串（對話框顯示用），如「✔ 地形 ×1、樓板 ×2」。</summary>
        public static string DescribeSelection(UIDocument uiDoc)
        {
            var toposolids = 0;
            var floors = 0;
            if (uiDoc?.Document != null)
            {
                foreach (var id in uiDoc.Selection.GetElementIds())
                {
                    var element = uiDoc.Document.GetElement(id);
                    if (element is Toposolid) { toposolids++; }
                    else if (element is Floor) { floors++; }
                }
            }

            return (toposolids >= 1 && floors >= 1 ? "✔" : "⚠") + $" 選取狀態：地形 ×{toposolids}、樓板 ×{floors}";
        }
    }

    /// <summary>整地-投影直貼（footprint_only）：以選取地形＋樓板執行。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradeFootprintCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.RunGrade(commandData, "footprint_only", null);
        }
    }

    /// <summary>整地-Offset 外延（offset_transition，預設外延 5m）：以選取地形＋樓板執行。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradeOffsetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.RunGrade(commandData, "offset_transition",
                new JObject { ["offsetDistance"] = 5.0 });
        }
    }

    /// <summary>整地-放坡（slope_transition，預設坡度 1:2）：以選取地形＋樓板執行。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradeSlopeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.RunGrade(commandData, "slope_transition",
                new JObject { ["slopeRatio"] = "1:2" });
        }
    }

    /// <summary>整地圖說一鍵出圖（create_grading_presentation）：對登記簿全部方案出圖並匯 PDF。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradingPresentationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.Run(commandData.Application, "create_grading_presentation",
                new JObject { ["export"] = true }, "整地圖說一鍵出圖");
        }
    }

    /// <summary>①新整地方案：對話框選模式/參數 → 執行並記錄一筆（重複按＝做下一個方案）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class NewGradingSchemeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var selectionSummary = GradingRibbonHelper.DescribeSelection(uiDoc);
            var dialog = new NewGradingSchemeDialog(selectionSummary);
            if (dialog.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            return GradingRibbonHelper.RunGrade(commandData, dialog.Mode, dialog.ExtraParameters);
        }
    }

    /// <summary>②方案總覽：非模態清單視窗（進度儀表板）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class SchemeOverviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SchemeOverviewWindow.ShowOrActivate(commandData.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>③比較出圖：登記簿全部方案 → C-00c 單張總比較圖紙＋PDF。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradingComparisonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return GradingRibbonHelper.Run(
                commandData.Application, "create_grading_comparison_sheet", new JObject(), "比較出圖");
        }
    }

    /// <summary>樓板邊選取過濾器：只允許點到 Floor 的邊。</summary>
    internal sealed class FloorEdgeSelectionFilter : ISelectionFilter
    {
        private readonly long _restrictToFloorId;

        public FloorEdgeSelectionFilter(long restrictToFloorId = 0)
        {
            _restrictToFloorId = restrictToFloorId;
        }

        public bool AllowElement(Element element)
        {
            return element is Autodesk.Revit.DB.Floor
                && (_restrictToFloorId == 0 || element.Id.GetIdValue() == _restrictToFloorId);
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>V3-S1 板高程/斜板：選取樓板 → 對話框收集意圖 → 斜板逐板點低側邊 → 一次套用。</summary>
    [Transaction(TransactionMode.Manual)]
    public class FloorElevationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("板高程", "沒有作用中的文件。");
                return Result.Cancelled;
            }

            var floors = uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<Autodesk.Revit.DB.Floor>()
                .ToList();
            if (floors.Count == 0)
            {
                TaskDialog.Show("板高程", "請先選取至少一片樓板，再點此按鈕。");
                return Result.Cancelled;
            }

            var rows = floors
                .Select(floor =>
                {
                    var offsetFeet = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
                    var offsetMeters = UnitUtils.ConvertFromInternalUnits(offsetFeet, UnitTypeId.Meters);
                    return ((long)floor.Id.GetIdValue(), $"板 {floor.Id.GetIdValue()}（目前偏移 {offsetMeters:0.00}m）");
                })
                .ToList();

            var dialog = new FloorElevationDialog(rows);
            if (dialog.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            // 斜板：對話框關閉後逐板點低側邊（模態中不能 PickObject）。
            var floorEntries = new JArray();
            foreach (var intent in dialog.Intents)
            {
                var entry = new JObject { ["floorId"] = intent.FloorId };
                switch (intent.Action)
                {
                    case "offset":
                        entry["offsetMeters"] = intent.OffsetMeters;
                        break;
                    case "reset":
                        entry["reset"] = true;
                        break;
                    case "slope":
                        try
                        {
                            var reference = uiDoc.Selection.PickObject(
                                ObjectType.Edge,
                                new FloorEdgeSelectionFilter(intent.FloorId),
                                $"請點選板 {intent.FloorId} 的低側邊（板往那條邊斜下去）");
                            var element = doc.GetElement(reference);
                            var edge = element?.GetGeometryObjectFromReference(reference) as Edge;
                            var curve = edge?.AsCurve();
                            if (curve == null)
                            {
                                TaskDialog.Show("板高程", $"板 {intent.FloorId} 點選的不是有效邊，略過斜板。");
                                continue;
                            }

                            var mid = curve.Evaluate(0.5, true);
                            entry["slope"] = new JObject
                            {
                                ["x"] = UnitUtils.ConvertFromInternalUnits(mid.X, UnitTypeId.Meters),
                                ["y"] = UnitUtils.ConvertFromInternalUnits(mid.Y, UnitTypeId.Meters),
                                ["slopeRatio"] = intent.SlopeRatio
                            };
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            continue; // 使用者 ESC，略過此板的斜板動作。
                        }

                        break;
                    default:
                        continue;
                }

                floorEntries.Add(entry);
            }

            if (floorEntries.Count == 0)
            {
                return Result.Cancelled;
            }

            return GradingRibbonHelper.Run(uiApp, "set_floor_elevations",
                new JObject { ["floors"] = floorEntries }, "板高程");
        }
    }

    /// <summary>V3-S2 邊坡率：連續點樓板邊（ESC 結束），每條邊填坡率（如臨路無障礙 1:12），存為板的邊政策。</summary>
    [Transaction(TransactionMode.Manual)]
    public class EdgeSlopeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("邊坡率", "沒有作用中的文件。");
                return Result.Cancelled;
            }

            var summaries = new List<string>();
            while (true)
            {
                Reference reference;
                try
                {
                    reference = uiDoc.Selection.PickObject(
                        ObjectType.Edge,
                        new FloorEdgeSelectionFilter(),
                        "點選要指定坡率的樓板邊（ESC 結束）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }

                var element = doc.GetElement(reference);
                var edge = element?.GetGeometryObjectFromReference(reference) as Edge;
                var curve = edge?.AsCurve();
                if (element == null || curve == null)
                {
                    TaskDialog.Show("邊坡率", "點選的不是有效的樓板邊，請再試一次。");
                    continue;
                }

                var lengthMeters = UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters);
                var rateDialog = new EdgeRateDialog(
                    $"板 {element.Id.GetIdValue()}、邊長約 {lengthMeters:0.0}m——這條邊的放坡坡率？");
                if (rateDialog.ShowDialog() != true)
                {
                    continue;
                }

                var mid = curve.Evaluate(0.5, true);
                var executor = new CommandExecutor(uiApp);
                var response = executor.ExecuteCommand(new RevitCommandRequest
                {
                    CommandName = "set_floor_edge_slope",
                    Parameters = new JObject
                    {
                        ["floorId"] = element.Id.GetIdValue(),
                        ["x"] = UnitUtils.ConvertFromInternalUnits(mid.X, UnitTypeId.Meters),
                        ["y"] = UnitUtils.ConvertFromInternalUnits(mid.Y, UnitTypeId.Meters),
                        ["slopeRatio"] = rateDialog.SlopeRatio
                    },
                    RequestId = "ribbon-" + Guid.NewGuid().ToString("N")
                });
                summaries.Add(response.Success
                    ? $"✔ 板 {element.Id.GetIdValue()} 邊（{lengthMeters:0.0}m）＝{rateDialog.SlopeRatio}"
                    : $"✘ 板 {element.Id.GetIdValue()}：{response.Error}");
            }

            TaskDialog.Show("邊坡率",
                summaries.Count == 0
                    ? "沒有指定任何邊。"
                    : string.Join("\n", summaries)
                        + "\n\n之後用「新整地方案」放坡時，這些邊會用自己的坡率、其餘邊用預設坡率。");
            return Result.Succeeded;
        }
    }

    /// <summary>整地檢視：選單挑「原地形／某方案」→ 專用視圖顯示（保留樓板），視圖標題含編號＋名稱。</summary>
    [Transaction(TransactionMode.Manual)]
    public class GradingTerrainViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                TaskDialog.Show("整地檢視", "沒有作用中的文件。");
                return Result.Cancelled;
            }

            var records = RevitToposolidGradingAdapter.ReadSchemeRecords(uiDoc.Document)
                .Select(JObject.Parse).ToList();
            if (records.Count == 0)
            {
                TaskDialog.Show("整地檢視", "目前沒有整地方案；請先用「新整地方案」建立至少一個。");
                return Result.Cancelled;
            }

            var options = new List<KeyValuePair<string, long>>
            {
                new KeyValuePair<string, long>("原地形（拆除前）", 0)
            };
            for (var index = 0; index < records.Count; index++)
            {
                var name = records[index].Value<string>("SchemeName") ?? $"方案{index + 1}";
                options.Add(new KeyValuePair<string, long>(
                    $"方案{index + 1}：{name}", records[index].Value<long>("DesignToposolidId")));
            }

            var dialog = new TerrainViewPickerDialog(options);
            if (dialog.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            return GradingRibbonHelper.Run(uiApp, "show_grading_terrain",
                new JObject { ["designToposolidId"] = dialog.SelectedDesignId }, "整地檢視");
        }
    }
}
#endif
