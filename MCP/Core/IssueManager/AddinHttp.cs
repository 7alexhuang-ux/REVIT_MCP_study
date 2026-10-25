#nullable disable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// HTTP 回應 → 例外／JSON 的共用轉換。訊息一律是固定的繁中白話文，
    /// 不回顯伺服器內文、網址或任何權杖（addin-auth.md §6 的日誌規則）。
    /// </summary>
    internal static class AddinHttp
    {
        public const string ApiVersionHeader = "X-Addin-Api";
        public const string ApiVersion = "1";
        private const int MaxBodyChars = 4 * 1024 * 1024;

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
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length <= 8192)
                {
                    var json = JObject.Parse(text);
                    code = json["error"] != null && json["error"].Type == JTokenType.String ? (string)json["error"] : null;
                }
            }
            catch (Exception)
            {
                // 沒有 JSON 本文：只靠狀態碼判斷。
            }

            var status = (int)response.StatusCode;
            if (status == 401) return new AddinApiException(AddinErrorKind.NeedLogin, "登入已失效，請重新登入。", status, code);
            if (status == 403 || status == 404) return new AddinApiException(AddinErrorKind.NotFound, "找不到這個項目，或這個帳號沒有權限。", status, code);
            if (status == 426) return new AddinApiException(AddinErrorKind.UpgradeRequired, "Issue Manager 要求先更新 Revit 外掛，才能上傳。", status, code);
            if (status >= 500) return new AddinApiException(AddinErrorKind.Server, "Issue Manager 暫時無法處理，請稍後再試。", status, code);
            if (status >= 400) return new AddinApiException(AddinErrorKind.Rejected, RejectedMessage(code), status, code);
            return new AddinApiException(AddinErrorKind.Other, "收到非預期的回應（狀態 " + status + "）。", status, code);
        }

        public static string RejectedMessage(string code)
        {
            switch (code)
            {
                case "stage_required":
                case "invalid_stage":
                    return "請選擇設計階段（PD／SD／DD）。";
                case "description_required":
                    return "請填寫版本說明。";
                case "invalid_grant":
                    return "授權已失效或已被使用，請重新登入。";
                case "payload_too_large":
                    return "檔案超過 Issue Manager 的大小上限。";
                case "checksum_mismatch":
                    return "上傳的檔案與清單的雜湊值不符，請重新上傳。";
                case "invalid_file":
                    return "檔案格式檢查未通過（PDF／GLB 標頭不正確）。";
                case "invalid_manifest":
                    return "版本包清單未通過伺服器檢查。";
                default:
                    return string.IsNullOrEmpty(code) ? "Issue Manager 拒絕了這個請求。" : "Issue Manager 拒絕了這個請求（" + code + "）。";
            }
        }

        public static AddinApiException FromTransport(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return new AddinApiException(AddinErrorKind.Network, "連線逾時，請檢查網路後再試。");
            return new AddinApiException(AddinErrorKind.Network, "連不到 Issue Manager，請檢查網路後再試。");
        }

        public static StringContent Json(JObject body)
        {
            return new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        }
    }
}
