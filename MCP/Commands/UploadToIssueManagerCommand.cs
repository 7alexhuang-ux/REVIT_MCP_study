#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitMCP.Core;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 功能區「上傳到 Issue Manager」：開啟勾選視窗，一次匯出 PDF／模型／視角並上傳成一個版本。
    /// 以 IExternalCommand 啟動、匯出經 ExternalEvent 佇列執行；不經 MCP WebSocket，所以 MCP 服務關閉時也能用。
    /// 這個指令本身不修改模型（匯出用的暫存視圖由既有指令以 TransactionGroup 還原）。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class UploadToIssueManagerCommand : IExternalCommand
    {
        private static IssueManagerUploadWindow _window;
        private static string _windowDocumentKey;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiApp = commandData.Application;
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    TaskDialog.Show(Strings.WindowTitle, "請先開啟一個 Revit 專案文件（族群文件不能上傳）。");
                    return Result.Cancelled;
                }

                var key = doc.Title + "|" + doc.PathName;
                if (_window != null)
                {
                    if (string.Equals(_windowDocumentKey, key, StringComparison.Ordinal))
                    {
                        _window.Activate();
                        return Result.Succeeded;
                    }
                    if (_window.IsBusy)
                    {
                        TaskDialog.Show(Strings.WindowTitle, "上一份文件的上傳還在進行中，請等它完成或取消後再開啟。");
                        return Result.Cancelled;
                    }
                    _window.Close();
                }

                var snapshot = RevitSelectionCollector.Collect(doc);
                var services = IssueManagerSession.Create(uiApp, snapshot);
                var window = new IssueManagerUploadWindow(new ExportSelectionViewModel(snapshot), services);
                new WindowInteropHelper(window) { Owner = uiApp.MainWindowHandle };
                window.Closed += (s, e) => { _window = null; _windowDocumentKey = null; };
                _window = window;
                _windowDocumentKey = key;
                window.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("開啟上傳到 Issue Manager 視窗失敗：" + ex.GetType().Name);
                message = "開啟視窗失敗：" + ex.Message;
                TaskDialog.Show(Strings.WindowTitle, message);
                return Result.Failed;
            }
        }
    }
}
