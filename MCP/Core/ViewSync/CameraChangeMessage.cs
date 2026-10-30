using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.ViewSync
{
    // 網頁版相機管理（issue-manager D-712）的「相機變動推送」訊息組裝。純計算，不依賴 Revit API／WebSocket，可直接單元測試。
    // 規格：issue-manager docs/spec/camera-manager-web.md §5。

    /// <summary>
    /// 推送給已配對瀏覽器連線的 <c>camera_changed</c> 事件。
    /// 格式：<c>{"Event":"camera_changed","Data":{"cameras":[...],"documentTitle":"..."}}</c>。
    /// cameras 的每一列與 list_cameras 回應的 Data.Cameras 列完全同欄位（PascalCase，同一個序列化規則）。
    /// 事件訊息沒有 RequestId／Success 欄位——網頁端以頂層有無 "Event" 區分「事件」與「指令回應」。
    /// </summary>
    public static class CameraChangeMessage
    {
        public const string EventName = "camera_changed";

        /// <summary>組出事件 JSON。cameras 為 null 視為空清單；每一列以預設規則轉成 JSON（與 RevitCommandResponse.Data 相同）。</summary>
        public static JObject Build(IEnumerable<object> cameras, string documentTitle)
        {
            var array = new JArray();
            foreach (var camera in cameras ?? Enumerable.Empty<object>())
            {
                if (camera == null) continue;
                array.Add(JToken.FromObject(camera));
            }

            var data = new JObject
            {
                ["cameras"] = array,
                ["documentTitle"] = documentTitle
            };
            return new JObject
            {
                ["Event"] = EventName,
                ["Data"] = data
            };
        }

        public static string Serialize(JObject message) => message.ToString(Formatting.None);
    }
}
