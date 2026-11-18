using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Core.ViewSync
{
    /// <summary>
    /// Revit 跳轉的外掛端設定（docs/REVIT_LINK.md §4，ALEX 規則：使用者可編輯，不寫死）。
    /// 檔案 %APPDATA%\RevitMCP\revit-link.json；沒有檔案＝全用預設；檔案壞掉＝用預設並回報 LoadError（呼叫端寫日誌）。
    /// 純邏輯，不依賴 Revit API。
    /// </summary>
    public sealed class RevitLinkSettings
    {
        public const string ConfigFileName = "revit-link.json";
        public const int DefaultDraftMinutes = 10;
        public const int DefaultCaptureMaxEdgePx = 2000;

        /// <summary>開預填頁用的 IM 網址；空＝依序取環境變數、issue-manager.json、網頁白名單第一筆。</summary>
        public string ImBaseUrl { get; private set; }
        public int DraftMinutes { get; private set; } = DefaultDraftMinutes;
        public int CaptureMaxEdgePx { get; private set; } = DefaultCaptureMaxEdgePx;
        public bool CaptureEnabled { get; private set; } = true;
        /// <summary>用哪個瀏覽器開預填頁（執行檔完整路徑，網址會當成唯一引數傳入）；空＝系統預設瀏覽器。</summary>
        public string BrowserPath { get; private set; }
        /// <summary>讀檔失敗的原因（沒有檔案不算失敗）；null＝正常。</summary>
        public string LoadError { get; private set; }

        public static string DefaultConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", ConfigFileName);
        }

        public static RevitLinkSettings Load(string path)
        {
            var settings = new RevitLinkSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return settings;
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                var url = json["imBaseUrl"];
                if (url != null && url.Type == JTokenType.String) settings.ImBaseUrl = ((string)url).Trim();
                settings.DraftMinutes = ClampInt(json["draftMinutes"], 1, 60, DefaultDraftMinutes);
                settings.CaptureMaxEdgePx = ClampInt(json["captureMaxEdgePx"], 400, 4000, DefaultCaptureMaxEdgePx);
                var capture = json["captureEnabled"];
                if (capture != null && capture.Type == JTokenType.Boolean) settings.CaptureEnabled = (bool)capture;
                var browser = json["browserPath"];
                if (browser != null && browser.Type == JTokenType.String) settings.BrowserPath = ((string)browser).Trim();
            }
            catch (Exception)
            {
                // 不把檔案內容放進訊息（可能含網址）；只說壞了，使用者自己開檔看。
                return new RevitLinkSettings { LoadError = ConfigFileName + " 不是有效的 JSON，已改用預設值。" };
            }
            return settings;
        }

        private static int ClampInt(JToken token, int min, int max, int fallback)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return fallback;
            var value = (int)Math.Round((double)token);
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>
        /// 解析要開的 IM 網址（無結尾斜線）。優先序：revit-link.json 的 imBaseUrl → ISSUE_MANAGER_URL／issue-manager.json → 網頁白名單第一筆合法網址。
        /// 設定了但不合法（例如 http 的非本機網址）丟 ArgumentException；全都沒有回 null（呼叫端顯示「尚未設定」，不猜網址）。
        /// </summary>
        public string ResolveImBaseUrl(Func<string, string> getEnvironmentVariable, string issueManagerConfigPath, IEnumerable<string> allowedOrigins)
        {
            if (!string.IsNullOrWhiteSpace(ImBaseUrl)) return IssueManagerSettings.NormalizeBaseUrl(ImBaseUrl);
            var fromIssueManager = IssueManagerSettings.ResolveBaseUrl(getEnvironmentVariable, issueManagerConfigPath);
            if (fromIssueManager != null) return fromIssueManager;
            if (allowedOrigins != null)
            {
                foreach (var origin in allowedOrigins)
                {
                    try { return IssueManagerSettings.NormalizeBaseUrl(origin); }
                    catch (ArgumentException) { /* 這一筆不合法，試下一筆 */ }
                }
            }
            return null;
        }
    }

    /// <summary>功能區按鈕與 Claude 指令共用的白話字串（集中放，之後加英文只需換這個類別）。</summary>
    public static class RevitLinkStrings
    {
        public const string DialogTitle = "Revit 跳轉（Issue Manager）";
        public const string ButtonCreateText = "在 IM\n建立議題";
        public const string ButtonCreateTooltip = "把 Revit 目前選取的元件與視圖截圖，帶到 Issue Manager 的新議題預填頁（按送出才會建立；需先開 MCP 服務並完成網頁配對）。";
        public const string ButtonLookupText = "這個元素\n有哪些議題";
        public const string ButtonLookupTooltip = "查 Issue Manager 上與 Revit 目前選取的元件相關的議題（需先開 MCP 服務並完成網頁配對）。";

        public const string NoDocument = "請先開啟一個 Revit 專案文件（族群文件不能使用）。";
        public const string NoSelection = "請先在 Revit 選取至少一個元素。";
        public const string ServiceNotRunning = "MCP 服務還沒啟動。請先按功能區「MCP 服務(開/關)」啟動，再按「網頁配對」完成配對，網頁才領得到草稿。";
        public const string NotPaired = "還沒有任何網頁裝置完成配對。請先按功能區「網頁配對」，把畫面上的 6 位數字輸入 Issue Manager 網頁，再按一次這個按鈕。";
        public const string NoImUrl = "尚未設定 Issue Manager 網址。請在 %APPDATA%\\RevitMCP\\revit-link.json 加入 imBaseUrl（例如 https://你的網址），或設定 ISSUE_MANAGER_URL。";
    }
}
