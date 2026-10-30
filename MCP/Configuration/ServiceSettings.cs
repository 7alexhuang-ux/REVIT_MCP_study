using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Configuration
{
    /// <summary>
    /// MCP 服務設定
    /// </summary>
    [Serializable]
    public class ServiceSettings
    {
        /// <summary>
        /// 設定檔格式版本（用於偵測舊版設定檔並自動修正）
        /// </summary>
        public int ConfigVersion { get; set; } = 2;

        /// <summary>
        /// 目前設定檔格式版本號
        /// </summary>
        public static int CurrentConfigVersion => 2;

        /// <summary>
        /// 預設 WebSocket 埠號
        /// </summary>
        public const int DefaultPort = 8964;

        /// <summary>
        /// WebSocket 伺服器主機位址
        /// </summary>
        public string Host { get; set; } = "localhost";

        /// <summary>
        /// WebSocket 伺服器埠號
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// 是否啟用 MCP 服務
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 自動重連間隔（毫秒）
        /// </summary>
        public int ReconnectInterval { get; set; } = 5000;

        /// <summary>
        /// 命令執行逾時時間（毫秒）
        /// </summary>
        public int CommandTimeout { get; set; } = 30000;

        /// <summary>
        /// 允許連線的瀏覽器網域（Origin 白名單，網頁 3D 視角聯動 D-710）。可在 config.json 增刪；
        /// 沒有 Origin 的本機連線（MCP Server、tools/revit-command.mjs）不受此清單影響。
        /// Replace：反序列化時整份取代預設值，避免預設網域每次載入重複累加。
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AllowedOrigins { get; set; } = new List<string>(OriginPolicy.DefaultAllowedOrigins);

        /// <summary>已配對的網頁裝置（只存 token 的 SHA-256 與配對時的 Origin；Revit 功能區「撤銷網頁配對」會清空）。</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<PairedWebClient> PairedWebClients { get; set; } = new List<PairedWebClient>();

        /// <summary>
        /// 驗證並修正設定值。回傳 true 表示有修正（需存檔）。
        /// </summary>
        public bool ValidateAndFix()
        {
            bool changed = false;

            // 偵測舊版設定檔（沒有 ConfigVersion 欄位時反序列化為 0）
            if (ConfigVersion < CurrentConfigVersion)
            {
                // 舊版設定檔：強制將 Port 修正為預設值
                Port = DefaultPort;
                ConfigVersion = CurrentConfigVersion;
                changed = true;
            }

            // Port 合法範圍檢查
            if (Port < 1024 || Port > 65535)
            {
                Port = DefaultPort;
                changed = true;
            }

            return changed;
        }
    }
}
