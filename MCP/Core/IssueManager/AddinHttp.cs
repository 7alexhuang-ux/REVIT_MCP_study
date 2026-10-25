#nullable disable
using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// HTTP 回應 → 例外／JSON 的共用轉換。訊息一律是固定的繁中白話文，
    /// 不回顯伺服器內文、網址或任何權杖（addin-auth.md §6 的日誌規則）。
    /// 錯誤碼對照 Issue Manager docs/spec/revit-addin-export.md §5.2「錯誤碼總表」。
    /// </summary>
    internal static class AddinHttp
    {
        public const string ApiVersionHeader = "X-Addin-Api";
        public const string ApiVersion = "1";
        private const int MaxBodyChars = 4 * 1024 * 1024;
        private static readonly Regex SafeField = new Regex("^[A-Za-z0-9_.\\[\\]]{1,40}$", RegexOptions.Compiled);

        public static async Task<JObject> ReadJsonAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (text.Length > MaxBodyChars) throw new AddinApiException(AddinErrorKind.Server, "伺服器回應過大，已中止。", (int)response.StatusCode);
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            try
            {
                return JObject.Parse(text);
            }
            catch (Exception)
            {
                throw new AddinApiException(AddinErrorKind.Server, "伺服器回應格式不正確。", (int)response.StatusCode);
            }
        }

        public static async Task<AddinApiException> ToExceptionAsync(HttpResponseMessage response)
        {
            string code = null;
            string field = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length <= 8192)
                {
                    var json = JObject.Parse(text);
                    code = json["error"] != null && json["error"].Type == JTokenType.String ? (string)json["error"] : null;
                    field = json["field"] != null && json["field"].Type == JTokenType.String ? (string)json["field"] : null;
                }
            }
            catch (Exception)
            {
                // 沒有 JSON 本文：只靠狀態碼判斷。
            }

            var status = (int)response.StatusCode;
            if (status == 401) return new AddinApiException(AddinErrorKind.NeedLogin, "登入已失效，請重新登入。", status, code);
            if (status == 403 || status == 404) return new AddinApiException(AddinErrorKind.NotFound, "找不到這個項目，或這個帳號沒有權限。", status, code);
            if (status == 426) return new AddinApiException(AddinErrorKind.UpgradeRequired, UpgradeMessage(code), status, code);
            if (status >= 500) return new AddinApiException(AddinErrorKind.Server, ServerMessage(code), status, code);
            if (status == 410) return new AddinApiException(AddinErrorKind.Rejected, RejectedMessage(code ?? "bundle_expired"), status, code);
            if (status >= 400) return new AddinApiException(AddinErrorKind.Rejected, RejectedMessage(code, field), status, code);
            return new AddinApiException(AddinErrorKind.Other, "收到非預期的回應（狀態 " + status + "）。", status, code);
        }

        private static string UpgradeMessage(string code)
        {
            return code == "client_too_old"
                ? "目前的 Revit 外掛版本過舊，Issue Manager 要求先更新外掛才能上傳。"
                : "Issue Manager 要求先更新 Revit 外掛，才能上傳。";
        }

        private static string ServerMessage(string code)
        {
            switch (code)
            {
                case "server_misconfigured":
                    return "Issue Manager 伺服器的設定不完整，暫時無法上傳。請通知管理員。";
                case "finalize_failed":
                    return "Issue Manager 在建立版本時失敗，這次上傳已作廢，沒有建立任何版本。請重試。";
                case "upload_failed":
                    return "Issue Manager 寫入上傳的檔案時失敗，請重試。";
                case "bundle_create_failed":
                    return "Issue Manager 無法建立上傳用的版本包，請稍後再試。";
                case "uploads_paused":
                    return "Issue Manager 目前暫停接受上傳，請稍後再試。";
                default:
                    return "Issue Manager 暫時無法處理，請稍後再試。";
            }
        }

        public static string RejectedMessage(string code, string field = null)
        {
            switch (code)
            {
                case "stage_required":
                case "invalid_stage":
                    return "請選擇設計階段（PD／SD／DD）。";
                case "invalid_input":
                    if (field == "stage") return "請選擇設計階段（PD／SD／DD）。";
                    return field != null && SafeField.IsMatch(field)
                        ? "送出的資料有誤（欄位 " + field + "），Issue Manager 拒絕了這個請求。"
                        : "送出的資料有誤，Issue Manager 拒絕了這個請求。";
                case "description_required":
                    return "請填寫版本說明。";
                case "invalid_grant":
                    return "授權已失效或已被使用，請重新登入。";
                case "payload_too_large":
                    return "檔案超過 Issue Manager 的大小上限。";
                case "checksum_mismatch":
                    return "上傳的檔案與清單的雜湊值不符，請重新上傳。";
                case "invalid_file":
                    return "檔案格式檢查未通過（檔案缺漏，或 PDF／GLB／JSON 內容不正確）。";
                case "invalid_manifest":
                    return "版本包清單未通過伺服器檢查（內容不符規格，或沒有圖紙也沒有模型）。";
                case "page_count_mismatch":
                    return "PDF 的頁數與勾選的圖紙數不同，伺服器已拒絕。請確認 PDF 匯出設定（合併成單一檔案、每張圖紙一頁）後重新匯出。";
                case "invalid_pdf":
                    return "伺服器無法讀取匯出的 PDF（檔案損毀，或頁數超過 1,000 頁）。請重新匯出。";
                case "invalid_part":
                    return "上傳了空的檔案分段，請重新上傳。";
                case "bundle_failed":
                    return "伺服器已作廢這個版本包（先前的檢查失敗），不能再使用；需要重新建立版本包後再上傳。";
                case "bundle_expired":
                    return "這個版本包已經過期，伺服器已清除暫存；需要重新建立版本包後再上傳。";
                case "bundle_closed":
                    return "這個版本包已經不接受上傳（已取消或已結束）；需要重新建立版本包。";
                case "finalize_in_progress":
                    return "伺服器正在處理這個版本，請稍候。";
                case "already_finalized":
                    return "這個版本已經上傳完成，無法取消或修改；如要移除，請到 Issue Manager 網站刪除該版本。";
                case "too_many_open_bundles":
                    return "未完成的上傳太多了，請稍後再試，或等先前未完成的上傳逾時清除。";
                case "invalid_request":
                    return "Issue Manager 無法解讀送出的請求內容。";
                default:
                    return string.IsNullOrEmpty(code) ? "Issue Manager 拒絕了這個請求。" : "Issue Manager 拒絕了這個請求（" + code + "）。";
            }
        }

        public static AddinApiException FromTransport(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return new AddinApiException(AddinErrorKind.Network, "連線逾時，請檢查網路後再試。", 0, null, true);
            return new AddinApiException(AddinErrorKind.Network, "連不到 Issue Manager，請檢查網路後再試。");
        }

        public static StringContent Json(JObject body)
        {
            return new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        }
    }
}
