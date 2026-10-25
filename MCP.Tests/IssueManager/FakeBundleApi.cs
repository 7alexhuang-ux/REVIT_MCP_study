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
    /// <summary>FakeTokenServer 的版本包 API 部分（revit-addin-export.md §5）：建立、分段 PUT、finalize（冪等）、DELETE。</summary>
    public sealed partial class FakeTokenServer
    {
        public long PartBytes = 10;
        /// <summary>簽名上傳網址的主機；預設與 API 同主機（本機開發模式），測試「Bearer 不送給別的主機」時改成別的。</summary>
        public string UploadHost = "im.example.test";
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

        public byte[] StoredFile(string bundleId, string kind)
        {
            lock (_gate)
            {
                return _stored[bundleId][kind].Values.SelectMany(b => b).ToArray();
            }
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
                    if (FailNextPuts > 0)
                    {
                        FailNextPuts--;
                        return FakeHandler.Error(FailPutStatus, "temporarily_unavailable");
                    }
                    if (!_stored.ContainsKey(parts[2])) return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
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
                if (stage != "PD" && stage != "SD" && stage != "DD") return FakeHandler.Error(HttpStatusCode.BadRequest, "stage_required");
                if (string.IsNullOrWhiteSpace((string)body["description"])) return FakeHandler.Error(HttpStatusCode.BadRequest, "description_required");
                var id = "bundle-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                lock (_gate) _stored[id] = new Dictionary<string, SortedDictionary<int, byte[]>>();
                var files = new JArray();
                foreach (var kind in new[] { "pdf", "glb", "namedViews", "viewPositions" })
                {
                    files.Add(new JObject
                    {
                        ["kind"] = kind, ["name"] = kind, ["partBytes"] = PartBytes,
                        ["urls"] = new JArray(Enumerable.Range(0, UrlsPerFile).Select(i => (object)("https://" + UploadHost + "/upload/" + id + "/" + kind + "/" + i)).ToArray())
                    });
                }
                return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["bundleId"] = id, ["expiresAt"] = BundleExpiresAt.ToString("o"), ["files"] = files });
            }

            if (segments.Length >= 5)
            {
                var id = segments[4];
                if (request.Method == HttpMethod.Delete)
                {
                    lock (_gate) { Deleted.Add(id); _stored.Remove(id); }
                    return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["deleted"] = true });
                }
                if (request.Method == HttpMethod.Post && segments.Length == 6 && segments[5] == "finalize")
                {
                    lock (_gate)
                    {
                        if (RejectFinalizeWithCode != null) return FakeHandler.Error(HttpStatusCode.BadRequest, RejectFinalizeWithCode);
                        string existing;
                        if (_finalizedSetIds.TryGetValue(id, out existing))
                            return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["setId"] = existing, ["url"] = "https://im.example.test/sets/" + existing, ["warnings"] = new JArray() });
                        var manifest = (JObject)request.Json["manifest"];
                        FinalizeManifests.Add(manifest);
                        foreach (var property in ((JObject)manifest["files"]).Properties())
                        {
                            SortedDictionary<int, byte[]> parts;
                            if (!_stored.ContainsKey(id) || !_stored[id].TryGetValue(property.Name, out parts)) return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_file");
                            var joined = parts.Values.SelectMany(b => b).ToArray();
                            string hex;
                            using (var sha = SHA256.Create()) hex = string.Concat(sha.ComputeHash(joined).Select(b => b.ToString("x2")));
                            if (joined.Length != (long)property.Value["bytes"] || hex != (string)property.Value["sha256"]) return FakeHandler.Error(HttpStatusCode.BadRequest, "checksum_mismatch");
                        }
                        var setId = "set-" + id;
                        _finalizedSetIds[id] = setId;
                        return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["setId"] = setId, ["url"] = "https://im.example.test/sets/" + setId, ["warnings"] = new JArray("測試警告") });
                    }
                }
            }
            return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
        }
    }
}
