using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// FakeTokenServer 的版本包 API 部分（revit-addin-export.md §5、§5.2，依網站端 imv4a 的實際行為）：
    /// 建立回 201、階段錯誤碼 invalid_input、files 只有 pdf／glb／namedViews、finalize 驗證失敗即作廢（再送 409 bundle_failed）、
    /// finalize 冪等、DELETE 已 finalize 回 409 already_finalized、過期 410 bundle_expired。
    /// </summary>
    public sealed partial class FakeTokenServer
    {
        public long PartBytes = 10;
        /// <summary>簽名上傳網址的主機；預設與 API 同主機（本機開發模式），測試「Bearer 不送給別的主機」時改成別的。</summary>
        public string UploadHost = "im.example.test";
        /// <summary>簽名上傳網址的協定；測試「伺服器回傳 http://localhost 上傳網址」時設成 http。</summary>
        public string UploadScheme = "https";
        public int FailNextPuts;
        public HttpStatusCode FailPutStatus = HttpStatusCode.ServiceUnavailable;
        public DateTimeOffset BundleExpiresAt = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        public int UrlsPerFile = 50;

        public readonly List<JObject> CreateRequests = new List<JObject>();
        public readonly List<RecordedRequest> Puts = new List<RecordedRequest>();
        public readonly List<string> Deleted = new List<string>();
        public readonly List<JObject> FinalizeManifests = new List<JObject>();
        private readonly Dictionary<string, string> _finalizedSetIds = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string, SortedDictionary<int, byte[]>>> _stored =
            new Dictionary<string, Dictionary<string, SortedDictionary<int, byte[]>>>();
        public string RejectFinalizeWithCode;
        /// <summary>建立回應的 limits（網站端：PDF 300 MB、GLB 500 MB、命名視圖 4 MB、1,000 頁）。</summary>
        public JObject Limits = new JObject { ["pdfBytes"] = 314572800L, ["glbBytes"] = 524288000L, ["namedViewsBytes"] = 4194304L, ["viewPositionsBytes"] = 4194304L, ["paramsBytes"] = 8388608L, ["pdfPages"] = 1000 };
        /// <summary>建立回應核發的檔案種類。預設是 imrevitaddin 起的網站（五種）；測試舊網站時改成 pdf／glb／namedViews。</summary>
        public string[] Kinds = { "pdf", "glb", "namedViews", "viewPositions", "params" };
        private string VersionOf(string bundleId) { return "v" + (CreatedBundleIds.IndexOf(bundleId) + 1); }
        public readonly List<string> CreatedBundleIds = new List<string>();
        public readonly List<string> FinalizeRequestBundleIds = new List<string>();
        /// <summary>bundle 狀態：open／finalizing／finalized／failed／deleted／expired。</summary>
        public readonly Dictionary<string, string> BundleStatus = new Dictionary<string, string>();
        /// <summary>依序消耗的 finalize 回應腳本（先於一般邏輯）：可回傳 409 finalize_in_progress、拋出 TaskCanceledException 模擬逾時…</summary>
        public readonly Queue<Func<RecordedRequest, HttpResponseMessage>> FinalizeScript = new Queue<Func<RecordedRequest, HttpResponseMessage>>();
        /// <summary>依序消耗的分段 PUT 回應腳本（先於一般邏輯）：可回傳 410 bundle_expired、409 bundle_closed、簽名網址主機的 403…</summary>
        public readonly Queue<Func<RecordedRequest, HttpResponseMessage>> PutScript = new Queue<Func<RecordedRequest, HttpResponseMessage>>();
        /// <summary>讓指定 bundle 在伺服器端視為已過期（410 bundle_expired）。</summary>
        public void Expire(string bundleId) { lock (_gate) BundleStatus[bundleId] = "expired"; }
        /// <summary>模擬「finalize 其實在伺服器端做完了」：把 bundle 標成已 finalize（取消時的 DELETE 就會得到 409）。</summary>
        public void MarkFinalized(string bundleId)
        {
            lock (_gate)
            {
                BundleStatus[bundleId] = "finalized";
                _finalizedSetIds[bundleId] = "set-" + bundleId;
            }
        }

        public byte[] StoredFile(string bundleId, string kind)
        {
            lock (_gate)
            {
                return _stored[bundleId][kind].Values.SelectMany(b => b).ToArray();
            }
        }

        /// <summary>驗證失敗即作廢：暫存清除、之後 finalize 一律 409 bundle_failed（B03）。呼叫端已持有 _gate。</summary>
        private HttpResponseMessage Fail(string bundleId, HttpStatusCode status, string code)
        {
            BundleStatus[bundleId] = "failed";
            _stored[bundleId] = new Dictionary<string, SortedDictionary<int, byte[]>>();
            return FakeHandler.Error(status, code);
        }

        private HttpResponseMessage RespondBundles(RecordedRequest request)
        {
            var path = request.Uri.AbsolutePath;
            if (path.StartsWith("/upload/", StringComparison.Ordinal))
            {
                var parts = path.Split('/'); // "", upload, bundle, kind, index
                lock (_gate)
                {
                    Puts.Add(request);
                    if (PutScript.Count > 0) return PutScript.Dequeue()(request);
                    if (FailNextPuts > 0)
                    {
                        FailNextPuts--;
                        return FakeHandler.Error(FailPutStatus, "temporarily_unavailable");
                    }
                    if (!_stored.ContainsKey(parts[2])) return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
                    string putStatus;
                    if (BundleStatus.TryGetValue(parts[2], out putStatus) && putStatus != "open")
                        return putStatus == "expired" ? FakeHandler.Error((HttpStatusCode)410, "bundle_expired") : FakeHandler.Error(HttpStatusCode.Conflict, putStatus == "finalizing" ? "finalize_in_progress" : "bundle_closed");
                    var files = _stored[parts[2]];
                    if (!files.ContainsKey(parts[3])) files[parts[3]] = new SortedDictionary<int, byte[]>();
                    files[parts[3]][int.Parse(parts[4])] = request.BodyBytes;
                }
                return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["ok"] = true });
            }

            if (!path.StartsWith("/api/addin/v1/bundles", StringComparison.Ordinal)) return null;
            if (request.ApiVersion != "1") return FakeHandler.Error((HttpStatusCode)426, "upgrade_required");
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries); // api addin v1 bundles [id] [finalize]

            if (request.Method == HttpMethod.Post && segments.Length == 4)
            {
                var body = request.Json;
                lock (_gate) CreateRequests.Add(body);
                var stage = (string)body["stage"];
                if (stage != "PD" && stage != "SD" && stage != "DD")
                    return FakeHandler.Json(HttpStatusCode.BadRequest, new JObject { ["error"] = "invalid_input", ["field"] = "stage" });
                if (string.IsNullOrWhiteSpace((string)body["description"])) return FakeHandler.Error(HttpStatusCode.BadRequest, "description_required");
                var id = "bundle-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                lock (_gate) { _stored[id] = new Dictionary<string, SortedDictionary<int, byte[]>>(); BundleStatus[id] = "open"; CreatedBundleIds.Add(id); }
                var files = new JArray();
                foreach (var kind in Kinds)
                {
                    files.Add(new JObject
                    {
                        ["kind"] = kind, ["name"] = kind, ["partBytes"] = PartBytes,
                        ["urls"] = new JArray(Enumerable.Range(0, UrlsPerFile).Select(i => (object)(UploadScheme + "://" + UploadHost + "/upload/" + id + "/" + kind + "/" + i)).ToArray())
                    });
                }
                return FakeHandler.Json(HttpStatusCode.Created, new JObject { ["bundleId"] = id, ["expiresAt"] = BundleExpiresAt.ToString("o"), ["limits"] = Limits.DeepClone(), ["files"] = files });
            }

            if (segments.Length >= 5)
            {
                var id = segments[4];
                if (request.Method == HttpMethod.Delete)
                {
                    lock (_gate)
                    {
                        string current;
                        if (!BundleStatus.TryGetValue(id, out current)) return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
                        if (current == "finalized") return FakeHandler.Error(HttpStatusCode.Conflict, "already_finalized");
                        if (current == "finalizing") return FakeHandler.Error(HttpStatusCode.Conflict, "finalize_in_progress");
                        Deleted.Add(id);
                        _stored.Remove(id);
                        BundleStatus[id] = "deleted";
                    }
                    return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["deleted"] = true });
                }
                if (request.Method == HttpMethod.Post && segments.Length == 6 && segments[5] == "finalize")
                {
                    lock (_gate)
                    {
                        FinalizeRequestBundleIds.Add(id);
                        if (FinalizeScript.Count > 0) return FinalizeScript.Dequeue()(request);
                        string current;
                        if (!BundleStatus.TryGetValue(id, out current)) return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
                        string existing;
                        if (current == "finalized" && _finalizedSetIds.TryGetValue(id, out existing))
                            return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["setId"] = existing, ["url"] = "https://im.example.test/sets/" + existing, ["version"] = VersionOf(id), ["warnings"] = new JArray() });
                        if (current == "expired") return FakeHandler.Error((HttpStatusCode)410, "bundle_expired");
                        if (current == "failed") return FakeHandler.Error(HttpStatusCode.Conflict, "bundle_failed");
                        if (current == "finalizing") return FakeHandler.Error(HttpStatusCode.Conflict, "finalize_in_progress");
                        if (current != "open") return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
                        if (RejectFinalizeWithCode != null) return Fail(id, HttpStatusCode.BadRequest, RejectFinalizeWithCode);
                        var manifest = (JObject)request.Json["manifest"];
                        FinalizeManifests.Add(manifest);
                        var files = (JObject)manifest["files"];
                        var hasSheets = ((JArray)manifest["sheets"]).Count > 0;
                        if (hasSheets != (files["pdf"] != null) || (files["pdf"] == null && files["glb"] == null))
                            return Fail(id, HttpStatusCode.BadRequest, "invalid_manifest"); // 只有命名視圖：PDF 與 GLB 至少要有一個
                        foreach (var property in files.Properties())
                        {
                            SortedDictionary<int, byte[]> parts;
                            if (!_stored.ContainsKey(id) || !_stored[id].TryGetValue(property.Name, out parts)) return Fail(id, HttpStatusCode.BadRequest, "invalid_file");
                            var joined = parts.Values.SelectMany(b2 => b2).ToArray();
                            string hex;
                            using (var sha = SHA256.Create()) hex = string.Concat(sha.ComputeHash(joined).Select(b2 => b2.ToString("x2")));
                            if (joined.Length != (long)property.Value["bytes"] || hex != (string)property.Value["sha256"]) return Fail(id, HttpStatusCode.BadRequest, "checksum_mismatch");
                        }
                        var setId = "set-" + id;
                        _finalizedSetIds[id] = setId;
                        BundleStatus[id] = "finalized";
                        return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["setId"] = setId, ["url"] = "https://im.example.test/sets/" + setId, ["version"] = VersionOf(id), ["warnings"] = new JArray("測試警告", "named_views_skipped:3") });
                    }
                }
            }
            return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
        }
    }
}
