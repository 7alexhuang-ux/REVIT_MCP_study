using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>不需要 Revit 的匯出步驟：寫出最小但格式正確的假檔案，並記錄呼叫順序。</summary>
    public sealed class FakeSteps : IBundleExportSteps
    {
        public List<string> Calls = new List<string>();
        public Action<string> OnCall = name => { };
        public double[] Origin = { 12.5, 0, -3.25 };
        /// <summary>null＝依傳入的圖紙數產生同頁數的假 PDF（頁數檢查要求頁數＝圖紙數）；要測壞檔或頁數不符時才明確指定。</summary>
        public byte[] PdfBytes;

        public static byte[] PdfWithPages(int pages)
        {
            var text = new StringBuilder("%PDF-1.7\n");
            for (var i = 0; i < pages; i++) text.Append((i + 1) + " 0 obj << /Type /Page >> endobj\n");
            return Encoding.ASCII.GetBytes(text.ToString());
        }
        public byte[] GlbBytes = Encoding.ASCII.GetBytes("glTF-fake-glb-bytes-0123456789");
        public Exception ThrowInModel;
        public IReadOnlyList<SheetItem> LastPdfSheets;
        public IReadOnlyList<string> LastNamedViewIds;
        public double[] LastNamedViewOrigin;
        public bool LastIncludeDesignOptions;

        public RevitDocInfo GetDocInfo() { Calls.Add("doc"); OnCall("doc"); return new RevitDocInfo { Title = "AR_CPC", VersionNumber = "2024 (test)" }; }

        public void ExportPdf(IReadOnlyList<SheetItem> sheetsInOrder, string outputPath)
        {
            Calls.Add("pdf"); LastPdfSheets = sheetsInOrder; File.WriteAllBytes(outputPath, PdfBytes ?? PdfWithPages(sheetsInOrder.Count)); OnCall("pdf");
        }

        public ModelExportResult ExportModel(bool includeDesignOptions, string workDirectory, string outputGlbPath)
        {
            Calls.Add("glb"); LastIncludeDesignOptions = includeDesignOptions;
            if (ThrowInModel != null) throw ThrowInModel;
            File.WriteAllBytes(outputGlbPath, GlbBytes); OnCall("glb");
            return new ModelExportResult { OriginOffsetMeters = Origin, Warnings = { "2 個材質的貼圖檔找不到，已退回材質顏色。" } };
        }

        public void ExportNamedViews(IReadOnlyList<string> viewUniqueIds, double[] originOffsetMeters, string workDirectory, string outputPath)
        {
            Calls.Add("namedViews"); LastNamedViewIds = viewUniqueIds; LastNamedViewOrigin = originOffsetMeters;
            File.WriteAllText(outputPath, new JObject { ["originOffsetMeters"] = new JArray(originOffsetMeters), ["views"] = new JArray(), ["warnings"] = new JArray() }.ToString());
            OnCall("namedViews");
        }
    }

    public sealed class InlineRunner : IApiThreadRunner
    {
        public int Calls;

        public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
        {
            Calls++;
            if (ct.IsCancellationRequested) { var cancelled = new TaskCompletionSource<T>(); cancelled.SetCanceled(); return cancelled.Task; }
            try { return Task.FromResult(work()); }
            catch (OperationCanceledException) { var tcs = new TaskCompletionSource<T>(); tcs.SetCanceled(); return tcs.Task; }
            catch (Exception ex) { var tcs = new TaskCompletionSource<T>(); tcs.SetException(ex); return tcs.Task; }
        }
    }

    public sealed class RecordingExportProgress : IExportProgress
    {
        public List<string> Steps = new List<string>();
        public void Report(string text, int step, int totalSteps) { Steps.Add(step + "/" + totalSteps + " " + text); }
    }

    public static class RequestFactory
    {
        /// <summary>A-2 與 A-10 兩張圖紙、3D 視圖與平面視圖各一、含模型。</summary>
        public static ExportRequest Full()
        {
            return new ExportRequest
            {
                ProjectId = "p1", Stage = "SD", Description = "測試版本", IncludeModel = true, IncludeDesignOptions = true,
                Sheets =
                {
                    Samples.Sheet("sheet-a2", "A-2", "二層平面圖", Samples.Viewport("v-plan", 4, 3)),
                    Samples.Sheet("sheet-a10", "A-10", "透視", Samples.Viewport("v-3d", 3, 2))
                },
                Views = { Samples.View("v-3d", "主透視", ViewKinds.ThreeD), Samples.View("v-plan", "2F", ViewKinds.Plan) }
            };
        }
    }

    [TestFixture]
    public class BundleExporterTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("exporter"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        [Test]
        public void ExportsAllPartsInOrderWithSha256AndManifest()
        {
            var steps = new FakeSteps();
            var progress = new RecordingExportProgress();
            var bundle = new BundleExporter(() => new DateTimeOffset(2026, 10, 25, 1, 2, 3, TimeSpan.Zero))
                .Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b"), progress, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb", "namedViews" }, steps.Calls);
            CollectionAssert.AreEqual(new[] { "A-2", "A-10" }, steps.LastPdfSheets.Select(s => s.Number).ToArray());
            CollectionAssert.AreEqual(new[] { "v-3d" }, steps.LastNamedViewIds.ToArray());
            CollectionAssert.AreEqual(steps.Origin, steps.LastNamedViewOrigin, "命名視圖必須用 GLB 的同一組原點");
            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews" }, bundle.Files.Select(f => f.Kind).ToArray());

            foreach (var file in bundle.Files)
            {
                Assert.AreEqual(new FileInfo(file.Path).Length, file.Bytes);
                Assert.AreEqual(BundleExporter.Sha256Hex(file.Path), file.Sha256);
                Assert.That(file.Sha256, Does.Match("^[0-9a-f]{64}$"));
            }
            var manifest = Jsons.Parse(File.ReadAllText(bundle.ManifestPath));
            Assert.AreEqual("2026-10-25T01:02:03Z", (string)manifest["exportedAt"]);
            Assert.AreEqual(12.5, (double)manifest["originOffsetMeters"][0]);
            Assert.AreEqual(bundle.Files.First(f => f.Kind == "pdf").Sha256, (string)manifest["files"]["pdf"]["sha256"]);
            CollectionAssert.Contains(manifest["warnings"].Select(w => (string)w).ToList(), "2 個材質的貼圖檔找不到，已退回材質顏色。");
            Assert.AreEqual(5, progress.Steps.Count);
            StringAssert.StartsWith("1/5", progress.Steps[0]);

        }

        [Test]
        public void IntermediateWorkFolderIsRemovedAfterTheManifestIsWritten()
        {
            var dir = Path.Combine(_dir, "b");
            var steps = new FakeSteps();
            steps.OnCall = name => { if (name == "glb") File.WriteAllText(Path.Combine(dir, "work", "part.glb"), "intermediate"); };
            var bundle = new BundleExporter().Export(RequestFactory.Full(), steps, dir, null, CancellationToken.None);
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "work")));
            Assert.IsTrue(File.Exists(bundle.ManifestPath));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "model.glb")));
        }

        [Test]
        public void SkipsPartsThatWereNotSelectedAndLeavesThemOutOfTheManifest()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.Sheets.Clear();
            request.IncludeModel = false;
            var bundle = new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc" }, steps.Calls);
            Assert.IsEmpty(bundle.Files);
            Assert.IsEmpty(((JObject)bundle.Manifest["files"]).Properties());
            var warnings = bundle.Manifest["warnings"].Select(w => (string)w).ToList();
            Assert.IsTrue(warnings.Any(w => w.Contains("略過 3D 視角")), "勾了 3D 視圖卻沒匯出模型要提醒");
        }

        [Test]
        public void ModelWithoutThreeDViewsDoesNotExportNamedViews()
        {
            var steps = new FakeSteps();
            var request = RequestFactory.Full();
            request.Views.RemoveAll(v => v.Kind == ViewKinds.ThreeD);
            new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb" }, steps.Calls);
        }

        [Test]
        public void X05_CancelBetweenStepsStopsBeforeTheNextStep()
        {
            var steps = new FakeSteps();
            using (var cts = new CancellationTokenSource())
            {
                steps.OnCall = name => { if (name == "pdf") cts.Cancel(); };
                Assert.Catch<OperationCanceledException>(() =>
                    new BundleExporter().Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b"), null, cts.Token));
            }
            CollectionAssert.AreEqual(new[] { "doc", "pdf" }, steps.Calls, "PDF 之後就停，不得進入 GLB");
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "b", "manifest.json")));
        }

        [Test]
        public void CancelDuringModelExportIsHonouredRightAfterIt()
        {
            var steps = new FakeSteps();
            using (var cts = new CancellationTokenSource())
            {
                steps.OnCall = name => { if (name == "glb") cts.Cancel(); };
                Assert.Catch<OperationCanceledException>(() =>
                    new BundleExporter().Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b"), null, cts.Token));
            }
            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb" }, steps.Calls);
        }

        [Test]
        public void InvalidOutputFilesAreRejectedWithPlainMessages()
        {
            var steps = new FakeSteps { PdfBytes = Encoding.ASCII.GetBytes("<html>not a pdf</html>") };
            var ex = Assert.Throws<InvalidOperationException>(() => new BundleExporter().Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b1"), null, CancellationToken.None));
            StringAssert.Contains("PDF", ex.Message);

            var steps2 = new FakeSteps { GlbBytes = Encoding.ASCII.GetBytes("nope-not-glb") };
            var ex2 = Assert.Throws<InvalidOperationException>(() => new BundleExporter().Export(RequestFactory.Full(), steps2, Path.Combine(_dir, "b2"), null, CancellationToken.None));
            StringAssert.Contains("GLB", ex2.Message);
        }

        [Test]
        public void PdfPageCountMismatchAbortsTheExportInsteadOfJustWarning()
        {
            var steps = new FakeSteps { PdfBytes = FakeSteps.PdfWithPages(2) }; // 假 PDF 有 2 頁（沒有物件串流＝頁數可信）
            var request = RequestFactory.Full();
            request.Sheets.Add(Samples.Sheet("sheet-a11", "A-11", "第三張"));
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleExporter().Export(request, steps, Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("PDF 頁數（2）與勾選的圖紙數（3）不同", ex.Message);
            StringAssert.Contains("已中止", ex.Message);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "b", "manifest.json")), "中止後不產生 manifest");
        }

        [Test]
        public void ModelFailurePropagatesTheStepMessage()
        {
            var steps = new FakeSteps { ThrowInModel = new InvalidOperationException("3D 模型匯出不完整") };
            var ex = Assert.Throws<InvalidOperationException>(() => new BundleExporter().Export(RequestFactory.Full(), steps, Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("不完整", ex.Message);
        }

        [Test]
        public void PdfInspectorCountsPageObjectsButNotPagesTree()
        {
            var path = Path.Combine(_dir, "t.pdf");
            File.WriteAllText(path, "%PDF-1.4\n<< /Type /Pages /Count 2 >>\n<< /Type /Page >>\n<< /Type/Page /Parent 1 0 R >>\n");
            Assert.AreEqual(2, PdfInspector.TryCountPages(path));
            File.WriteAllText(path, "%PDF-1.5 compressed object streams only");
            Assert.IsNull(PdfInspector.TryCountPages(path));
        }
    }

    internal static class Harness
    {
        public static async Task<AddinApiClient> LoggedInApiAsync(FakeTokenServer server, Func<DateTimeOffset> now = null)
        {
            return (await LoggedInAsync(server, now)).Item2;
        }

        public static async Task<Tuple<AddinAuthService, AddinApiClient>> LoggedInAsync(FakeTokenServer server, Func<DateTimeOffset> now = null)
        {
            var http = server.CreateClient();
            var auth = new AddinAuthService(http, new InMemoryVault(), FakeTokenServer.BaseUrl, ClientInfo.Create("PC", "1.0.0.0"), now);
            using (var callback = new HttpClient())
            {
                var browser = new ActionBrowser(url =>
                {
                    var query = FakeTokenServer.ParseQuery(new Uri(url).Query);
                    var code = server.ApproveFromAuthorizeUrl(url);
                    Task.Run(async () => { try { await callback.GetAsync("http://127.0.0.1:" + query["port"] + "/callback?code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(query["state"])); } catch (Exception) { } });
                });
                await auth.LoginAsync(browser, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            return Tuple.Create(auth, new AddinApiClient(http, auth, ClientInfo.Create("PC", "1.0.0.0")));
        }

        public static ExportedBundle MakeBundle(string dir, params KeyValuePair<string, byte[]>[] files)
        {
            Directory.CreateDirectory(dir);
            var bundle = new ExportedBundle { Directory = dir, Manifest = new JObject { ["schemaVersion"] = 1, ["files"] = new JObject() } };
            foreach (var pair in files)
            {
                var path = Path.Combine(dir, pair.Key + ".bin");
                File.WriteAllBytes(path, pair.Value);
                var file = BundleExporter.Describe(pair.Key, pair.Key + ".bin", path);
                bundle.Files.Add(file);
                ((JObject)bundle.Manifest["files"])[pair.Key] = new JObject { ["name"] = file.Name, ["bytes"] = file.Bytes, ["sha256"] = file.Sha256 };
            }
            return bundle;
        }

        public static byte[] Bytes(int count, byte seed = 1)
        {
            var data = new byte[count];
            for (var i = 0; i < count; i++) data[i] = (byte)(seed + i);
            return data;
        }
    }

    [TestFixture]
    public class BundleUploaderTests
    {
        private string _dir;
        private FakeTokenServer _server;
        [SetUp] public void SetUp() { _dir = TempDir.Create("uploader"); _server = new FakeTokenServer { PartBytes = 10 }; }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static Func<TimeSpan, CancellationToken, Task> NoDelay { get { return (span, token) => Task.FromResult(0); } }

        private async Task<BundleCreateResult> Create(AddinApiClient api)
        {
            return await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
        }

        [Test]
        public async Task UploadsEveryFileInPartsAndServerSeesIdenticalBytes()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var pdf = Harness.Bytes(35, 3);
            var glb = Harness.Bytes(10, 9); // 剛好一段
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", pdf), new KeyValuePair<string, byte[]>("glb", glb));
            await new BundleUploader(api, 3, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None);

            Assert.AreEqual(4 + 1, _server.Puts.Count, "35 bytes / 10 = 4 段，glb 1 段");
            CollectionAssert.AreEqual(pdf, _server.StoredFile(created.BundleId, "pdf"));
            CollectionAssert.AreEqual(glb, _server.StoredFile(created.BundleId, "glb"));
            Assert.AreEqual(new[] { 10, 10, 10, 5 }, _server.Puts.Where(p => p.Uri.AbsolutePath.Contains("/pdf/")).Select(p => p.BodyBytes.Length).ToArray());
        }

        [Test]
        public async Task X07_RetryDoesNotReuploadCompletedFilesButContinuesPartialOnes()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"),
                new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(25)), new KeyValuePair<string, byte[]>("glb", Harness.Bytes(30, 7)));
            // 第 4 次 PUT 起連續失敗：pdf 3 段完成，glb 第 1 段完成後失敗
            _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.StartsWith("/upload/") && _server.Puts.Count == 4) _server.FailNextPuts = 100; return Task.FromResult(0); };
            var uploader = new BundleUploader(api, 2, NoDelay);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await uploader.UploadAsync(created, bundle, null, CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Server, ex.Kind);
            var putsAfterFailure = _server.Puts.Count;

            _server.FailNextPuts = 0;
            _server.Handler.BeforeRespond = null;
            var statePath = BundleUploader.StatePath(bundle);
            Assert.IsTrue(File.Exists(statePath));
            StringAssert.DoesNotContain("imat_", File.ReadAllText(statePath));

            var before = _server.Puts.Count;
            await uploader.UploadAsync(created, bundle, null, CancellationToken.None);
            var newPuts = _server.Puts.Skip(before).Select(p => p.Uri.AbsolutePath).ToList();
            Assert.IsFalse(newPuts.Any(p => p.Contains("/pdf/")), "已完成且 sha256 相符的 pdf 不得重傳");
            Assert.IsTrue(newPuts.All(p => p.Contains("/glb/")));
            CollectionAssert.AreEqual(Harness.Bytes(30, 7), _server.StoredFile(created.BundleId, "glb"));
            Assert.Greater(putsAfterFailure, 0);

            // 第三次：全部完成，完全不送
            var beforeThird = _server.Puts.Count;
            await uploader.UploadAsync(created, bundle, null, CancellationToken.None);
            Assert.AreEqual(beforeThird, _server.Puts.Count);
        }

        [Test]
        public async Task X07_ChangedFileIsUploadedAgainEvenIfStateSaysDone()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var dir = Path.Combine(_dir, "b");
            var bundle = Harness.MakeBundle(dir, new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(15)));
            var uploader = new BundleUploader(api, 2, NoDelay);
            await uploader.UploadAsync(created, bundle, null, CancellationToken.None);
            var puts = _server.Puts.Count;

            var changed = Harness.MakeBundle(dir, new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(15, 99)));
            await uploader.UploadAsync(created, changed, null, CancellationToken.None);
            Assert.AreEqual(puts * 2, _server.Puts.Count);
        }

        [Test]
        public async Task TransientFailuresAreRetriedThenSucceed()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(10)));
            _server.FailNextPuts = 2;
            var delays = new List<TimeSpan>();
            await new BundleUploader(api, 3, (span, token) => { delays.Add(span); return Task.FromResult(0); })
                .UploadAsync(created, bundle, null, CancellationToken.None);
            Assert.AreEqual(3, _server.Puts.Count);
            Assert.AreEqual(2, delays.Count);
            Assert.Less(delays[0], delays[1], "退避時間遞增");
        }

        [Test]
        public async Task ClientErrorsAreNotRetried()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(10)));
            _server.FailNextPuts = 5;
            _server.FailPutStatus = HttpStatusCode.Forbidden;
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await new BundleUploader(api, 3, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NotFound, ex.Kind);
            Assert.AreEqual(1, _server.Puts.Count);
        }

        [Test]
        public async Task MissingTicketOrTooFewUrlsFailWithPlainMessage()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(100)));
            created.Files.Find(f => f.Kind == "pdf").Urls.RemoveRange(3, created.Files.Find(f => f.Kind == "pdf").Urls.Count - 3);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None));
            StringAssert.Contains("上傳網址不足", ex.Message);

            created.Files.RemoveAll(f => f.Kind == "pdf");
            var ex2 = Assert.ThrowsAsync<AddinApiException>(async () => await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None));
            StringAssert.Contains("上傳憑證", ex2.Message);
        }

        [Test]
        public async Task AbsurdPartSizeFromServerIsRejectedBeforeReadingAnything()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            created.Files.Find(f => f.Kind == "pdf").PartBytes = BundleUploader.MaxPartBytes + 1;
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(10)));
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None));
            StringAssert.Contains("分段大小", ex.Message);
            Assert.AreEqual(0, _server.Puts.Count);
        }

        [Test]
        public async Task BearerIsOnlySentToTheIssueManagerHost()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await Create(api);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(10)));
            await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None);
            Assert.IsNotNull(_server.Puts[0].Authorization, "同主機（本機開發）要帶 Bearer");

            _server.Puts.Clear();
            var other = new FakeTokenServer { PartBytes = 10, UploadHost = "storage.example.net" };
            var api2 = await Harness.LoggedInApiAsync(other);
            var created2 = await api2.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var bundle2 = Harness.MakeBundle(Path.Combine(_dir, "b2"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(10)));
            await new BundleUploader(api2, 1, NoDelay).UploadAsync(created2, bundle2, null, CancellationToken.None);
            Assert.IsNull(other.Puts[0].Authorization, "簽名網址在別的主機時不得附 Bearer");
            StringAssert.Contains("storage.example.net", other.Puts[0].Uri.Host);
        }

        [Test]
        public async Task InsecureSignedUrlsAreRefused()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.PutPartAsync("http://evil.example.test/upload/x", new byte[1], 0, 1, CancellationToken.None));
            StringAssert.Contains("不安全", ex.Message);
            Assert.AreEqual(0, _server.Puts.Count);
        }
    }

    [TestFixture]
    public class ApiClientTests
    {
        [Test]
        public async Task UnauthorizedTriggersOneRefreshThenRetries()
        {
            var server = new FakeTokenServer();
            var api = await Harness.LoggedInApiAsync(server);
            server.RevokeAllAccessTokens(); // access 被伺服器撤銷，但 refresh 家族也被標為撤銷 → 應該要求重新登入
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.GetProjectsAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NeedLogin, ex.Kind);
        }

        [Test]
        public async Task ProjectsAndMeAreParsed()
        {
            var server = new FakeTokenServer();
            var api = await Harness.LoggedInApiAsync(server);
            var projects = await api.GetProjectsAsync(CancellationToken.None);
            Assert.AreEqual(1, projects.Count);
            Assert.AreEqual("p1", projects[0].Id);
            Assert.AreEqual("CPC　測試專案", projects[0].Label);
            Assert.AreEqual("fake.user@example.test", (await api.GetMeAsync(CancellationToken.None)).Email);
        }

        [Test]
        public async Task V1CallsCarryApiVersionHeaderAndBearer()
        {
            var server = new FakeTokenServer();
            var api = await Harness.LoggedInApiAsync(server);
            await api.CreateBundleAsync("p1", "DD", "說明", new BundleSummary { Sheets = 2, Views = 1, Models = 1, Bytes = 0 }, CancellationToken.None);
            var request = server.Handler.Requests.Last(r => r.Uri.AbsolutePath == "/api/addin/v1/bundles");
            Assert.AreEqual("1", request.ApiVersion);
            StringAssert.StartsWith("Bearer imat_", request.Authorization);
            var body = request.Json;
            Assert.AreEqual("DD", (string)body["stage"]);
            Assert.AreEqual("p1", (string)body["projectId"]);
            Assert.AreEqual(2, (int)body["summary"]["sheets"]);
            Assert.AreEqual("1.0.0.0", (string)body["clientVersion"]);
        }

        [Test]
        public async Task ServerRejectionsBecomePlainMessages()
        {
            var server = new FakeTokenServer();
            var api = await Harness.LoggedInApiAsync(server);
            var stage = Assert.ThrowsAsync<AddinApiException>(async () => await api.CreateBundleAsync("p1", "", "x", new BundleSummary(), CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Rejected, stage.Kind);
            StringAssert.Contains("設計階段", stage.Message);
            var desc = Assert.ThrowsAsync<AddinApiException>(async () => await api.CreateBundleAsync("p1", "SD", "  ", new BundleSummary(), CancellationToken.None));
            StringAssert.Contains("版本說明", desc.Message);
        }

        [Test]
        public void Upgrade426IsReported()
        {
            var handler = new FakeHandler(r => r.Uri.AbsolutePath == "/api/addin/token"
                ? FakeHandler.Json(HttpStatusCode.OK, new JObject { ["access_token"] = "imat_x", ["refresh_token"] = "imrt_x", ["expires_in"] = 3600, ["refresh_expires_in"] = 99999 })
                : FakeHandler.Error((HttpStatusCode)426, "upgrade_required"));
            var http = new HttpClient(handler);
            var vault = new InMemoryVault { Stored = new StoredCredentials { BaseUrl = FakeTokenServer.BaseUrl, RefreshToken = "imrt_seed", RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1) } };
            var auth = new AddinAuthService(http, vault, FakeTokenServer.BaseUrl, ClientInfo.Create("PC", "1.0"));
            Assert.IsTrue(auth.TryRestore());
            var api = new AddinApiClient(http, auth, ClientInfo.Create("PC", "1.0"));
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.UpgradeRequired, ex.Kind);
            StringAssert.Contains("更新", ex.Message);
        }

        [Test]
        public async Task NetworkFailureBecomesNetworkError()
        {
            var server = new FakeTokenServer();
            var api = await Harness.LoggedInApiAsync(server);
            server.Handler.BeforeRespond = r => { throw new HttpRequestException("socket closed"); };
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await api.GetProjectsAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Network, ex.Kind);
            StringAssert.DoesNotContain("socket closed", ex.Message);
        }
    }

    [TestFixture]
    public class UploadFlowTests
    {
        private string _dir;
        private FakeTokenServer _server;
        [SetUp] public void SetUp() { _dir = TempDir.Create("flow"); _server = new FakeTokenServer { PartBytes = 16 }; }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private async Task<UploadFlow> NewFlow(FakeSteps steps)
        {
            var api = await Harness.LoggedInApiAsync(_server);
            return new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"),
                uploader: new BundleUploader(api, 2, (span, token) => Task.FromResult(0)));
        }

        private sealed class StageLog : IUploadFlowProgress
        {
            public List<string> Stages = new List<string>();
            public void Stage(string text) { Stages.Add(text); }
            public void Upload(UploadProgress progress) { }
        }

        [Test]
        public async Task HappyPathCreatesUploadsFinalizesAndCleansUp()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var stages = new StageLog();
            var result = await flow.RunAsync(RequestFactory.Full(), new RecordingExportProgress(), stages, CancellationToken.None);

            Assert.AreEqual("set-" + result.BundleId, result.SetId);
            Assert.AreEqual(1, _server.FinalizeManifests.Count, "finalize 恰好一次");
            Assert.AreEqual("SD", (string)_server.CreateRequests[0]["stage"]);
            Assert.AreEqual(2, (int)_server.CreateRequests[0]["summary"]["sheets"]);
            CollectionAssert.AreEqual(new[] { "pdf", "glb", "namedViews" }, ((JObject)_server.FinalizeManifests[0]["files"]).Properties().Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(steps.GlbBytes, _server.StoredFile(result.BundleId, "glb"));
            Assert.IsEmpty(_server.Deleted);
            CollectionAssert.Contains(result.Warnings, "測試警告");
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "bundles")) && Directory.GetDirectories(Path.Combine(_dir, "bundles")).Length > 0, "成功後本機暫存刪除");
            Assert.IsFalse(flow.CanResume);
            Assert.IsFalse(UploadFlow.IsRunning);
            Assert.AreEqual(3, stages.Stages.Count);
        }

        [Test]
        public async Task X05_CancelDuringExportDeletesServerBundleAndNeverUploads()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            using (var cts = new CancellationTokenSource())
            {
                steps.OnCall = name => { if (name == "pdf") cts.Cancel(); };
                Assert.CatchAsync<OperationCanceledException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, cts.Token));
            }
            CollectionAssert.AreEqual(new[] { "doc", "pdf" }, steps.Calls);
            Assert.AreEqual(0, _server.Puts.Count, "取消後不上傳");
            Assert.AreEqual(0, _server.FinalizeManifests.Count);
            Assert.AreEqual(1, _server.Deleted.Count, "取消後呼叫 DELETE /bundles/:id");
            Assert.AreEqual(_server.Handler.Requests.Last(r => r.Method == HttpMethod.Delete).Uri.Segments.Last(), _server.Deleted[0]);
            Assert.IsFalse(UploadFlow.IsRunning, "取消後可以再開始新的上傳");
        }

        [Test]
        public async Task X05_CancelDuringUploadDeletesBundleAndNeverFinalizes()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            using (var cts = new CancellationTokenSource())
            {
                _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.StartsWith("/upload/")) cts.Cancel(); return Task.FromResult(0); };
                Assert.CatchAsync<OperationCanceledException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, cts.Token));
            }
            Assert.AreEqual(0, _server.FinalizeManifests.Count);
            Assert.AreEqual(1, _server.Deleted.Count);
            Assert.IsFalse(flow.CanResume);
        }

        [Test]
        public async Task ExportFailureLeavesServerBundleToExpireAndNoLocalFiles()
        {
            var steps = new FakeSteps { ThrowInModel = new InvalidOperationException("3D 模型匯出不完整") };
            var flow = await NewFlow(steps);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            StringAssert.Contains("不完整", ex.Message);
            Assert.IsEmpty(_server.Deleted, "規格：失敗時伺服器端 bundle 保持 open 至過期自動清");
            Assert.AreEqual(0, _server.Puts.Count);
            Assert.IsFalse(flow.CanResume);
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "bundles")) && Directory.GetDirectories(Path.Combine(_dir, "bundles")).Length > 0);
        }

        [Test]
        public async Task UploadFailureCanBeResumedAndFinalizeIsIdempotent()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            _server.FailNextPuts = 100; // 重試 2 次仍失敗
            Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            Assert.IsTrue(flow.CanResume);
            Assert.IsFalse(UploadFlow.IsRunning);
            Assert.AreEqual(0, steps.Calls.Count(c => c == "glb") - 1, "匯出只做一次");

            _server.FailNextPuts = 0;
            var result = await flow.ResumeAsync(null, CancellationToken.None);
            Assert.AreEqual(1, steps.Calls.Count(c => c == "glb"), "重試不重新匯出");
            Assert.IsNotNull(result.SetId);
            Assert.AreEqual(1, _server.FinalizeManifests.Count);
            Assert.IsFalse(flow.CanResume);
        }

        [Test]
        public async Task FinalizeRejectionIsReportedAndResumable()
        {
            var flow = await NewFlow(new FakeSteps());
            _server.RejectFinalizeWithCode = "checksum_mismatch"; // 檔案在傳輸中壞掉：伺服器作廢整個版本包，用本機檔案重建後可以重試
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            StringAssert.Contains("清單", ex.Message);
            Assert.IsTrue(flow.CanResume);
            await flow.DiscardAsync();
            Assert.AreEqual(0, _server.Deleted.Count, "伺服器已作廢的版本包不必再 DELETE");
            Assert.IsFalse(flow.CanResume);
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "bundles")) && Directory.GetDirectories(Path.Combine(_dir, "bundles")).Length > 0, "放棄後本機檔案已刪");
        }

        [Test]
        public async Task OnlyOneUploadMayRunAtATime()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            steps.OnCall = name => { if (name == "doc") { entered.TrySetResult(true); release.Task.Wait(); } };
            var first = Task.Run(() => flow.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
            await entered.Task;
            Assert.IsTrue(UploadFlow.IsRunning);
            try
            {
                var flow2 = await NewFlow(new FakeSteps());
                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow2.RunAsync(RequestFactory.Full(), null, null, CancellationToken.None));
                StringAssert.Contains("正在進行", ex.Message);
            }
            finally
            {
                release.SetResult(true);
            }
            await first;
            Assert.IsFalse(UploadFlow.IsRunning);
        }

        [Test]
        public async Task ServerRejectsMissingStageBeforeAnyExport()
        {
            var steps = new FakeSteps();
            var flow = await NewFlow(steps);
            var request = RequestFactory.Full();
            request.Stage = null;
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await flow.RunAsync(request, null, null, CancellationToken.None));
            StringAssert.Contains("設計階段", ex.Message);
            Assert.IsEmpty(steps.Calls, "建立版本包失敗就不該進入長時間的匯出");
        }
    }
}
