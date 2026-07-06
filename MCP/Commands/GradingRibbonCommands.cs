#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Core;
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
}
#endif
