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
    /// 版本包 API 客戶端（/api/addin/v1/…，Bearer 權杖）。合約見 docs/spec/revit-addin-export.md §5（Issue Manager repo）。
    /// 遇到 401 會用 refresh 換新權杖後重試一次；仍 401 → NeedLogin。
    /// Bearer 只送給 Issue Manager 自己的主機：簽名上傳網址若指向別的主機（例如雲端儲存），不附 Authorization。
    /// </summary>
    public sealed class AddinApiClient
    {
        private readonly HttpClient _http;
        private readonly AddinAuthService _auth;
        private readonly ClientInfo _client;
        private readonly HttpClient _uploadHttp;

        /// <param name="uploadHttp">上傳分段專用（逾時比一般 JSON 呼叫長）；不給就共用 http。</param>
        public AddinApiClient(HttpClient http, AddinAuthService auth, ClientInfo client, HttpClient uploadHttp = null)
        {
            _http = http ?? throw new ArgumentNullException("http");
            _auth = auth ?? throw new ArgumentNullException("auth");
            _client = client ?? ClientInfo.Create("Revit", "0");
            _uploadHttp = uploadHttp ?? http;
        }

        public async Task<AddinUser> GetMeAsync(CancellationToken ct)
        {
            var json = await SendJsonAsync(HttpMethod.Get, "/api/addin/me", null, false, ct).ConfigureAwait(false);
            return AddinUser.FromJson(json["user"]);
        }

        public async Task<List<AddinProject>> GetProjectsAsync(CancellationToken ct)
        {
            var json = await SendJsonAsync(HttpMethod.Get, "/api/addin/projects", null, false, ct).ConfigureAwait(false);
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
            var json = await SendJsonAsync(HttpMethod.Post, "/api/addin/v1/bundles", body, true, ct).ConfigureAwait(false);
            var result = new BundleCreateResult { BundleId = (string)json["bundleId"] };
            if (string.IsNullOrEmpty(result.BundleId)) throw new AddinApiException(AddinErrorKind.Server, "伺服器沒有回傳版本包編號。");
            DateTimeOffset expires;
            if (DateTimeOffset.TryParse((string)json["expiresAt"], out expires)) result.ExpiresAt = expires;
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

        /// <summary>PUT 一個分段到簽名網址。回傳前已確認 2xx。</summary>
        public async Task PutPartAsync(string url, byte[] data, int offset, int count, CancellationToken ct)
        {
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target) || (target.Scheme != Uri.UriSchemeHttps && !target.IsLoopback))
                throw new AddinApiException(AddinErrorKind.Server, "伺服器給的上傳網址不安全或格式不正確。");
            var sameHost = string.Equals(target.Host, new Uri(_auth.BaseUrl).Host, StringComparison.OrdinalIgnoreCase)
                && target.Port == new Uri(_auth.BaseUrl).Port;

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

        public async Task<FinalizeResult> FinalizeAsync(string bundleId, JObject manifest, CancellationToken ct)
        {
            var json = await SendJsonAsync(HttpMethod.Post, "/api/addin/v1/bundles/" + Uri.EscapeDataString(bundleId) + "/finalize",
                new JObject { ["manifest"] = manifest }, true, ct).ConfigureAwait(false);
            var result = new FinalizeResult { SetId = (string)json["setId"], Url = (string)json["url"] };
            var warnings = json["warnings"] as JArray;
            if (warnings != null) foreach (var w in warnings) result.Warnings.Add((string)w);
            return result;
        }

        /// <summary>取消並清除伺服器端暫存。失敗只回 false（取消流程不應該再丟例外）。</summary>
        public async Task<bool> DeleteBundleAsync(string bundleId, CancellationToken ct)
        {
            try
            {
                await SendJsonAsync(HttpMethod.Delete, "/api/addin/v1/bundles/" + Uri.EscapeDataString(bundleId), null, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task<JObject> SendJsonAsync(HttpMethod method, string path, JObject body, bool versioned, CancellationToken ct)
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
                        using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
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
