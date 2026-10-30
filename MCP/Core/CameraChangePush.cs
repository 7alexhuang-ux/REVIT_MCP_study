using System;
using Autodesk.Revit.DB;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Core
{
    /// <summary>
    /// 網頁版相機管理（issue-manager D-712）：相機變動後，主動推送 <c>camera_changed</c> 事件給已配對的瀏覽器連線。
    /// 觸發點：①相機後續更新（去抖）完成後（面板與 set_camera_pose 的後續更新共用 UpdateCamerasAfterChange）
    /// ②set_camera_pose 成功後（立即推送，不等去抖）。
    /// 設計原則：沒有已配對的瀏覽器連線就完全不做事（連相機列都不組）；任何失敗只寫 log，絕不影響相機本身的編輯結果。
    /// 組裝邏輯在 <see cref="CameraChangeMessage"/>（純函式，有單元測試）；這裡只負責讀 Revit、送出。
    /// 須在 Revit API context 內呼叫（讀相機清單要用 Document）。
    /// </summary>
    internal static class CameraChangePush
    {
        public static void Notify(Document doc, string source)
        {
            try
            {
                if (doc == null) return;
                var socket = RevitMCP.Application.SocketService;
                if (socket == null || !socket.HasPairedBrowserConnections()) return;

                CameraManagerSnapshot snap = CameraManagerService.ReadSnapshot(doc);
                if (!string.IsNullOrEmpty(snap.ErrorMessage))
                {
                    Logger.Info($"相機變動推送（{source}）略過：{snap.ErrorMessage}");
                    return;
                }

                var cameras = CommandExecutor.BuildCameraList(doc, snap, null);
                string json = CameraChangeMessage.Serialize(CameraChangeMessage.Build(cameras, snap.DocumentTitle));
                int sent = socket.BroadcastToPairedBrowsers(json, CameraChangeMessage.EventName);
                Logger.Info($"相機變動推送（{source}）：{cameras.Count} 台相機 → {sent} 個瀏覽器連線");
            }
            catch (Exception ex)
            {
                Logger.Error($"相機變動推送（{source}）失敗（不影響相機編輯）", ex);
            }
        }
    }
}
