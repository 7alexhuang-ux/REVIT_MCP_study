using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>審查後的加固項目：loopback 讀取逾時與獨佔位址、續傳前重新驗證磁碟檔案、PDF 頁數串流檢查與不符時中止。</summary>
    [TestFixture]
    public class LoopbackHardeningTests
    {
        [Test]
        public async Task AConnectionThatSendsNothingIsClosedAfterTheReadTimeoutAndDoesNotBlockTheRealCallback()
        {
            using (var listener = LoopbackCallbackListener.Start("state-stall-123", TimeSpan.FromMilliseconds(300)))
            using (var staller = new TcpClient())
            {
                await staller.ConnectAsync(IPAddress.Loopback, listener.Port);
                var stream = staller.GetStream();
                var buffer = new byte[16];
                // 什麼都不送：伺服器端必須在逾時後關閉連線（舊版 NetworkStream.ReadAsync 不理會取消，會永遠掛著）。
                var read = stream.ReadAsync(buffer, 0, buffer.Length);
                var finished = await Task.WhenAny(read, Task.Delay(3000));
                Assert.AreSame(read, finished, "逾時後連線應被關閉");
                int closedRead;
                try { closedRead = await read; } catch (Exception) { closedRead = 0; }
                Assert.AreEqual(0, closedRead);

                // 正確的回呼仍然收得到
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                using (var good = new TcpClient())
                {
                    await good.ConnectAsync(IPAddress.Loopback, listener.Port);
                    var good_stream = good.GetStream();
                    var request = Encoding.ASCII.GetBytes("GET /callback?code=abc&state=state-stall-123 HTTP/1.1\r\nHost: 127.0.0.1:" + listener.Port + "\r\n\r\n");
                    await good_stream.WriteAsync(request, 0, request.Length);
                    var result = await wait;
                    Assert.AreEqual("abc", result.Code);
                }
            }
        }

        [Test]
        public void ANoSendingClientIsNotHeldOpenWhileAnotherRequestIsServed()
        {
            using (var listener = LoopbackCallbackListener.Start("state-parallel-1", TimeSpan.FromMilliseconds(500)))
            using (var staller1 = new TcpClient())
            using (var staller2 = new TcpClient())
            {
                staller1.Connect(IPAddress.Loopback, listener.Port);
                staller2.Connect(IPAddress.Loopback, listener.Port);
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, listener.Port);
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes("GET /nope HTTP/1.1\r\nHost: 127.0.0.1:" + listener.Port + "\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    stream.ReadTimeout = 3000;
                    var buffer = new byte[256];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    StringAssert.StartsWith("HTTP/1.1 404", Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
        }

        [Test]
        public void TheListenerPortCannotBeStolenByAnotherSocketUsingReuseAddress()
        {
            using (var listener = LoopbackCallbackListener.Start("state-exclusive-1"))
            using (var thief = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                thief.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Assert.Throws<SocketException>(() => thief.Bind(new IPEndPoint(IPAddress.Loopback, listener.Port)),
                    "ExclusiveAddressUse：別的程式不能搶綁同一個埠來攔截授權碼");
            }
        }
    }

    [TestFixture]
    public class UploaderResumeVerificationTests
    {
        private string _dir;
        private FakeTokenServer _server;
        [SetUp] public void SetUp() { _dir = TempDir.Create("uploader-verify"); _server = new FakeTokenServer { PartBytes = 10 }; }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static Func<TimeSpan, CancellationToken, Task> NoDelay { get { return (span, token) => Task.FromResult(0); } }

        [Test]
        public async Task ResumingRecomputesTheDiskSha256AndRefusesAFileThatChangedSinceExport()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(35, 3)));
            // 第 3 次 PUT 起連續失敗 → 已寫入狀態檔（PartsDone=2）
            _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.StartsWith("/upload/") && _server.Puts.Count == 2) _server.FailNextPuts = 100; return Task.FromResult(0); };
            var uploader = new BundleUploader(api, 1, NoDelay);
            Assert.ThrowsAsync<AddinApiException>(async () => await uploader.UploadAsync(created, bundle, null, CancellationToken.None));

            // 檔案在兩次嘗試之間被改動（同樣長度，內容不同）：匯出時算的雜湊仍在記憶體裡，只有重新讀磁碟才抓得到。
            var path = bundle.Files[0].Path;
            var tampered = File.ReadAllBytes(path);
            tampered[20] ^= 0xFF;
            File.WriteAllBytes(path, tampered);
            _server.FailNextPuts = 0;
            _server.Handler.BeforeRespond = null;
            var before = _server.Puts.Count;

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await uploader.UploadAsync(created, bundle, null, CancellationToken.None));
            StringAssert.Contains("和匯出時不一樣", ex.Message);
            StringAssert.Contains("重新匯出", ex.Message);
            Assert.AreEqual(before, _server.Puts.Count, "發現檔案變了就不能再送任何分段");
        }

        [Test]
        public async Task ResumeWithAnUnchangedFileStillWorks()
        {
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var data = Harness.Bytes(35, 3);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", data));
            _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.StartsWith("/upload/") && _server.Puts.Count == 2) _server.FailNextPuts = 100; return Task.FromResult(0); };
            var uploader = new BundleUploader(api, 1, NoDelay);
            Assert.ThrowsAsync<AddinApiException>(async () => await uploader.UploadAsync(created, bundle, null, CancellationToken.None));
            _server.FailNextPuts = 0;
            _server.Handler.BeforeRespond = null;
            await uploader.UploadAsync(created, bundle, null, CancellationToken.None);
            CollectionAssert.AreEqual(data, _server.StoredFile(created.BundleId, "pdf"));
        }

        [Test]
        public async Task TheFirstAttemptDoesNotRehashTheFile()
        {
            // 第一次上傳不需要重算雜湊（避免對大 GLB 多花時間）：即使磁碟內容與記憶體中的描述不同，第一次也照送（finalize 由伺服器檢查）。
            var api = await Harness.LoggedInApiAsync(_server);
            var created = await api.CreateBundleAsync("p1", "SD", "x", new BundleSummary(), CancellationToken.None);
            var bundle = Harness.MakeBundle(Path.Combine(_dir, "b"), new KeyValuePair<string, byte[]>("pdf", Harness.Bytes(15, 3)));
            var tampered = File.ReadAllBytes(bundle.Files[0].Path);
            tampered[3] ^= 0xFF;
            File.WriteAllBytes(bundle.Files[0].Path, tampered);
            await new BundleUploader(api, 1, NoDelay).UploadAsync(created, bundle, null, CancellationToken.None);
            Assert.Greater(_server.Puts.Count, 0, "第一次嘗試不重算雜湊，照送");
        }
    }

    [TestFixture]
    public class PdfInspectionTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("pdf-inspect"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private string WritePdf(string name, string body)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, body, Encoding.GetEncoding("ISO-8859-1"));
            return path;
        }

        [Test]
        public void CountsPageObjectsAcrossChunkBoundariesWithoutDoubleCounting()
        {
            // 1 MB 區塊邊界附近放 3 個頁面物件（含一個剛好跨界），其餘是填充。
            var chunk = 1 << 20;
            var sb = new StringBuilder("%PDF-1.4\n");
            sb.Append('x', chunk - sb.Length - 6); // 讓 "/Type /Page" 的前半跨過第一個區塊邊界
            sb.Append("/Type /Page\n");
            sb.Append('y', 5000);
            sb.Append("/Type\r\n /Page \n");
            sb.Append('z', chunk * 2);
            sb.Append("<< /Type /Pages /Count 3 >>\n/Type/Page\n");
            var path = WritePdf("big.pdf", sb.ToString());
            Assert.Greater(new FileInfo(path).Length, chunk * 2);
            var inspection = PdfInspector.Inspect(path);
            Assert.AreEqual(3, inspection.PageObjects);
            Assert.IsTrue(inspection.CountIsReliable);
        }

        [Test]
        public void ObjectStreamsMakeTheCountUnreliable()
        {
            var inspection = PdfInspector.Inspect(WritePdf("o.pdf", "%PDF-1.5\n<< /Type /ObjStm /N 3 >>\n<< /Type /Page >>\n"));
            Assert.AreEqual(1, inspection.PageObjects);
            Assert.IsFalse(inspection.CountIsReliable);
        }

        [Test]
        public void MissingFileGivesAnUnknownCountInsteadOfThrowing()
        {
            var inspection = PdfInspector.Inspect(Path.Combine(_dir, "nope.pdf"));
            Assert.IsNull(inspection.PageObjects);
            Assert.IsFalse(inspection.CountIsReliable);
        }

        private static FakeSteps StepsWithPages(int pages, bool objectStreams = false)
        {
            var sb = new StringBuilder("%PDF-1.7\n");
            if (objectStreams) sb.Append("1 0 obj << /Type /ObjStm >> endobj\n");
            for (var i = 0; i < pages; i++) sb.Append((i + 2) + " 0 obj << /Type /Page >> endobj\n");
            return new FakeSteps { PdfBytes = Encoding.ASCII.GetBytes(sb.ToString()) };
        }

        [Test]
        public void MorePagesThanSheetsAbortsTheExport()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleExporter().Export(RequestFactory.Full(), StepsWithPages(3), Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("PDF 頁數（3）", ex.Message);
            StringAssert.Contains("已中止", ex.Message);
        }

        [Test]
        public void FewerPagesThanSheetsAbortsWhenTheCountIsReliable()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleExporter().Export(RequestFactory.Full(), StepsWithPages(1), Path.Combine(_dir, "b"), null, CancellationToken.None));
            StringAssert.Contains("已中止", ex.Message);
        }

        [Test]
        public void FewerPagesThanSheetsOnlyWarnsWhenTheCountIsUnreliable()
        {
            var bundle = new BundleExporter().Export(RequestFactory.Full(), StepsWithPages(1, objectStreams: true), Path.Combine(_dir, "b"), null, CancellationToken.None);
            Assert.IsTrue(bundle.Manifest["warnings"].Any(w => ((string)w).Contains("頁數")));
        }

        [Test]
        public void MatchingPageCountDoesNotWarnOrAbort()
        {
            var bundle = new BundleExporter().Export(RequestFactory.Full(), StepsWithPages(2), Path.Combine(_dir, "b"), null, CancellationToken.None);
            Assert.IsFalse(bundle.Manifest["warnings"].Any(w => ((string)w).Contains("頁數")));
        }
    }
}
