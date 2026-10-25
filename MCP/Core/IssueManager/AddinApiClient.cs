#nullable disable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>建立版本包時送出的摘要（revit-addin-export.md §5：summary）。</summary>
    public sealed class BundleSummary
    {
        public int Sheets { get; set; }
        public int Views { get; set; }
        public int Models { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>
    /// 版本包 API 客戶端（/api/addin/v1/…，Bearer 權杖）。合約見 docs/spec/revit-addin-export.md §5、§5.2（Issue Manager repo）。
    /// 遇到 401 會用 refresh 換新權杖後重試一次；仍 401 → NeedLogin。
    /// Bearer 只送給 Issue Manager 自己的主機：簽名上傳網址若指向別的主機（例如雲端儲存），不附 Authorization；
    /// 若上傳網址是本機（localhost）而 Issue Manager 不是本機，視為伺服器設定錯誤並中止（不靜默送到本機）。
    /// </summary>
    public sealed class AddinApiClient
    {
        /// <summary>伺服器 finalize 最長約 300 秒（磁碟模式更久）：finalize 專用請求的逾時要大於它。</summary>
        public static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(330);

        private readonly HttpClient _http;
        private readonly AddinAuthService _auth;
        private readonly ClientInfo _client;
        private readonly HttpClient _uploadHttp;
        private readonly HttpClient _finalizeHttp;

        /// <param name="uploadHttp">上傳分段專用（逾時比一般 JSON 呼叫長）；不給就共用 http。</param>
        /// <param name="finalizeHttp">finalize 專用（逾時 ≥ <see cref="FinalizeTimeout"/>）；不給就共用 http。</param>
        public AddinApiClient(HttpClient http, AddinAuthService auth, ClientInfo client, HttpClient uploadHttp = null, HttpClient finalizeHttp = null)
        {
            _http = http ?? throw new ArgumentNullException("http");
            _auth = auth ?? throw new ArgumentNullException("auth");
            _client = client ?? ClientInfo.Create("Revit", "0");
            _uploadHttp = uploadHttp ?? http;
            _finalizeHttp = finalizeHttp ?? http;
        }

        public async Task<AddinUser> GetMeAsync(CancellationToken ct)
        {
            var json = await SendJsonAsync(_http, HttpMethod.Get, "/api/addin/me", null, false, ct).ConfigureAwait(false);
            return AddinUser.FromJson(json["user"]);
        }

        public async Task<List<AddinProject>> GetProjectsAsync(CancellationToken ct)
        {
            var json = await SendJsonAsync(_http, HttpMethod.Get, "/api/addin/projects", null, false, ct).ConfigureAwait(false);
            var list = new List<AddinProject>();
            var array = json["projects"] as JArray;
            if (array != null)
            {
                foreach (var item in array)
                {
                    list.Add(new AddinProject { Id = (string)item["id"], Code = (string)item["code"], Name = (string)item["name"] });
                }
            }
            return list;
        }

        /// <summary>建立版本包。伺服器成功時回 201（舊的假伺服器回 200）；兩者都接受。</summary>
        public async Task<BundleCreateResult> CreateBundleAsync(string projectId, string stage, string description,
            BundleSummary summary, CancellationToken ct)
        {
            var body = new JObject
            {
                ["projectId"] = projectId,
                ["stage"] = stage,
                ["description"] = description,
                ["summary"] = new JObject
                {
                    ["sheets"] = summary.Sheets, ["views"] = summary.Views, ["models"] = summary.Models, ["bytes"] = summary.Bytes
                },
                ["clientVersion"] = _client.Version
            };
            var json = await SendJsonAsync(_http, HttpMethod.Post, "/api/addin/v1/bundles", body, true, ct).ConfigureAwait(false);
            var result = new BundleCreateResult { BundleId = (string)json["bundleId"] };
            if (string.IsNullOrEmpty(result.BundleId)) throw new AddinApiException(AddinErrorKind.Server, "伺服器沒有回傳版本包編號。");
            DateTimeOffset expires;
            if (DateTimeOffset.TryParse((string)json["expiresAt"], out expires)) result.ExpiresAt = expires;
            var limits = json["limits"] as JObject;
            if (limits != null)
            {
                result.Limits = new BundleLimits
                {
                    PdfBytes = ReadLong(limits["pdfBytes"]), GlbBytes = ReadLong(limits["glbBytes"]),
                    NamedViewsBytes = ReadLong(limits["namedViewsBytes"]), PdfPages = (int)Math.Min(int.MaxValue, ReadLong(limits["pdfPages"]))
                };
            }
            var files = json["files"] as JArray;
            if (files != null)
            {
                foreach (var file in files)
                {
                    var ticket = new BundleFileTicket
                    {
                        Kind = (string)file["kind"], Name = (string)file["name"],
                        PartBytes = file["partBytes"] != null ? (long)file["partBytes"] : 0
                    };
                    var urls = file["urls"] as JArray;
                    if (urls != null) foreach (var url in urls) ticket.Urls.Add((string)url);
                    result.Files.Add(ticket);
                }
            }
            return result;
        }

        private static long ReadLong(JToken token)
        {
            return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (long)token : 0;
        }

        /// <summary>可選標頭：分段的 SHA-256（64 位小寫十六進位），revit-addin-export.md §5.2。</summary>
        public const string PartHashHeader = "X-Part-Sha256";

        private static string Sha256Hex(byte[] data, int offset, int count)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(data, offset, count);
                var builder = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        /// <summary>PUT 一個分段到簽名網址。回傳前已確認 2xx。</summary>
        public async Task PutPartAsync(string url, byte[] data, int offset, int count, CancellationToken ct)
        {
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target) || (target.Scheme != Uri.UriSchemeHttps && !target.IsLoopback))
                throw new AddinApiException(AddinErrorKind.Server, "伺服器給的上傳網址不安全或格式不正確。");
            var baseUri = new Uri(_auth.BaseUrl);
            // 磁碟模式下伺服器用 ADDIN_PUBLIC_URL 組上傳網址；沒設定時可能組出 http://localhost:<port>。
            // Issue Manager 本身不在本機卻要我們把檔案送到本機，不是正常情況（也可能是被竄改的回應），一律中止。
            if (target.IsLoopback && !baseUri.IsLoopback)
                throw new AddinApiException(AddinErrorKind.Other,
                    "伺服器回傳的上傳網址不是 Issue Manager 的主機，已中止；請通知管理員設定 ADDIN_PUBLIC_URL。");
            var sameHost = target.Port == baseUri.Port
                && (string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || (target.IsLoopback && baseUri.IsLoopback));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                string token = null;
                if (sameHost) token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Put, target))
                    {
                        if (token != null)
                        {
                            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                            request.Headers.TryAddWithoutValidation(AddinHttp.ApiVersionHeader, AddinHttp.ApiVersion);
                            // 磁碟模式（Issue Manager 自己接收分段）：帶分段雜湊，伺服器不符就在這一段回 400 checksum_mismatch，
                            // 不必等整包 finalize 才發現。簽名網址（別的主機）不加額外標頭。
                            request.Headers.TryAddWithoutValidation(PartHashHeader, Sha256Hex(data, offset, count));
                        }
                        var content = new ByteArrayContent(data, offset, count);
                        content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
                        request.Content = content;
                        using (var response = await _uploadHttp.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode) return;
                            if ((int)response.StatusCode == 401 && sameHost && attempt == 0)
                            {
                                await _auth.RefreshAfterUnauthorizedAsync(token, ct).ConfigureAwait(false);
                                continue;
                            }
                            throw await AddinHttp.ToExceptionAsync(response).ConfigureAwait(false);
                        }
                    }
                }
                catch (AddinApiException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { throw AddinHttp.FromTransport(ex); }
            }
            throw new AddinApiException(AddinErrorKind.NeedLogin, "登入已失效，請重新登入。");
        }

        /// <summary>
        /// finalize（冪等：同一個 bundle 重送回同一個 setId）。伺服器最長約 300 秒，所以用專用的長逾時 HttpClient。
        /// 驗證失敗（400／413）會讓伺服器作廢這個 bundle，之後再送一律 409 bundle_failed——由 <see cref="UploadFlow"/> 處理（重建 bundle）。
        /// </summary>
        public async Task<FinalizeResult> FinalizeAsync(string bundleId, JObject manifest, CancellationToken ct)
        {
            var json = await SendJsonAsync(_finalizeHttp, HttpMethod.Post, "/api/addin/v1/bundles/" + Uri.EscapeDataString(bundleId) + "/finalize",
                new JObject { ["manifest"] = manifest }, true, ct).ConfigureAwait(false);
            var result = new FinalizeResult { SetId = (string)json["setId"], Url = (string)json["url"] };
            var warnings = json["warnings"] as JArray;
            if (warnings != null) foreach (var w in warnings) result.Warnings.Add((string)w);
            return result;
        }

        /// <summary>取消並清除伺服器端暫存。失敗只回 false（取消流程不應該再丟例外）。</summary>
        public async Task<bool> DeleteBundleAsync(string bundleId, CancellationToken ct)
        {
            return await DeleteBundleDetailedAsync(bundleId, ct).ConfigureAwait(false) == BundleDeleteOutcome.Deleted;
        }

        /// <summary>
        /// 取消並回報結果：已 finalize 的 bundle 伺服器回 409 already_finalized 且不刪版本（版本只能到網站刪除）；
        /// finalize 進行中回 409 finalize_in_progress。呼叫端要把這兩種情況白話告訴使用者。
        /// </summary>
        public async Task<BundleDeleteOutcome> DeleteBundleDetailedAsync(string bundleId, CancellationToken ct)
        {
            try
            {
                await SendJsonAsync(_http, HttpMethod.Delete, "/api/addin/v1/bundles/" + Uri.EscapeDataString(bundleId), null, true, ct).ConfigureAwait(false);
                return BundleDeleteOutcome.Deleted;
            }
            catch (AddinApiException ex)
            {
                if (ex.Code == "already_finalized") return BundleDeleteOutcome.AlreadyFinalized;
                if (ex.Code == "finalize_in_progress") return BundleDeleteOutcome.FinalizeInProgress;
                if (ex.StatusCode == 404 || ex.StatusCode == 410) return BundleDeleteOutcome.Deleted;
                return BundleDeleteOutcome.Failed;
            }
            catch (Exception)
            {
                return BundleDeleteOutcome.Failed;
            }
        }

        private async Task<JObject> SendJsonAsync(HttpClient http, HttpMethod method, string path, JObject body, bool versioned, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                try
                {
                    using (var request = new HttpRequestMessage(method, _auth.BaseUrl + path))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        if (versioned) request.Headers.TryAddWithoutValidation(AddinHttp.ApiVersionHeader, AddinHttp.ApiVersion);
                        if (body != null) request.Content = AddinHttp.Json(body);
                        using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                if (response.Content == null || response.Content.Headers.ContentLength == 0) return new JObject();
                                return await AddinHttp.ReadJsonAsync(response).ConfigureAwait(false);
                            }
                            if ((int)response.StatusCode == 401 && attempt == 0)
                            {
                                await _auth.RefreshAfterUnauthorizedAsync(token, ct).ConfigureAwait(false);
                                continue;
                            }
                            throw await AddinHttp.ToExceptionAsync(response).ConfigureAwait(false);
                        }
                    }
                }
                catch (AddinApiException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { throw AddinHttp.FromTransport(ex); }
            }
            throw new AddinApiException(AddinErrorKind.NeedLogin, "登入已失效，請重新登入。");
        }
    }
}
