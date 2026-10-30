using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>
    /// 萬用窗生成的共用進入點：規格 → 檢查目標專案 → 呼叫 <see cref="WindowFamilyBuilder"/>。
    /// MCP 工具（CommandExecutor.GenerateWindowFamily）與 add-in 對話框（UniversalWindowDialog）都走這裡，
    /// 兩個入口只做「組規格」，不各自畫幾何（SPEC §11、ADR D-008）。
    /// 必須在 Revit API 有效的執行緒呼叫：MCP 端由 ExternalEventManager 提供，add-in 端是 IExternalCommand 本身。
    /// </summary>
    public static class WindowFamilyService
    {
        /// <summary>
        /// 生成族並（視規格）載入目前專案。
        /// 規格無效、找不到樣板、載入衝突等一律丟 <see cref="WindowSpecException"/> 或 Exception，訊息為繁體中文。
        /// </summary>
        public static WindowFamilyBuildResult Generate(UIApplication uiApp, WindowFamilySpec spec)
        {
            if (uiApp == null) throw new ArgumentNullException("uiApp");
            if (spec == null) throw new ArgumentNullException("spec");

            Document targetDoc = null;
            if (spec.LoadIntoProject)
            {
                var uiDoc = uiApp.ActiveUIDocument;
                targetDoc = uiDoc == null ? null : uiDoc.Document;
                if (targetDoc == null)
                    throw new Exception("loadIntoProject = true 但目前沒有開啟中的專案文件。");
                if (targetDoc.IsFamilyDocument)
                    throw new Exception("目前開啟的是族文件，無法載入。請切換到專案再執行。");
            }

            var builder = new WindowFamilyBuilder(uiApp.Application);
            return builder.Build(spec, targetDoc);
        }
    }
}
