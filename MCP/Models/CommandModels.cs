using System;
using Newtonsoft.Json;

namespace RevitMCP.Models
{
    /// <summary>
    /// Revit 命令請求模型
    /// </summary>
    [Serializable]
    public class RevitCommandRequest
    {
        /// <summary>
        /// 命令名稱
        /// </summary>
        public string CommandName { get; set; }

        /// <summary>
        /// 命令參數（JSON 字串）
        /// </summary>
        public object Parameters { get; set; }

        /// <summary>
        /// 請求 ID（用於追蹤回應）
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// 內部使用：此請求來自哪一條 WebSocket 連線。避免重連後舊請求回應送到新 client。
        /// </summary>
        [JsonIgnore]
        public Guid ConnectionId { get; set; }

        /// <summary>
        /// 內部使用：請求來自瀏覽器連線（帶 Origin 標頭）。只由 SocketService 在授權通過後設定，不從 JSON 讀取；
        /// CommandExecutor 對瀏覽器請求再檢查一次指令白名單（縱深防禦）。
        /// </summary>
        [JsonIgnore]
        public bool IsBrowser { get; set; }
    }

    /// <summary>
    /// Revit 命令回應模型
    /// </summary>
    [Serializable]
    public class RevitCommandResponse
    {
        /// <summary>
        /// 執行是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 回應資料
        /// </summary>
        public object Data { get; set; }

        /// <summary>
        /// 錯誤訊息
        /// </summary>
        public string Error { get; set; }

        /// <summary>機器可讀錯誤碼（目前只有 viewsync_* 指令會帶，例如 model_mismatch）；沒有時不輸出。</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorCode { get; set; }

        /// <summary>
        /// 請求 ID
        /// </summary>
        public string RequestId { get; set; }
    }
}
