using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    public static class Jsons
    {
        /// <summary>解析 JSON 但不把 ISO 8601 字串轉成 DateTime（否則 exportedAt 會變成 Date 型別、字串比對全錯）。</summary>
        public static JObject Parse(string text)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
            {
                return JObject.Load(reader);
            }
        }
    }

    /// <summary>記錄一次收到的請求（本文在送出當下就讀成字串，因為 HttpContent 事後會被釋放）。</summary>
    public sealed class RecordedRequest
    {
        public HttpMethod Method { get; set; }
        public Uri Uri { get; set; }
        public string Body { get; set; }
        public byte[] BodyBytes { get; set; }
        public string Authorization { get; set; }
        public string ApiVersion { get; set; }
        public string PartSha256 { get; set; }
        public JObject Json { get { return string.IsNullOrEmpty(Body) ? null : Jsons.Parse(Body); } }
    }

    /// <summary>可程式化的假 HTTP 處理器：依請求回傳指定回應，並保留所有請求供斷言。</summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> _respond;
        public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();
        public Func<RecordedRequest, Task> BeforeRespond { get; set; }

        public FakeHandler(Func<RecordedRequest, HttpResponseMessage> respond) { _respond = respond; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri,
                Authorization = request.Headers.Authorization == null ? null : request.Headers.Authorization.ToString(),
            };
            IEnumerable<string> versions;
            if (request.Headers.TryGetValues("X-Addin-Api", out versions)) recorded.ApiVersion = string.Join(",", versions);
            if (request.Headers.TryGetValues("X-Part-Sha256", out versions)) recorded.PartSha256 = string.Join(",", versions);
            if (request.Content != null)
            {
                recorded.BodyBytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                recorded.Body = Encoding.UTF8.GetString(recorded.BodyBytes);
            }
            lock (Requests) Requests.Add(recorded);
            if (BeforeRespond != null) await BeforeRespond(recorded).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _respond(recorded);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, JObject body)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        }

        public static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            return Json(status, new JObject { ["error"] = code });
        }
    }

    public sealed class InMemoryVault : ITokenVault
    {
        public StoredCredentials Stored { get; set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }
        public StoredCredentials Load() { return Stored; }
        public void Save(StoredCredentials credentials) { Stored = credentials; SaveCount++; }
        public void Delete() { Stored = null; DeleteCount++; }
    }

    public sealed class ActionBrowser : IBrowserLauncher
    {
        private readonly Action<string> _open;
        public string LastUrl { get; private set; }
        public ActionBrowser(Action<string> open) { _open = open; }
        public void Open(string url) { LastUrl = url; _open(url); }
    }

    public sealed class RecordingLog
    {
        public List<string> Lines { get; } = new List<string>();
        public void Write(string line) { lock (Lines) Lines.Add(line); }
        public string All { get { lock (Lines) return string.Join("\n", Lines); } }
    }

    public static class TempDir
    {
        public static string Create(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "revitmcp-im-tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(dir.FullName, "docs", "schemas"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("找不到 repo 根目錄（含 CLAUDE.md 與 docs/schemas）。");
        }
    }

    /// <summary>快速建立測試用的圖紙、視圖與快照。</summary>
    public static class Samples
    {
        public static ViewItem View(string id, string name, string kind)
        {
            return new ViewItem { UniqueId = id, ElementId = Math.Abs(id.GetHashCode()), Name = name, Kind = kind };
        }

        public static SheetItem Sheet(string id, string number, string title, params ViewportItem[] viewports)
        {
            var sheet = new SheetItem { UniqueId = id, ElementId = Math.Abs(id.GetHashCode()), Number = number, Title = title };
            sheet.Viewports.AddRange(viewports);
            return sheet;
        }

        public static ViewportItem Viewport(string viewId, double width, double height)
        {
            return new ViewportItem { ViewUniqueId = viewId, Width = width, Height = height };
        }

        /// <summary>A-2（平面＋剖面）、A-10（3D＋小剖面）、A-1（空圖紙）三張圖紙；另有不在圖紙上的立面與 3D；兩個圖紙集。</summary>
        public static ProjectSnapshot Snapshot()
        {
            var snapshot = new ProjectSnapshot { DocumentTitle = "AR_CPC", DocumentPath = @"C:\models\AR_CPC.rvt" };
            snapshot.Views.AddRange(new[]
            {
                View("v-plan-2f", "2F", ViewKinds.Plan),
                View("v-sec-aa", "A-A 剖面", ViewKinds.Section),
                View("v-3d-main", "主透視", ViewKinds.ThreeD),
                View("v-sec-bb", "B-B 剖面", ViewKinds.Section),
                View("v-elev-n", "北立面", ViewKinds.Elevation),
                View("v-3d-free", "鳥瞰", ViewKinds.ThreeD)
            });
            snapshot.Sheets.AddRange(new[]
            {
                Sheet("s-a10", "A-10", "剖面與透視", Viewport("v-sec-bb", 1, 1), Viewport("v-3d-main", 3, 2)),
                Sheet("s-a2", "A-2", "二層平面圖", Viewport("v-plan-2f", 4, 3), Viewport("v-sec-aa", 1, 1)),
                Sheet("s-a1", "A-1", "封面")
            });
            snapshot.SheetSets.Add(new SheetSetItem { Name = "送審圖", SheetUniqueIds = { "s-a2" }, ViewUniqueIds = { "v-elev-n" } });
            snapshot.SheetSets.Add(new SheetSetItem { Name = "全部", SheetUniqueIds = { "s-a1", "s-a2", "s-a10" } });
            return snapshot;
        }
    }
}
