using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// 對齊網站端版本包 API 的實際合約（Issue Manager imv4a：docs/spec/revit-addin-export.md §5.2、handoff.md §4 的 16 項差異）。
    /// 假伺服器（FakeBundleApi.cs）已改成網站端的行為：201、invalid_input、三種 kind、finalize 驗證失敗即作廢、409／410 …
    /// </summary>
    [TestFixture]
    public class ServerContractAlignmentTests
    {
        private string _dir;
        private FakeTokenServer _server;

        [SetUp] public void SetUp() { _dir = TempDir.Create("contract"); _server = new FakeTokenServer { PartBytes = 16 }; }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static Func<TimeSpan, CancellationToken, Task> NoDelay { get { return (span, token) => Task.FromResult(0); } }

        private async Task<UploadFlow> NewFlow(FakeSteps steps, Func<DateTimeOffset> now = null, List<TimeSpan> delays = null, AddinApiClient api = null)
        {
            api = api ?? await Harness.LoggedInApiAsync(_server);
            // 固定時鐘：版本包到期時間是假伺服器的固定日期，測試不能跟真實時間賽跑。
            now = now ?? (() => new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
            return new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"),
                uploader: new BundleUploader(api, 2, NoDelay), now: now,
                delay: (span, token) => { if (delays != null) delays.Add(span); return Task.FromResult(0); });
        }

        private sealed class StageLog : IUploadFlowProgress
        {
            public List<string> Stages = new List<string>();
            public void Stage(string text) { Stages.Add(text); }
            public void Upload(UploadProgress progress) { }
        }

        private static string BundleIdOf(RecordedRequest request)
        {
            return request.Uri.AbsolutePath.Split('/')[5]; // "", api, addin, v1, bundles, <id>, finalize
        }

        private static bool IsFinalize(RecordedRequest request)
        {
            return request.Uri.AbsolutePath.EndsWith("/finalize", StringComparison.Ordinal);
        }

        private static HttpResponseMessage Status(int status, string code)
        {
            return FakeHandler.Error((HttpStatusCode)status, code);
        }

        private sealed class RewriteStatusHandler : DelegatingHandler
        {
            private readonly HttpStatusCode _from;
            private readonly HttpStatusCode _to;
            public RewriteStatusHandler(HttpMessageHandler inner, HttpStatusCode from, HttpStatusCode to) { InnerHandler = inner; _from = from; _to = to; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == _from) response.StatusCode = _to;
                return response;
            }
        }

        private sealed class RecordingHandler : DelegatingHandler
        {
            public readonly List<string> Paths = new List<string>();
            public RecordingHandler(HttpMessageHandler inner) { InnerHandler = inner; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Paths) Paths.Add(request.Method + " " + request.RequestUri.AbsolutePath);
                return base.SendAsync(request, cancellationToken);
            }
        }

        // ── #1 建立回 201（舊假伺服器 200）──────────────────────────

        [Test]
        public async Task CreateBundleAccepts201AndTheOlder200()
        {
            var api201 = await Harness.LoggedInApiAsync(_server);
            var created = await api201.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            Assert.IsNotNull(created.BundleId);

            var server200 = new FakeTokenServer();
            var login = await Harness.LoggedInAsync(server200);
            var http = new HttpClient(new RewriteStatusHandler(server200.Handler, HttpStatusCode.Created, HttpStatusCode.OK));
            var api200 = new AddinApiClient(http, login.Item1, ClientInfo.Create("PC", "1.0.0.0"));
            var createdOk = await api200.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            Assert.IsNotNull(createdOk.BundleId);
        }

        [Test]
        public async Task CreateBundleReadsLimitsAndOnlyTheThreeFileKinds()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews" }, created.Files.Select(f => f.Kind).ToArray());
            Assert.IsNull(created.Find("viewPositions"));
            Assert.AreEqual(314572800L, created.Limits.PdfBytes);
            Assert.AreEqual(524288000L, created.Limits.GlbBytes);
            Assert.AreEqual(4194304L, created.Limits.NamedViewsBytes);
            Assert.AreEqual(1000, created.Limits.PdfPages);
        }

        [Test]
        public async Task ManifestNeverContainsViewPositionsEvenIfAFileOfThatKindIsPassed()
        {
            var dir = Path.Combine(_dir, "vp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "view_positions.json");
            File.WriteAllText(path, "{}");
            var files = new[] { BundleExporter.Describe(BundleFile.Pdf, "sheets.pdf", WritePdf(dir, 2)), BundleExporter.Describe(BundleFile.ViewPositions, "view_positions.json", path) };
            var manifest = ManifestBuilder.Build(RequestFactory.Full(), new RevitDocInfo { Title = "AR", VersionNumber = "2024" }, DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, files, null);
            CollectionAssert.AreEqual(new[] { "pdf" }, ((JObject)manifest["files"]).Properties().Select(p => p.Name).ToArray());
            await Task.FromResult(0);
        }

        private static string WritePdf(string dir, int pages)
        {
            var path = Path.Combine(dir, "sheets.pdf");
            File.WriteAllBytes(path, FakeSteps.PdfWithPages(pages));
            return path;
        }

        // ── #2 / #9 錯誤碼與白話訊息 ───────────────────────────────

        [Test]
        public async Task InvalidInputWithStageFieldIsShownAsTheStageMessage()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.CreateBundleAsync("p1", "pd", "x", new BundleSummary(), CancellationToken.None));
            Assert.AreEqual("invalid_input", ex.Code);
            Assert.AreEqual(400, ex.StatusCode);
            StringAssert.Contains("設計階段", ex.Message);
        }

        [TestCase(400, "invalid_input", "stage", "設計階段")]
        [TestCase(400, "invalid_input", "description", "資料有誤")]
        [TestCase(400, "invalid_input", null, "資料有誤")]
        [TestCase(409, "bundle_failed", null, "作廢")]
        [TestCase(410, "bundle_expired", null, "過期")]
        [TestCase(410, null, null, "過期")]
        [TestCase(409, "finalize_in_progress", null, "處理")]
        [TestCase(409, "already_finalized", null, "網站刪除")]
        [TestCase(409, "bundle_closed", null, "不接受")]
        [TestCase(400, "page_count_mismatch", null, "頁數")]
        [TestCase(400, "invalid_pdf", null, "PDF")]
        [TestCase(400, "invalid_manifest", null, "清單")]
        [TestCase(400, "invalid_file", null, "檔案")]
        [TestCase(400, "checksum_mismatch", null, "雜湊")]
        [TestCase(413, "payload_too_large", null, "上限")]
        [TestCase(429, "too_many_open_bundles", null, "未完成")]
        [TestCase(500, "server_misconfigured", null, "設定")]
        [TestCase(500, "finalize_failed", null, "作廢")]
        [TestCase(503, "uploads_paused", null, "暫停")]
        [TestCase(426, "client_too_old", null, "版本過舊")]
        [TestCase(426, "upgrade_required", null, "更新")]
        [TestCase(426, null, null, "更新")]
        public async Task EveryServerErrorCodeHasAPlainChineseMessageWithoutEchoingTheCode(int status, string code, string field, string expected)
        {
            var body = new JObject();
            if (code != null) body["error"] = code;
            if (field != null) body["field"] = field;
            var response = FakeHandler.Json((HttpStatusCode)status, body);
            var ex = await AddinHttp.ToExceptionAsync(response);
            StringAssert.Contains(expected, ex.Message);
            if (code != null) StringAssert.DoesNotContain(code, ex.Message, "訊息是固定的白話文，不回顯代碼");
            Assert.AreEqual(status, ex.StatusCode);
        }

        [Test]
        public async Task UnknownCodeStillShowsTheShortCodeButNeverFreeText()
        {
            var ex = await AddinHttp.ToExceptionAsync(FakeHandler.Json((HttpStatusCode)418, new JObject { ["error"] = "some_new_code", ["detail"] = "secret imat_xxx" }));
            StringAssert.Contains("some_new_code", ex.Message);
            StringAssert.DoesNotContain("secret", ex.Message);
        }

        // ── #3 取消與 finalize 之後 ────────────────────────────────

        [Test]
        public async Task DeleteOutcomeDistinguishesDeletedFinalizedAndInProgress()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var open = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var done = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var busy = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            _server.MarkFinalized(done.BundleId);
            _server.BundleStatus[busy.BundleId] = "finalizing";

            Assert.AreEqual(BundleDeleteOutcome.Deleted, await api.DeleteBundleDetailedAsync(open.BundleId, CancellationToken.None));
            Assert.AreEqual(BundleDeleteOutcome.AlreadyFinalized, await api.DeleteBundleDetailedAsync(done.BundleId, CancellationToken.None));
            Assert.AreEqual(BundleDeleteOutcome.FinalizeInProgress, await api.DeleteBundleDetailedAsync(busy.BundleId, CancellationToken.None));
            Assert.AreEqual(BundleDeleteOutcome.Deleted, await api.DeleteBundleDetailedAsync("no-such-bundle", CancellationToken.None), "404＝已經不存在，視為清乾淨了");
            Assert.IsFalse(await api.DeleteBundleAsync(done.BundleId, CancellationToken.None));
        }

        [Test]
        public async Task CancelWhileFinalizingAfterTheServerFinishedTellsTheUserToDeleteTheVersionOnTheWebsite()
        {
            var flow = await NewFlow(new FakeSteps());
            using (var cts = new CancellationTokenSource())
            {
                // finalize 請求送出時使用者按了取消；但伺服器其實已經建立好版本。
                _server.Handler.BeforeRespond = r =>
                {
                    if (IsFinalize(r)) { _server.MarkFinalized(BundleIdOf(r)); cts.Cancel(); }
                    return Task.FromResult(0);
                };
                var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, cts.Token));
                Assert.AreEqual("already_finalized", ex.Code);
                StringAssert.Contains("已經上傳完成，無法取消", ex.Message);
                StringAssert.Contains("網站", ex.Message);
            }
            Assert.IsEmpty(_server.Deleted, "版本已建立，DELETE 什麼都沒刪");
            Assert.IsFalse(flow.CanResume);
            Assert.IsFalse(UploadFlow.IsRunning);
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "bundles")) && Directory.GetDirectories(Path.Combine(_dir, "bundles")).Length > 0, "本機暫存仍清掉");
        }

        [Test]
        public async Task CancelWhileTheServerIsStillFinalizingSaysSo()
        {
            var flow = await NewFlow(new FakeSteps());
            using (var cts = new CancellationTokenSource())
            {
                _server.Handler.BeforeRespond = r =>
                {
                    if (IsFinalize(r)) { _server.BundleStatus[BundleIdOf(r)] = "finalizing"; cts.Cancel(); }
                    return Task.FromResult(0);
                };
                var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, cts.Token));
                Assert.AreEqual("finalize_in_progress", ex.Code);
                StringAssert.Contains("無法取消", ex.Message);
            }
            Assert.IsEmpty(_server.Deleted);
        }

        // ── #5 finalize 失敗即作廢：重建 bundle 後重送 ──────────────────

        [Test]
        public async Task ResumeAfterAValidationFailureRecreatesTheBundleInsteadOfResendingFinalize()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            _server.RejectFinalizeWithCode = "checksum_mismatch";
            var first = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            Assert.AreEqual("checksum_mismatch", first.Code);
            Assert.IsTrue(flow.CanResume, "檔案還在，可以重試（不必重新匯出）");
            var dead = _server.CreatedBundleIds.Single();
            Assert.AreEqual("failed", _server.BundleStatus[dead]);
            var putsBefore = _server.Puts.Count;

            _server.RejectFinalizeWithCode = null;
            var stages = new StageLog();
            var result = await flow.ResumeAsync(stages, CancellationToken.None);

            Assert.AreEqual(2, _server.CreatedBundleIds.Count, "重試時重新建立版本包");
            var fresh = _server.CreatedBundleIds[1];
            Assert.AreEqual(fresh, result.BundleId);
            CollectionAssert.AreEqual(new[] { dead, fresh }, _server.FinalizeRequestBundleIds, "不會再對作廢的版本包送 finalize（會得到 409）");
            Assert.IsEmpty(_server.Deleted, "作廢的版本包不用 DELETE");
            Assert.AreEqual(1, steps.Calls.Count(c => c == "glb"), "不重新匯出");
            Assert.Greater(_server.Puts.Count, putsBefore, "新的版本包整批重傳");
            Assert.IsTrue(_server.Puts.Skip(putsBefore).All(p => p.Uri.AbsolutePath.Contains("/" + fresh + "/")), "重傳的目標是新的版本包");
            CollectionAssert.Contains(stages.Stages, Strings.RecreateBundleStage);
            Assert.AreEqual("finalized", _server.BundleStatus[fresh]);
            Assert.IsFalse(flow.CanResume);
        }

        [Test]
        public async Task RecreatedBundleReportsTheRealTotalBytesInTheSummary()
        {
            var flow = await NewFlow(new FakeSteps());
            _server.RejectFinalizeWithCode = "invalid_file";
            Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            _server.RejectFinalizeWithCode = null;
            await flow.ResumeAsync(null, CancellationToken.None);
            Assert.AreEqual(0, (long)_server.CreateRequests[0]["summary"]["bytes"], "第一次建立時還不知道大小");
            Assert.Greater((long)_server.CreateRequests[1]["summary"]["bytes"], 0, "重建時已知道實際大小");
            Assert.AreEqual((string)_server.CreateRequests[0]["description"], (string)_server.CreateRequests[1]["description"]);
        }

        [TestCase("invalid_manifest")]
        [TestCase("page_count_mismatch")]
        [TestCase("invalid_pdf")]
        public async Task ContentRejectedByTheServerIsNotRetriedBecauseTheSameFilesWouldBeRejectedAgain(string code)
        {
            var flow = await NewFlow(new FakeSteps());
            _server.RejectFinalizeWithCode = code;
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            Assert.AreEqual(code, ex.Code);
            Assert.IsFalse(flow.CanResume);
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "bundles")) && Directory.GetDirectories(Path.Combine(_dir, "bundles")).Length > 0, "不留本機暫存");
            Assert.IsEmpty(_server.Deleted);
        }

        [Test]
        public async Task FinalizeFailedBundleExpiredAndGoneAreAllTreatedAsUnusable()
        {
            foreach (var script in new Func<HttpResponseMessage>[]
            {
                () => Status(500, "finalize_failed"), () => Status(410, "bundle_expired"), () => Status(409, "bundle_failed"),
                () => Status(409, "bundle_closed"), () => Status(404, "not_found"), () => Status(413, "payload_too_large")
            })
            {
                _server.FinalizeScript.Enqueue(r => script());
                var flow = await NewFlow(new FakeSteps());
                Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
                // 413 是內容被拒，不可重試；其他都可以用本機檔案重建
                var retryable = flow.CanResume;
                if (retryable)
                {
                    var created = _server.CreatedBundleIds.Count;
                    await flow.ResumeAsync(null, CancellationToken.None);
                    Assert.AreEqual(created + 1, _server.CreatedBundleIds.Count);
                }
                _server.FinalizeScript.Clear();
            }
        }

        [Test]
        public async Task ABundleThatExpiredOnTheServerWhileWaitingForRetryIsRecreatedAutomatically()
        {
            var steps = new FakeSteps();
            var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
            var flow = await NewFlow(steps, () => now);
            _server.FailNextPuts = 100;
            Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            Assert.IsTrue(flow.CanResume);

            _server.FailNextPuts = 0;
            now = _server.BundleExpiresAt.AddHours(1); // 超過到期時間
            var result = await flow.ResumeAsync(null, CancellationToken.None);

            Assert.AreEqual(2, _server.CreatedBundleIds.Count, "過期＝重新建立，不再叫使用者重新按上傳");
            Assert.AreEqual(_server.CreatedBundleIds[1], result.BundleId);
            Assert.AreEqual(1, steps.Calls.Count(c => c == "glb"));
        }

        // ── #8 finalize 逾時與「伺服器處理中」──────────────────────────

        [Test]
        public async Task FinalizeUsesTheDedicatedLongTimeoutClient()
        {
            Assert.GreaterOrEqual(AddinApiClient.FinalizeTimeout, TimeSpan.FromSeconds(330), "伺服器 finalize 最長約 300 秒");
            var login = await Harness.LoggedInAsync(_server);
            var jsonRecorder = new RecordingHandler(_server.Handler);
            var finalizeRecorder = new RecordingHandler(_server.Handler);
            var api = new AddinApiClient(new HttpClient(jsonRecorder), login.Item1, ClientInfo.Create("PC", "1.0.0.0"), null, new HttpClient(finalizeRecorder));
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var pdf = Harness.Bytes(10);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", pdf));
            bundle.Manifest["sheets"] = new JArray(new JObject { ["pageNumber"] = 1 });
            await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None);
            await api.FinalizeAsync(created.BundleId, bundle.Manifest, CancellationToken.None);
            await api.DeleteBundleAsync(created.BundleId, CancellationToken.None);

            Assert.IsTrue(finalizeRecorder.Paths.Single().EndsWith("/finalize"), "只有 finalize 走長逾時的 HttpClient：" + string.Join(";", finalizeRecorder.Paths));
            Assert.IsFalse(jsonRecorder.Paths.Any(p => p.EndsWith("/finalize")));
            Assert.IsTrue(jsonRecorder.Paths.Any(p => p.StartsWith("POST") && p.EndsWith("/bundles")), "建立走一般的 60 秒 client");
        }

        [Test]
        public async Task FinalizeInProgressIsPolledUntilTheServerIsDoneAndTheUserSeesTheProcessingMessage()
        {
            var delays = new List<TimeSpan>();
            var flow = await NewFlow(new FakeSteps(), delays: delays);
            _server.FinalizeScript.Enqueue(r => Status(409, "finalize_in_progress"));
            _server.FinalizeScript.Enqueue(r => Status(409, "finalize_in_progress"));
            var stages = new StageLog();

            var result = await flow.RunAsync(RequestFactory.Full(), null, stages, CancellationToken.None);

            Assert.IsNotNull(result.SetId);
            Assert.AreEqual(3, _server.FinalizeRequestBundleIds.Count, "兩次處理中＋最後成功");
            Assert.AreEqual(1, _server.CreatedBundleIds.Count, "輪詢期間不重建版本包");
            Assert.AreEqual(2, delays.Count);
            Assert.IsTrue(delays.All(d => d == flow.FinalizePollInterval));
            CollectionAssert.Contains(stages.Stages, Strings.FinalizeStage);
            Assert.IsTrue(stages.Stages.Any(s => s.Contains("伺服器處理中") && s.Contains("已等待")), string.Join("|", stages.Stages));
            Assert.IsEmpty(_server.Deleted);
        }

        [Test]
        public async Task ATimedOutFinalizeIsRetriedByAskingTheServerAgain()
        {
            var flow = await NewFlow(new FakeSteps());
            _server.FinalizeScript.Enqueue(r => { throw new TaskCanceledException("timeout"); });
            var stages = new StageLog();

            var result = await flow.RunAsync(RequestFactory.Full(), null, stages, CancellationToken.None);

            Assert.IsNotNull(result.SetId);
            Assert.AreEqual(2, _server.FinalizeRequestBundleIds.Count);
            Assert.AreEqual(1, _server.FinalizeManifests.Count, "伺服器端只真正處理一次");
            Assert.IsTrue(stages.Stages.Any(s => s.Contains("伺服器處理中")));
        }

        [TestCase(502)]
        [TestCase(504)]
        public async Task GatewayTimeoutsAreTreatedLikeATimeout(int status)
        {
            var flow = await NewFlow(new FakeSteps());
            _server.FinalizeScript.Enqueue(r => Status(status, null));
            var result = await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None);
            Assert.IsNotNull(result.SetId);
            Assert.AreEqual(2, _server.FinalizeRequestBundleIds.Count);
        }

        [Test]
        public async Task AConnectionFailureDuringFinalizeIsNotPolledAndKeepsTheBundleForRetry()
        {
            var flow = await NewFlow(new FakeSteps());
            _server.FinalizeScript.Enqueue(r => { throw new HttpRequestException("socket closed"); });
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Network, ex.Kind);
            Assert.AreEqual(1, _server.FinalizeRequestBundleIds.Count);
            Assert.IsTrue(flow.CanResume);

            var result = await flow.ResumeAsync(null, CancellationToken.None);
            Assert.AreEqual(1, _server.CreatedBundleIds.Count, "連線中斷不代表版本包作廢：續傳同一個，finalize 冪等");
            Assert.AreEqual(_server.CreatedBundleIds[0], result.BundleId);
        }

        [Test]
        public async Task PollingStopsAfterTheMaximumWaitAndTheBundleCanStillBeResumed()
        {
            var delays = new List<TimeSpan>();
            var flow = await NewFlow(new FakeSteps(), delays: delays, now: () => new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
            flow.FinalizePollInterval = TimeSpan.FromSeconds(30);
            flow.FinalizeMaxWait = TimeSpan.FromMinutes(10);
            for (var i = 0; i < 100; i++) _server.FinalizeScript.Enqueue(r => Status(409, "finalize_in_progress"));

            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));

            Assert.AreEqual("finalize_in_progress", ex.Code);
            StringAssert.Contains("10 分鐘", ex.Message);
            Assert.AreEqual(20, delays.Count, "10 分鐘 ÷ 30 秒");
            Assert.AreEqual(21, _server.FinalizeRequestBundleIds.Count);
            Assert.IsTrue(flow.CanResume, "逾時不代表失敗：伺服器可能稍後完成");

            _server.FinalizeScript.Clear();
            var result = await flow.ResumeAsync(null, CancellationToken.None);
            Assert.IsNotNull(result.SetId);
            Assert.AreEqual(1, _server.CreatedBundleIds.Count);
        }

        [Test]
        public async Task CancelWhilePollingIsAllowed()
        {
            var flow = await NewFlow(new FakeSteps(), delays: null);
            using (var cts = new CancellationTokenSource())
            {
                for (var i = 0; i < 5; i++) _server.FinalizeScript.Enqueue(r => { cts.Cancel(); return Status(409, "finalize_in_progress"); });
                Assert.CatchAsync<OperationCanceledException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, cts.Token));
            }
            Assert.IsFalse(flow.CanResume);
        }

        // ── #10 分段大小以回應為準 ────────────────────────────────

        [Test]
        public async Task PartSizeComesFromTheServerResponse()
        {
            _server.PartBytes = 8L * 1024 * 1024;
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var data = Harness.Bytes(10 * 1024 * 1024);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", data));
            await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 8 * 1024 * 1024, 2 * 1024 * 1024 }, _server.Puts.Select(p => p.BodyBytes.Length).ToArray());
            CollectionAssert.AreEqual(data, _server.StoredFile(created.BundleId, "pdf"));

            _server.PartBytes = 129L * 1024 * 1024;
            var big = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await new BundleUploader(api, 1, NoDelay).UploadAsync(big, bundle, null, CancellationToken.None));
            StringAssert.Contains("分段大小", ex.Message);
        }

        [Test]
        public async Task FilesOverTheServerLimitsAreRejectedBeforeUploadingAndTheBundleIsCleanedUp()
        {
            _server.Limits["glbBytes"] = 5;
            var flow = await NewFlow(new FakeSteps());
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            StringAssert.Contains("3D 模型", ex.Message);
            StringAssert.Contains("上限", ex.Message);
            Assert.AreEqual(0, _server.Puts.Count);
            Assert.AreEqual(1, _server.Deleted.Count);
            Assert.IsFalse(flow.CanResume);
        }

        [Test]
        public async Task MoreSheetsThanTheServerPageLimitAreRejectedBeforeUploading()
        {
            _server.Limits["pdfPages"] = 1;
            var flow = await NewFlow(new FakeSteps());
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            StringAssert.Contains("上限", ex.Message);
            Assert.AreEqual(0, _server.Puts.Count);
        }

        // ── #11 上傳網址的主機 ───────────────────────────────────

        [TestCase("localhost:3000")]
        [TestCase("127.0.0.1:3000")]
        public async Task ALocalhostUploadUrlFromARemoteServerIsRefusedWithoutSendingAnything(string host)
        {
            _server.UploadScheme = "http";
            _server.UploadHost = host;
            var flow = await NewFlow(new FakeSteps());
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            StringAssert.Contains("ADDIN_PUBLIC_URL", ex.Message);
            StringAssert.Contains("不是 Issue Manager 的主機", ex.Message);
            Assert.AreEqual(0, _server.Puts.Count, "一個位元組都沒有送到本機");
            Assert.IsFalse(_server.Handler.Requests.Any(r => r.Uri.Host == "localhost" || r.Uri.Host == "127.0.0.1"));
        }

        [Test]
        public async Task BearerIsStillOnlySentToTheIssueManagerHost()
        {
            var flow = await NewFlow(new FakeSteps());
            await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None);
            Assert.IsTrue(_server.Puts.All(p => p.Authorization != null), "同主機：帶權杖");

            var other = new FakeTokenServer { PartBytes = 16, UploadHost = "storage.example.test" };
            var flow2 = new UploadFlow(await Harness.LoggedInApiAsync(other), new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "b2"));
            await flow2.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None);
            Assert.IsTrue(other.Puts.All(p => p.Authorization == null), "別的主機：不帶權杖");
        }

        // ── #7 manifest 與網站端 schema ───────────────────────────

        private static MiniJsonSchema ClientSchema()
        {
            return new MiniJsonSchema(JObject.Parse(File.ReadAllText(Path.Combine(TempDir.RepoRoot(), "docs", "schemas", "bundle-manifest.schema.json"))));
        }

        [Test]
        public void ClientSchemaDiffersFromTheWebsiteAuthoritativeCopyOnlyInIdAndDescription()
        {
            var root = TempDir.RepoRoot();
            var client = JObject.Parse(File.ReadAllText(Path.Combine(root, "docs", "schemas", "bundle-manifest.schema.json")));
            var web = JObject.Parse(File.ReadAllText(Path.Combine(root, "docs", "schemas", "web-authoritative", "bundle-manifest.schema.json")));
            var differences = DiffPaths(client, web, "").OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { "$id", "description" }, differences, "已知差異只有 $id 與 description；其餘必須為空");
        }

        private static IEnumerable<string> DiffPaths(JToken a, JToken b, string at)
        {
            if (JToken.DeepEquals(a, b)) yield break;
            var oa = a as JObject;
            var ob = b as JObject;
            if (oa == null || ob == null) { yield return at; yield break; }
            foreach (var name in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
            {
                foreach (var path in DiffPaths(oa[name], ob[name], at.Length == 0 ? name : at + "." + name)) yield return path;
            }
        }

        private static ExportRequest ManySheets(int count)
        {
            var request = new ExportRequest { ProjectId = "p", Stage = "SD", Description = "x", IncludeModel = false };
            for (var i = 1; i <= count; i++) request.Sheets.Add(Samples.Sheet("sheet-" + i, "S" + i, "圖 " + i));
            return request;
        }

        private static JObject Build(ExportRequest request, IEnumerable<string> warnings = null, double[] origin = null)
        {
            return ManifestBuilder.Build(request, new RevitDocInfo { Title = "AR", VersionNumber = "2024" }, DateTimeOffset.UtcNow, origin ?? new double[] { 0, 0, 0 }, new BundleFile[0], warnings);
        }

        [Test]
        public void ManifestWarningsAreCappedAt200EntriesOf500CharactersAndStillPassTheWebsiteSchema()
        {
            var warnings = Enumerable.Range(0, 450).Select(i => "警告 " + i + " " + new string('字', 600)).ToList();
            var manifest = Build(ManySheets(1), warnings);
            var list = ((JArray)manifest["warnings"]).Select(t => (string)t).ToList();
            Assert.AreEqual(200, list.Count);
            Assert.IsTrue(list.All(w => w.Length <= 500));
            StringAssert.Contains("另有 251 則警告已省略", list.Last());
            StringAssert.StartsWith("警告 0", list.First());
            Assert.IsEmpty(ClientSchema().Validate(manifest));

            var exactly = Build(ManySheets(1), Enumerable.Range(0, 200).Select(i => "w" + i));
            Assert.AreEqual(200, ((JArray)exactly["warnings"]).Count);
            StringAssert.DoesNotContain("省略", string.Join("|", ((JArray)exactly["warnings"]).Select(t => (string)t)));
        }

        [Test]
        public void LongNamesAreClippedToTheWebsiteLimitsInsteadOfFailingTheWholeUpload()
        {
            var request = RequestFactory.Full();
            request.Sheets[0].Title = new string('標', 700);
            request.Views[0].Name = new string('視', 400);
            request.SheetSetName = new string('集', 400);
            var manifest = ManifestBuilder.Build(request, new RevitDocInfo { Title = new string('專', 400), VersionNumber = new string('v', 150) },
                DateTimeOffset.UtcNow, new double[] { 0, 0, 0 }, new BundleFile[0], null);
            Assert.IsEmpty(ClientSchema().Validate(manifest));
            Assert.AreEqual(500, ((string)manifest["sheets"][0]["title"]).Length);
            Assert.AreEqual(300, ((string)manifest["views"][0]["name"]).Length);
            Assert.AreEqual(300, ((string)manifest["sheetSet"]["name"]).Length);
            Assert.AreEqual(300, ((string)manifest["revitDoc"]["title"]).Length);
            Assert.AreEqual(100, ((string)manifest["revitDoc"]["versionNumber"]).Length);
        }

        [Test]
        public void ThingsThatCannotBeClippedFailWithAPlainMessageBeforeAnythingIsUploaded()
        {
            var tooMany = Assert.Throws<InvalidOperationException>(() => Build(ManySheets(1001)));
            StringAssert.Contains("1000", tooMany.Message);
            Assert.IsEmpty(ClientSchema().Validate(Build(ManySheets(1000))), "剛好 1,000 張可以");

            var longNumber = ManySheets(1);
            longNumber.Sheets[0].Number = new string('N', 101);
            StringAssert.Contains("圖紙號", Assert.Throws<InvalidOperationException>(() => Build(longNumber)).Message);

            var far = Assert.Throws<InvalidOperationException>(() => Build(ManySheets(1), null, new double[] { 2e9, 0, 0 }));
            StringAssert.Contains("原點", far.Message);
        }

        // ── #6 只勾視圖 ──────────────────────────────────────────

        [Test]
        public void ViewsAloneCannotBeUploadedBecauseTheServerRejectsAPackageWithoutSheetsOrModel()
        {
            var vm = new ExportSelectionViewModel(Samples.Snapshot());
            vm.IsLoggedIn = true;
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "C", Name = "N" } });
            vm.SelectedProject = vm.Projects[0];
            vm.SelectedStage = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "x";
            vm.IncludeModel = false;
            foreach (var group in vm.ViewGroups) foreach (var view in group.Views) view.IsChecked = true;

            Assert.Greater(vm.CheckedViewCount, 0);
            Assert.AreEqual(0, vm.CheckedSheetCount);
            Assert.IsFalse(vm.HasAnySelection);
            Assert.IsFalse(vm.CanUpload);
            Assert.AreEqual(Strings.NothingSelected, vm.BlockReason);
            StringAssert.Contains("圖紙", vm.BlockReason);

            vm.IncludeModel = true;
            Assert.IsTrue(vm.CanUpload, "有模型就行（視圖是模型的附屬資料）");
            vm.IncludeModel = false;
            vm.Sheets.First().IsChecked = true;
            Assert.IsTrue(vm.CanUpload, "有圖紙就行");
        }

        [Test]
        public void MoreThanTheServerSheetLimitBlocksTheUploadButtonWithAReason()
        {
            var snapshot = new ProjectSnapshot { DocumentTitle = "big" };
            for (var i = 1; i <= 1001; i++) snapshot.Sheets.Add(Samples.Sheet("s" + i, "S" + i, "t"));
            var vm = new ExportSelectionViewModel(snapshot);
            vm.IsLoggedIn = true;
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "C", Name = "N" } });
            vm.SelectedProject = vm.Projects[0];
            vm.SelectedStage = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "x";
            vm.SelectAllSheets(true);
            Assert.IsFalse(vm.CanUpload);
            StringAssert.Contains("1000", vm.BlockReason);
        }
    }
}
