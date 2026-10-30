using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.WindowFamily;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 萬用窗族生成 (generate_window_family)

        /// <summary>
        /// Phase 1 萬用窗族生成器（SPEC docs/universal-window/SPEC.md §8）。
        /// 吃規格 JSON → 以 Metric Window.rft 建族（擠出／空心／符號線／UW_ 參數）→ 存 .rfa → 載入目前專案。
        /// 本方法由 ExecuteCommand 在 ExternalEventManager 的 UI 執行緒上呼叫；族文件的交易在
        /// WindowFamilyBuilder 內開，專案端的型別啟用另開一次交易。
        /// </summary>
        private object GenerateWindowFamily(JObject parameters)
        {
            var spec = WindowFamilySpec.Parse(parameters);

            // 與 add-in 對話框共用同一個進入點（目標專案檢查＋建族＋載入）
            WindowFamilyBuildResult result = WindowFamilyService.Generate(_uiApp, spec);

            var d = result.Derived;
            return new
            {
                Success = true,
                familyPath = result.FamilyPath,
                familyName = result.FamilyName,
                typeName = result.TypeName,
                loaded = result.Loaded,
                familyId = result.FamilyId,
                symbolId = result.SymbolId,
                derived = new
                {
                    sashCount = d.SashCount,
                    screenCount = d.ScreenCount,
                    openableCount = d.OpenableCount,
                    glassArea_m2 = Math.Round(d.GlassAreaM2, 3),
                    daylightArea_m2 = Math.Round(d.DaylightAreaM2, 3),
                    openableArea_m2 = Math.Round(d.OpenableAreaM2, 3),
                    moWidth = Math.Round(d.MoWidth, 1),
                    moHeight = Math.Round(d.MoHeight, 1),
                    rowHeights = d.RowHeights.Select(v => Math.Round(v, 1)).ToArray(),
                    colWidths = d.ColWidths.Select(v => Math.Round(v, 1)).ToArray()
                },
                ifc = new
                {
                    entity = "IfcWindow",
                    partitioningType = result.IfcPartitioningType
                },
                operations = result.Operations,
                templateProbe = result.Probe == null ? null : new
                {
                    templatePath = result.Probe.TemplatePath,
                    exteriorSign = result.Probe.ExteriorSign,
                    exteriorSource = result.Probe.ExteriorSource,
                    exteriorViewName = result.Probe.ExteriorViewName,
                    exteriorViewDirection = result.Probe.ExteriorViewDirection,
                    hostWallOrientation = result.Probe.HostWallOrientation,
                    referencePlaneNames = result.Probe.ReferencePlaneNames,
                    viewNames = result.Probe.ViewNames
                },
                warnings = result.Warnings,
                Message = result.Loaded
                    ? $"已生成族「{result.FamilyName}」型別「{result.TypeName}」並載入專案：{result.FamilyPath}"
                    : $"已生成族「{result.FamilyName}」型別「{result.TypeName}」：{result.FamilyPath}"
            };
        }

        #endregion
    }
}
