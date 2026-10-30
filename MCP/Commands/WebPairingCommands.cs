using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Configuration;
using RevitMCP.Core;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Commands
{
    /// <summary>功能區「網頁配對」：顯示 6 位數配對碼（5 分鐘有效），在網頁輸入後網頁取得 token。</summary>
    [Transaction(TransactionMode.Manual)]
    public class WebPairCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var running = Application.SocketService != null && Application.SocketService.IsRunning;
                var auth = ViewSyncAuthService.Instance;
                var code = auth.IssuePairingCode();
                var minutes = Math.Max(1, (int)Math.Round((auth.Codes.ExpiresAtUtc - DateTime.UtcNow).TotalMinutes));
                var text = "配對碼：  " + code + "\n\n"
                    + "請在議題管理網頁的 3D 檢視器「Revit」卡片輸入這組號碼（" + minutes + " 分鐘內有效，只能用一次）。\n\n"
                    + "目前允許連線的網站：\n  " + string.Join("\n  ", auth.AllowedOrigins) + "\n\n"
                    + "已配對裝置：" + auth.PairedCount + " 台";
                if (!running) text += "\n\n⚠ MCP 服務尚未啟動，請先按「MCP 服務(開/關)」，網頁才連得上。";
                TaskDialog.Show("網頁配對", text);
                Logger.Info("使用者產生網頁配對碼");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("錯誤", "產生配對碼失敗: " + ex.Message);
                return Result.Failed;
            }
        }
    }

    /// <summary>功能區「撤銷網頁配對」：清空所有已配對的網頁 token 並中斷現有的瀏覽器連線。</summary>
    [Transaction(TransactionMode.Manual)]
    public class WebRevokeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var auth = ViewSyncAuthService.Instance;
                var dialog = new TaskDialog("撤銷網頁配對")
                {
                    MainInstruction = "撤銷全部 " + auth.PairedCount + " 台已配對的網頁裝置？",
                    MainContent = "撤銷後，網頁需要重新輸入配對碼才能再把視角送進 Revit。",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (dialog.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                var count = auth.RevokeAll();
                Logger.Info($"使用者撤銷全部網頁配對（{count} 台）");
                TaskDialog.Show("撤銷網頁配對", "已撤銷 " + count + " 台。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("錯誤", "撤銷失敗: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
