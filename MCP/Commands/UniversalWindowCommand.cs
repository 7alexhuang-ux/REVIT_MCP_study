using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Core;
using RevitMCP.Core.WindowFamily;

namespace RevitMCP.Commands
{
    /// <summary>
    /// ribbon「萬用窗 › 產生窗族」：開啟萬用窗生成對話框。
    /// 不需要 MCP 服務、不需要 AI（SPEC §11、ADR D-008）。對話框為 modal，使用者按「產生」時
    /// 直接在本 command 的 Revit API 內容中呼叫共用引擎（<see cref="WindowFamilyService"/>），
    /// 不經 WebSocket／ExternalEventManager。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class UniversalWindowCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            try
            {
                var dialog = new UniversalWindowDialog(commandData.Application);

                // 以 Revit 主視窗為 owner，避免對話框跑到 Revit 後面
                var helper = new WindowInteropHelper(dialog);
                helper.Owner = commandData.Application.MainWindowHandle;

                dialog.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Logger.Error("開啟萬用窗對話框失敗", ex);
                TaskDialog.Show("萬用窗", "無法開啟萬用窗對話框：" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
