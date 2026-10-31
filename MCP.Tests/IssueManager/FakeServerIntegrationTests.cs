using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// 外掛客戶端 ↔ 假 Issue Manager（Node 程序，tools/fake-issue-manager/server.mjs）的整合測試：
    /// 真的 HttpClient、真的 loopback 接收、真的分段 PUT；只有 Revit 匯出步驟用假檔案。
    /// 沒有 Node.js 時略過（不算通過）。
    /// </summary>
    [TestFixture]
    public class FakeServerIntegrationTests
    {
        private Process _node;
        private string _url;
        private string _dir;
        private HttpClient _http;

        [OneTimeSetUp]
        public void StartFakeServer()
        {
            var node = GlbMergeRunner.FindNode();
            if (node == null) Assert.Ignore("找不到 Node.js，略過整合測試。");
            var script = Path.Combine(TempDir.RepoRoot(), "tools", "fake-issue-manager", "server.mjs");
            var psi = new ProcessStartInfo
            {
                FileName = node, Arguments = "\"" + script + "\" --auto-approve --part-bytes 64",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            _node = Process.Start(psi);
            var line = _node.StandardOutput.ReadLine();
            Assert.IsNotNull(line, "假伺服器沒有輸出網址");
            var start = line.IndexOf("http://", StringComparison.Ordinal);
            _url = line.Substring(start).Split(' ')[0].Trim();
            _http = new HttpClient();
        }

        [OneTimeTearDown]
        public void StopFakeServer()
        {
            if (_http != null) _http.Dispose();
            if (_node != null && !_node.HasExited) { try { _node.Kill(); } catch (Exception) { } }
            if (_node != null) _node.Dispose();
        }

        [SetUp] public void SetUp() { _dir = TempDir.Create("integration"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private AddinAuthService NewAuth(InMemoryVault vault = null, Func<DateTimeOffset> now = null)
        {
            return new AddinAuthService(new HttpClient(), vault ?? new InMemoryVault(), _url, ClientInfo.Create("整合測試-PC", "1.0.0.0"), now);
        }

        /// <summary>瀏覽器＝一個會跟隨轉址的 HttpClient：假伺服器 auto-approve 後 302 到 loopback callback。</summary>
        private static ActionBrowser AutoBrowser()
        {
            return new ActionBrowser(url => Task.Run(async () => { using (var c = new HttpClient()) { try { await c.GetAsync(url); } catch (Exception) { } } }));
        }

        private async Task<JObject> StateAsync() { return Jsons.Parse(await _http.GetStringAsync(_url + "/__state")); }

        [Test]
        public async Task FullFlow_LoginProjectsUploadFinalize_ManifestMatchesSchema()
        {
            var auth = NewAuth();
            var user = await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            Assert.AreEqual("fake.user@example.test", user.Email);
            var api = new AddinApiClient(new HttpClient(), auth, ClientInfo.Create("整合測試-PC", "1.0.0.0"));
            var projects = await api.GetProjectsAsync(CancellationToken.None);
            Assert.AreEqual("project-1", projects[0].Id);

            var steps = new FakeSteps { PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 300) + "\n/Type /Page\n/Type /Page\n"), GlbBytes = Encoding.ASCII.GetBytes("glTF" + new string('y', 500)) };
            var request = RequestFactory.Full();
            request.ProjectId = "project-1";
            var flow = new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"));
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);

            var state = await StateAsync();
            var bundle = state["bundles"].First(b => (string)b["setId"] == result.SetId);
            Assert.AreEqual("finalized", (string)bundle["status"]);
            Assert.AreEqual("SD", (string)bundle["stage"]);
            Assert.AreEqual("測試版本", (string)bundle["description"]);
            var schema = new MiniJsonSchema(JObject.Parse(File.ReadAllText(Path.Combine(TempDir.RepoRoot(), "docs", "schemas", "bundle-manifest.schema.json"))));
            Assert.IsEmpty(schema.Validate(bundle["manifest"]), "伺服器收到的 manifest 必須通過 schema");
            Assert.AreEqual(2, bundle["manifest"]["sheets"].Count());
            Assert.Greater((int)state["putCount"], 5, "64 bytes 一段 → 多段上傳");
        }

        [Test]
        public async Task CancelAfterBundleCreationRemovesItOnTheServer()
        {
            var auth = NewAuth();
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var api = new AddinApiClient(new HttpClient(), auth, ClientInfo.Create("整合測試-PC", "1.0.0.0"));
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.ProjectId = "project-1";
            using (var cts = new CancellationTokenSource())
            {
                steps.OnCall = name => { if (name == "pdf") cts.Cancel(); };
                var flow = new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"));
                var before = (int)(await StateAsync())["deleted"].Count();
                Assert.CatchAsync<OperationCanceledException>(async () => await flow.RunAsync(request, null, null, cts.Token));
                var after = await StateAsync();
                Assert.AreEqual(before + 1, after["deleted"].Count());
            }
        }

        [Test]
        public async Task TransientUploadFailuresAreRetriedByTheRealUploader()
        {
            var auth = NewAuth();
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var api = new AddinApiClient(new HttpClient(), auth, ClientInfo.Create("整合測試-PC", "1.0.0.0"));
            await _http.PostAsync(_url + "/__config", new StringContent("{\"failNextPuts\":2}", Encoding.UTF8, "application/json"));
            var request = RequestFactory.Full();
            request.ProjectId = "project-1";
            var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles"),
                uploader: new BundleUploader(api, 3, (span, token) => Task.FromResult(0)));
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);
            Assert.IsNotNull(result.SetId);
        }

        [Test]
        public async Task StageRejectedByTheServerSurfacesAsPlainMessage()
        {
            var auth = NewAuth();
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var api = new AddinApiClient(new HttpClient(), auth, ClientInfo.Create("整合測試-PC", "1.0.0.0"));
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.CreateBundleAsync("project-1", null, "x", new BundleSummary(), CancellationToken.None));
            StringAssert.Contains("設計階段", ex.Message);
        }

        [Test]
        public async Task RefreshRotationThroughRealHttpAndReuseOfAnOldRefreshKillsTheFamily()
        {
            var vault = new InMemoryVault();
            var clock = DateTimeOffset.UtcNow;
            var auth = NewAuth(vault, () => clock);
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var firstRefresh = vault.Stored.RefreshToken;
            var firstAccess = await auth.GetAccessTokenAsync(CancellationToken.None);

            clock = clock.AddHours(2);
            var secondAccess = await auth.GetAccessTokenAsync(CancellationToken.None);
            Assert.AreNotEqual(firstAccess, secondAccess);
            Assert.AreNotEqual(firstRefresh, vault.Stored.RefreshToken, "輪替後的 refresh 已更新並落檔");

            // 另一個程序（或被偷走的舊 refresh）重用已被輪替掉的 refresh
            var thief = NewAuth(new InMemoryVault { Stored = new StoredCredentials { BaseUrl = _url, RefreshToken = firstRefresh, RefreshExpiresAt = clock.AddDays(5), UserEmail = "x" } }, () => clock);
            Assert.IsTrue(thief.TryRestore());
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await thief.GetAccessTokenAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NeedLogin, ex.Kind);

            // 家族被整個撤銷：合法的那一份也不能再用
            clock = clock.AddHours(2);
            var legit = Assert.ThrowsAsync<AddinApiException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NeedLogin, legit.Kind);
            Assert.IsNull(vault.Stored);
        }

        [Test]
        public async Task LogoutAfterAccessExpiredStillRevokesTheWholeFamilyOnTheServer()
        {
            // 審查探針：access 只活 1 秒，2.5 秒後登出。舊版拿過期 access 當 Bearer → 401 且沒撤銷（revokedTokens 為 0）。
            await _http.PostAsync(_url + "/__config", new StringContent("{\"accessTtlSec\":1}", Encoding.UTF8, "application/json"));
            try
            {
                var vault = new InMemoryVault();
                var auth = NewAuth(vault);
                await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
                var before = (int)(await StateAsync())["revokedTokens"];
                await Task.Delay(2500);

                var outcome = await auth.LogoutAsync(CancellationToken.None);

                Assert.AreEqual(LogoutOutcome.Revoked, outcome);
                Assert.AreEqual(before + 2, (int)(await StateAsync())["revokedTokens"], "access 與 refresh 兩個權杖都要在伺服器端被撤銷");
                Assert.IsNull(vault.Stored);
            }
            finally
            {
                await _http.PostAsync(_url + "/__config", new StringContent("{\"accessTtlSec\":3600}", Encoding.UTF8, "application/json"));
            }
        }

        // ── 對齊網站端合約（真的 HTTP、真的逾時）─────────────────────────

        private async Task ConfigAsync(string json)
        {
            await _http.PostAsync(_url + "/__config", new StringContent(json, Encoding.UTF8, "application/json"));
        }

        private async Task<AddinApiClient> LoggedInApiAsync(HttpClient finalizeHttp = null)
        {
            var auth = NewAuth();
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            return new AddinApiClient(new HttpClient(), auth, ClientInfo.Create("整合測試-PC", "1.0.0.0"), null, finalizeHttp);
        }

        private sealed class StageHook : IUploadFlowProgress
        {
            public Action<string> OnStage = text => { };
            public List<string> Stages = new List<string>();
            public void Stage(string text) { Stages.Add(text); OnStage(text); }
            public void Upload(UploadProgress progress) { }
        }

        [Test]
        public async Task FinalizeThatOutlastsTheHttpTimeoutIsPolledUntilTheServerFinishesAndRunsOnlyOnce()
        {
            // 伺服器 finalize 要 1.5 秒；finalize 專用 HttpClient 只等 0.5 秒 → 逾時 → 重送 → 409 finalize_in_progress → 輪詢 → 200。
            await ConfigAsync("{\"finalizeDelayMs\":1500}");
            try
            {
                var api = await LoggedInApiAsync(new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) });
                var request = RequestFactory.Full();
                request.ProjectId = "project-1";
                var before = (int)(await StateAsync())["finalizeCount"];
                var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles")) { FinalizePollInterval = TimeSpan.FromMilliseconds(200) };
                var hook = new StageHook();
                var result = await flow.RunAsync(request, null, hook, CancellationToken.None);

                Assert.IsNotNull(result.SetId);
                Assert.AreEqual(before + 1, (int)(await StateAsync())["finalizeCount"], "伺服器端只處理一次（重送的是冪等查詢）");
                Assert.IsTrue(hook.Stages.Any(t => t.Contains("伺服器處理中")), string.Join("|", hook.Stages));
            }
            finally { await ConfigAsync("{\"finalizeDelayMs\":0}"); }
        }

        [Test]
        public async Task CancelWhileTheRealServerIsFinalizingReportsThatItCannotBeCancelled()
        {
            await ConfigAsync("{\"finalizeDelayMs\":1500}");
            try
            {
                var api = await LoggedInApiAsync();
                var request = RequestFactory.Full();
                request.ProjectId = "project-1";
                var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles"));
                using (var cts = new CancellationTokenSource())
                {
                    var hook = new StageHook { OnStage = text => { if (text == Strings.FinalizeStage) cts.CancelAfter(300); } };
                    var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(request, null, hook, cts.Token));
                    Assert.AreEqual("finalize_in_progress", ex.Code);
                    StringAssert.Contains("無法取消", ex.Message);
                }
                Assert.IsFalse(flow.CanResume);
                await Task.Delay(1500); // 讓伺服器把這個版本做完，不影響後面的測試
            }
            finally { await ConfigAsync("{\"finalizeDelayMs\":0}"); }
        }

        [Test]
        public async Task DeleteAfterFinalizeIsRefusedByTheRealServerAndTheVersionStays()
        {
            var api = await LoggedInApiAsync();
            var request = RequestFactory.Full();
            request.ProjectId = "project-1";
            var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles"));
            var result = await flow.RunAsync(request, null, null, CancellationToken.None);
            var deletedBefore = (await StateAsync())["deleted"].Count();

            Assert.AreEqual(BundleDeleteOutcome.AlreadyFinalized, await api.DeleteBundleDetailedAsync(result.BundleId, CancellationToken.None));
            Assert.IsFalse(await api.DeleteBundleAsync(result.BundleId, CancellationToken.None));

            var state = await StateAsync();
            Assert.AreEqual(deletedBefore, state["deleted"].Count());
            Assert.AreEqual("finalized", (string)state["bundles"].First(b => (string)b["id"] == result.BundleId)["status"]);
        }

        [Test]
        public async Task ServerSideFinalizeFailureVoidsTheBundleAndRetryRecreatesIt()
        {
            var api = await LoggedInApiAsync();
            var request = RequestFactory.Full();
            request.ProjectId = "project-1";
            var steps = new FakeSteps();
            var flow = new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"));
            await ConfigAsync("{\"failNextFinalize\":1}");
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(request, null, null, CancellationToken.None));
            Assert.AreEqual("finalize_failed", ex.Code);
            StringAssert.Contains("作廢", ex.Message);
            Assert.IsTrue(flow.CanResume);

            var result = await flow.ResumeAsync(null, CancellationToken.None);
            var bundles = (await StateAsync())["bundles"].ToList();
            var voided = bundles.Where(b => (string)b["status"] == "failed").ToList();
            Assert.IsNotEmpty(voided, "第一個版本包被伺服器作廢");
            Assert.AreEqual("finalized", (string)bundles.First(b => (string)b["id"] == result.BundleId)["status"]);
            Assert.AreEqual(1, steps.Calls.Count(c => c == "pdf"), "不重新匯出");
        }

        [Test]
        public async Task AViewsOnlyPackageIsRejectedAsInvalidManifestAndNotRetried()
        {
            var api = await LoggedInApiAsync();
            var request = new ExportRequest { ProjectId = "project-1", Stage = "SD", Description = "只有視圖", IncludeModel = false };
            request.Views.Add(Samples.View("v-3d", "主透視", ViewKinds.ThreeD));
            var flow = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles"));
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(request, null, null, CancellationToken.None));
            Assert.AreEqual("invalid_manifest", ex.Code);
            StringAssert.Contains("清單", ex.Message);
            Assert.IsFalse(flow.CanResume);
        }

        [Test]
        public async Task TheRealServerAnswers201AndTheFiveFileKinds()
        {
            var api = await LoggedInApiAsync();
            var created = await api.CreateBundleAsync("project-1", "SD", "x", new BundleSummary(), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews", "viewPositions", "params" }, created.Files.Select(f => f.Kind).ToArray());
            Assert.AreEqual(1000, created.Limits.PdfPages);
            await api.DeleteBundleAsync(created.BundleId, CancellationToken.None);
        }

        [Test]
        public async Task LogoutRevokesOnTheRealServer()
        {
            var vault = new InMemoryVault();
            var auth = NewAuth(vault);
            await auth.LoginAsync(AutoBrowser(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var access = await auth.GetAccessTokenAsync(CancellationToken.None);
            await auth.LogoutAsync(CancellationToken.None);
            using (var request = new HttpRequestMessage(HttpMethod.Get, _url + "/api/addin/me"))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
                var response = await _http.SendAsync(request);
                Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            }
            Assert.IsNull(vault.Stored);
        }
    }
}
