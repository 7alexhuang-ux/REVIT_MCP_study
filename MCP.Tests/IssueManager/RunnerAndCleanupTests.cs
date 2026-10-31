using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// QueuedApiThreadRunner：Revit 外部事件佇列的等待行為（取消、Raise() 被拒、開始前逾時、已開始就等做完）。
    /// 用假的 post 取代 ExternalEvent，所以能在沒有 Revit 的環境驗證。
    /// </summary>
    [TestFixture]
    public class QueuedApiThreadRunnerTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

        [Test]
        public async Task RunsTheWorkOnWhateverThreadThePostUsesAndReturnsItsResult()
        {
            var workThread = -1;
            var runner = new QueuedApiThreadRunner(job => { var t = new Thread(() => job()); t.Start(); return true; });
            var result = await runner.RunAsync(() => { workThread = Thread.CurrentThread.ManagedThreadId; return 42; }, CancellationToken.None);
            Assert.AreEqual(42, result);
            Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, workThread, "工作在 post 指定的執行緒（Revit API 執行緒）執行");
        }

        [Test]
        public void WorkExceptionsFlowToTheCaller()
        {
            var runner = new QueuedApiThreadRunner(job => { Task.Run(job); return true; });
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync<int>(() => { throw new InvalidOperationException("匯出失敗"); }, CancellationToken.None));
            Assert.AreEqual("匯出失敗", ex.Message);
        }

        [Test]
        public async Task CancelWhileStillQueuedEndsTheWaitAndTheWorkNeverRunsEvenIfRevitDrainsTheQueueLater()
        {
            Action stored = null;
            var ran = false;
            var runner = new QueuedApiThreadRunner(job => { stored = job; return true; }, TimeSpan.FromMinutes(5));
            using (var cts = new CancellationTokenSource())
            {
                var task = runner.RunAsync(() => { ran = true; return 1; }, cts.Token);
                Assert.IsFalse(task.IsCompleted, "Revit 還沒處理佇列：呼叫端在等");
                cts.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await task);
            }
            stored(); // Revit 之後才處理佇列（例如使用者關掉對話框）
            await Task.Delay(50);
            Assert.IsFalse(ran, "已取消的工作不能在稍後被執行");
        }

        [Test]
        public void CancelledBeforeQueueingNeverPostsAnything()
        {
            var posted = false;
            var runner = new QueuedApiThreadRunner(job => { posted = true; return true; });
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await runner.RunAsync(() => 1, cts.Token));
            }
            Assert.IsFalse(posted);
        }

        [Test]
        public async Task RevitNeverStartsTheWorkTimesOutWithAPlainMessageAndTheLateJobIsSkipped()
        {
            Action stored = null;
            var ran = false;
            var runner = new QueuedApiThreadRunner(job => { stored = job; return true; }, Short);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(() => { ran = true; return 1; }, CancellationToken.None));
            StringAssert.Contains("Revit 目前沒有回應，請關掉 Revit 裡的對話框後重試", ex.Message);
            stored();
            await Task.Delay(50);
            Assert.IsFalse(ran, "逾時放棄的工作之後不能再執行（否則 Revit 會在背景默默匯出）");
        }

        [Test]
        public async Task RaiseRejectedFailsImmediatelyInsteadOfWaitingForever()
        {
            Action stored = null;
            var ran = false;
            var runner = new QueuedApiThreadRunner(job => { stored = job; return false; }, TimeSpan.FromMinutes(5));
            var started = DateTime.UtcNow;
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(() => { ran = true; return 1; }, CancellationToken.None));
            StringAssert.Contains("Revit 沒有接受", ex.Message);
            Assert.Less((DateTime.UtcNow - started).TotalSeconds, 5, "被拒絕時要立刻失敗，不是等逾時");
            stored();
            await Task.Delay(50);
            Assert.IsFalse(ran);
        }

        [Test]
        public void PostThrowingIsReportedAsThePlainRejectionMessage()
        {
            var runner = new QueuedApiThreadRunner(job => { throw new ObjectDisposedException("ExternalEvent"); });
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(() => 1, CancellationToken.None));
            StringAssert.Contains("Revit 沒有接受", ex.Message);
        }

        [Test]
        public async Task OnceTheWorkHasStartedNeitherTheStartTimeoutNorCancelInterruptsIt()
        {
            // GLB 匯出可達十幾分鐘：開始執行後不受「開始前逾時」限制，取消也要等它做完（API 呼叫無法中途打斷）。
            var runner = new QueuedApiThreadRunner(job => { Task.Run(job); return true; }, TimeSpan.FromMilliseconds(100));
            using (var cts = new CancellationTokenSource())
            {
                var started = new ManualResetEventSlim();
                var task = runner.RunAsync(() => { started.Set(); Thread.Sleep(500); return "done"; }, cts.Token);
                Assert.IsTrue(started.Wait(2000));
                cts.Cancel();
                Assert.AreEqual("done", await task);
            }
        }
    }

    [TestFixture]
    public class UploadFlowStuckRevitTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("flow-stuck"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static async Task<AddinApiClient> LoggedInAsync(FakeTokenServer server) { return await Harness.LoggedInApiAsync(server); }

        private static ExportRequest Request()
        {
            var request = RequestFactory.Full();
            request.ProjectId = "p1";
            return request;
        }

        [Test]
        public async Task ExternalEventNeverRunning_FlowEndsWithAPlainMessage_AndTheRunningFlagIsReleased()
        {
            var server = new FakeTokenServer();
            var api = await LoggedInAsync(server);
            var steps = new FakeSteps();
            var stuck = new QueuedApiThreadRunner(job => true, TimeSpan.FromMilliseconds(250)); // 排進佇列但 Revit 一直不處理（停在對話框）
            var flow = new UploadFlow(api, steps, stuck, Path.Combine(_dir, "bundles"));

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(Request(), null, null, CancellationToken.None));

            StringAssert.Contains("Revit 目前沒有回應", ex.Message);
            Assert.IsFalse(UploadFlow.IsRunning, "靜態 _running 旗標必須在逾時後歸零，否則要重開 Revit 才能再上傳");
            CollectionAssert.IsEmpty(steps.Calls, "Revit 沒執行任何步驟");
            Assert.IsEmpty(Directory.Exists(Path.Combine(_dir, "bundles")) ? Directory.GetDirectories(Path.Combine(_dir, "bundles")) : new string[0], "本機暫存不留");

            // 之後可以立刻再上傳
            var again = new UploadFlow(api, new FakeSteps(), new InlineRunner(), Path.Combine(_dir, "bundles2"));
            var result = await again.RunAsync(Request(), null, null, CancellationToken.None);
            Assert.IsNotNull(result.SetId);
            Assert.IsFalse(UploadFlow.IsRunning);
        }

        [Test]
        public async Task CancelWhileTheExportIsStillQueuedStopsTheFlowDeletesTheBundleAndReleasesTheFlag()
        {
            var server = new FakeTokenServer();
            var api = await LoggedInAsync(server);
            var stuck = new QueuedApiThreadRunner(job => true, TimeSpan.FromMinutes(5));
            var flow = new UploadFlow(api, new FakeSteps(), stuck, Path.Combine(_dir, "bundles"));
            using (var cts = new CancellationTokenSource())
            {
                var task = flow.RunAsync(Request(), null, null, cts.Token);
                await Task.Delay(300);
                Assert.IsTrue(UploadFlow.IsRunning);
                Assert.IsFalse(task.IsCompleted, "等待 Revit 處理佇列中");
                cts.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await task);
            }
            Assert.IsFalse(UploadFlow.IsRunning);
            Assert.AreEqual(1, server.Deleted.Count, "取消時伺服器端的 bundle 也要刪除");
        }

        [Test]
        public async Task RaiseRejectedFailsTheFlowAtOnceAndReleasesTheFlag()
        {
            var server = new FakeTokenServer();
            var api = await LoggedInAsync(server);
            var rejecting = new QueuedApiThreadRunner(job => false, TimeSpan.FromMinutes(5));
            var flow = new UploadFlow(api, new FakeSteps(), rejecting, Path.Combine(_dir, "bundles"));
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(Request(), null, null, CancellationToken.None));
            StringAssert.Contains("Revit 沒有接受", ex.Message);
            Assert.IsFalse(UploadFlow.IsRunning);
        }

        [Test]
        public async Task AStepExceptionAlsoReleasesTheFlag()
        {
            var server = new FakeTokenServer();
            var api = await LoggedInAsync(server);
            var steps = new FakeSteps { ThrowInModel = new InvalidOperationException("模型匯出失敗") };
            var flow = new UploadFlow(api, steps, new InlineRunner(), Path.Combine(_dir, "bundles"));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.RunAsync(Request(), null, null, CancellationToken.None));
            Assert.IsFalse(UploadFlow.IsRunning);
        }
    }

    [TestFixture]
    public class ExporterStepSplittingTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("exporter-steps"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private sealed class LoggingRunner : IApiThreadRunner
        {
            public readonly List<string> Events;
            public LoggingRunner(List<string> events) { Events = events; }

            public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
            {
                Events.Add("run");
                return new InlineApiThreadRunner().RunAsync(work, ct);
            }
        }

        private sealed class LoggingProgress : IExportProgress
        {
            private readonly List<string> _events;
            public LoggingProgress(List<string> events) { _events = events; }
            public void Report(string text, int step, int totalSteps) { _events.Add("report"); }
        }

        [Test]
        public async Task EveryRevitStepIsItsOwnQueuedCallAndProgressIsReportedBetweenThem()
        {
            var events = new List<string>();
            var steps = new FakeSteps();
            var bundle = await new BundleExporter().ExportAsync(RequestFactory.Full(), steps, new LoggingRunner(events),
                Path.Combine(_dir, "b"), new LoggingProgress(events), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb", "namedViews" }, steps.Calls);
            CollectionAssert.AreEqual(new[] { "report", "run", "report", "run", "report", "run", "report", "run", "report" }, events,
                "doc／pdf／glb／namedViews 各自一次 ExternalEvent，步驟之間回到呼叫端（Revit 訊息迴圈）；manifest 不佔用 Revit 執行緒");
            Assert.IsTrue(File.Exists(bundle.ManifestPath));
        }

        [Test]
        public void CancelWhileAStepIsStillQueuedStopsTheExportBeforeAnyLaterStep()
        {
            var steps = new FakeSteps();
            using (var cts = new CancellationTokenSource())
            {
                var stuck = new QueuedApiThreadRunner(job => { cts.Cancel(); return true; }, TimeSpan.FromMinutes(5));
                Assert.CatchAsync<OperationCanceledException>(async () =>
                    await new BundleExporter().ExportAsync(RequestFactory.Full(), steps, stuck, Path.Combine(_dir, "b"), null, cts.Token));
            }
            CollectionAssert.IsEmpty(steps.Calls);
        }
    }

    [TestFixture]
    public class BundleTempCleanerTests
    {
        private string _root;
        [SetUp] public void SetUp() { _root = TempDir.Create("cleaner"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_root); }

        private string MakeBundleDir(string name, DateTime lastWriteUtc, bool freshFile = false)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "model.glb");
            File.WriteAllText(file, "glTF-big-file");
            File.SetLastWriteTimeUtc(file, freshFile ? DateTime.UtcNow : lastWriteUtc);
            Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
            return dir;
        }

        [Test]
        public void DeletesOnlyBundleFoldersOlderThanTheLimit()
        {
            var now = DateTimeOffset.UtcNow;
            var old = MakeBundleDir("20261001-000000-aaaaaa", now.UtcDateTime.AddDays(-5));
            var recent = MakeBundleDir("20261024-000000-bbbbbb", now.UtcDateTime.AddHours(-5));
            var oldButStillBeingWritten = MakeBundleDir("20261001-000000-cccccc", now.UtcDateTime.AddDays(-5), freshFile: true);
            var strayFile = Path.Combine(_root, "notes.txt");
            File.WriteAllText(strayFile, "keep me");
            File.SetLastWriteTimeUtc(strayFile, now.UtcDateTime.AddDays(-30));

            var deleted = BundleTempCleaner.CleanOld(_root, TimeSpan.FromDays(BundleTempCleaner.DefaultMaxAgeDays), now);

            Assert.AreEqual(1, deleted);
            Assert.IsFalse(Directory.Exists(old), "超過 3 天的暫存要刪");
            Assert.IsTrue(Directory.Exists(recent));
            Assert.IsTrue(Directory.Exists(oldButStillBeingWritten), "資料夾內有最近才寫入的檔案（上傳中）不能刪");
            Assert.IsTrue(File.Exists(strayFile), "只處理子資料夾，不動根目錄下的其他檔案");
        }

        [Test]
        public void DefaultAgeLimitIsThreeDays()
        {
            Assert.AreEqual(3, BundleTempCleaner.DefaultMaxAgeDays);
            StringAssert.EndsWith(Path.Combine("RevitMCP", "issue-manager", "bundles"), BundleTempCleaner.DefaultRoot());
        }

        [Test]
        public void MissingRootIsNotAnError()
        {
            Assert.AreEqual(0, BundleTempCleaner.CleanOld(Path.Combine(_root, "no-such"), TimeSpan.FromDays(3), DateTimeOffset.UtcNow));
            Assert.AreEqual(0, BundleTempCleaner.CleanOld(null, TimeSpan.FromDays(3), DateTimeOffset.UtcNow));
        }

        [Test]
        public void ALockedFileDoesNotStopTheOtherFoldersFromBeingCleaned()
        {
            var now = DateTimeOffset.UtcNow;
            var locked = MakeBundleDir("20261001-000000-locked", now.UtcDateTime.AddDays(-9));
            var other = MakeBundleDir("20261001-000000-other", now.UtcDateTime.AddDays(-9));
            using (new FileStream(Path.Combine(locked, "model.glb"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BundleTempCleaner.CleanOld(_root, TimeSpan.FromDays(3), now);
            }
            Assert.IsFalse(Directory.Exists(other));
        }
    }
}
