// 反向入口（Q1＝C，ADR D-015）：功能區按鈕「在 IM 建立議題」「這個元素有哪些議題」與 Claude MCP 指令（open_issue_draft、open_element_issues）
// 共用同一段 OpenIssueDraft。唯讀：只讀選取與視圖截圖、開瀏覽器；不開 Transaction、不改模型。
using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.IssueManager;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 取目前選取＋（建立議題時）視圖截圖 → 暫存草稿（記憶體、取一次就刪、預設 10 分鐘）→ 以預設瀏覽器開 {IM}/revit/draft?code=… 或 /revit/lookup?code=…。
        /// kind：create＝建立議題；lookup＝這個元素有哪些議題。截圖失敗或設定關閉時仍可繼續（草稿沒有截圖，預填頁顯示「沒有截圖」）。
        /// 回傳 { opened:true, kind, url, elementCount, pairedWebDevices }：url 刻意不含一次性代碼（代碼只交給瀏覽器，不進 Claude 對話與日誌）。
        /// 錯誤碼：selection_empty、no_document、not_project_document、im_url_missing、im_url_invalid、bad_request。
        /// </summary>
        internal object OpenIssueDraft(string kind)
        {
            if (kind != "create" && kind != "lookup") throw new ViewSyncException("bad_request", "kind 只能是 create 或 lookup。");

            var settings = RevitLinkSettings.Load(RevitLinkSettings.DefaultConfigPath());
            if (settings.LoadError != null) Logger.Error("[RevitLink] " + settings.LoadError);

            string baseUrl;
            try
            {
                baseUrl = settings.ResolveImBaseUrl(Environment.GetEnvironmentVariable, IssueManagerSettings.DefaultConfigPath(), ViewSyncAuthService.Instance.AllowedOrigins);
            }
            catch (ArgumentException ex)
            {
                throw new ViewSyncException("im_url_invalid", ex.Message);
            }
            if (baseUrl == null) throw new ViewSyncException("im_url_missing", RevitLinkStrings.NoImUrl);

            // 帶 __browser 讓選取結果不含本機檔案路徑（草稿之後會交給瀏覽器）。
            var selection = (JObject)ViewSyncGetSelection(new JObject { ["__browser"] = true });

            JToken image = JValue.CreateNull();
            if (kind == "create" && settings.CaptureEnabled)
            {
                try { image = (JObject)ViewSyncCaptureView(new JObject { ["maxLongEdgePx"] = settings.CaptureMaxEdgePx }); }
                catch (ViewSyncException) { image = JValue.CreateNull(); } // 截圖失敗不擋建立議題
            }

            var draft = new JObject
            {
                ["intent"] = kind,
                ["model"] = selection["model"],
                ["view"] = selection["view"],
                ["elements"] = selection["elements"],
                ["image"] = image,
            };
            var code = RevitLinkDraftStore.Shared.Put(draft, TimeSpan.FromMinutes(settings.DraftMinutes));
            var page = kind == "create" ? "/revit/draft" : "/revit/lookup";
            var elementCount = ((JArray)selection["elements"]).Count;

            // 日誌只記數量；不記識別碼、代碼、截圖。
            Logger.Info("[RevitLink] 建立了一份草稿（" + kind + "），元素數 " + elementCount);
            Process.Start(new ProcessStartInfo(baseUrl + page + "?code=" + Uri.EscapeDataString(code)) { UseShellExecute = true });

            return new JObject
            {
                ["opened"] = true,
                ["kind"] = kind,
                ["url"] = baseUrl + page,
                ["elementCount"] = elementCount,
                ["pairedWebDevices"] = ViewSyncAuthService.Instance.PairedCount,
            };
        }
    }
}
