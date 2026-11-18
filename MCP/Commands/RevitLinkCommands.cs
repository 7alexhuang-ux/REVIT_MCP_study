#nullable disable
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Core;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 功能區「在 IM 建立議題」「這個元素有哪些議題」（docs/REVIT_LINK.md §2、ADR D-015）。
    /// 兩顆按鈕與 Claude 指令（open_issue_draft、open_element_issues）共用 CommandExecutor.OpenIssueDraft。
    /// 唯讀：TransactionMode.ReadOnly，Revit 會在任何人嘗試開 Transaction 時直接丟例外（本功能不改模型的第二道保證）。
    /// 網頁要靠 WebSocket 領草稿，所以 MCP 服務沒開、或還沒有任何已配對的網頁裝置時不開瀏覽器，改說明怎麼做。
    /// </summary>
    internal static class RevitLinkRibbon
    {
        internal static Result Run(ExternalCommandData commandData, ref string message, string kind)
        {
            try
            {
                var uiApp = commandData.Application;
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null || uiDoc.Document.IsFamilyDocument)
                {
                    TaskDialog.Show(RevitLinkStrings.DialogTitle, RevitLinkStrings.NoDocument);
                    return Result.Cancelled;
                }
                if (uiDoc.Selection.GetElementIds().Count == 0)
                {
                    TaskDialog.Show(RevitLinkStrings.DialogTitle, RevitLinkStrings.NoSelection);
                    return Result.Cancelled;
                }
                if (Application.SocketService == null || !Application.SocketService.IsRunning)
                {
                    TaskDialog.Show(RevitLinkStrings.DialogTitle, RevitLinkStrings.ServiceNotRunning);
                    return Result.Cancelled;
                }
                if (ViewSyncAuthService.Instance.PairedCount == 0)
                {
                    TaskDialog.Show(RevitLinkStrings.DialogTitle, RevitLinkStrings.NotPaired);
                    return Result.Cancelled;
                }

                new CommandExecutor(uiApp).OpenIssueDraft(kind);
                return Result.Succeeded;
            }
            catch (ViewSyncException ex)
            {
                message = ex.Message;
                TaskDialog.Show(RevitLinkStrings.DialogTitle, ex.Message);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Error("[RevitLink] 功能區按鈕執行失敗（" + kind + "）", ex);
                message = ex.Message;
                TaskDialog.Show(RevitLinkStrings.DialogTitle, "執行失敗：" + ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class CreateIssueFromRevitCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => RevitLinkRibbon.Run(commandData, ref message, "create");
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class ElementIssuesFromRevitCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => RevitLinkRibbon.Run(commandData, ref message, "lookup");
    }
}
