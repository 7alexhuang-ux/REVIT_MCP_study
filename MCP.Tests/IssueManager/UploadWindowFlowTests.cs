using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using NUnit.Framework;
using WpfVisibility = System.Windows.Visibility;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>模擬 Revit：API 工作與視窗在同一條（STA／Dispatcher）執行緒上執行，就像 ExternalEvent 處理常式與 WPF 視窗都在 Revit 主執行緒。</summary>
    internal sealed class DispatcherRunner : IApiThreadRunner
    {
        private readonly Dispatcher _dispatcher;
        public DispatcherRunner(Dispatcher dispatcher) { _dispatcher = dispatcher; }

        public Task<T> RunAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.BeginInvoke(new Action(() =>
            {
                try { completion.SetResult(work()); }
                catch (OperationCanceledException) { completion.SetCanceled(); }
                catch (Exception ex) { completion.SetException(ex); }
            }));
            return completion.Task;
        }
    }

    /// <summary>
    /// 勾選視窗從頭到尾的行為（按「上傳」→ 建立版本包 → 在「API 執行緒」匯出並讓視窗處理訊息 → 背景上傳 → finalize → 顯示結果），
    /// 以及按「取消」。用真的視窗、真的 UploadFlow，只有 Revit 匯出步驟與伺服器是假的。
    /// </summary>
    [TestFixture]
    public class UploadWindowFlowTests
    {
        private string _dir;
        [SetUp] public void SetUp() { _dir = TempDir.Create("window-flow"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60))) { thread.Abort(); throw new AssertionException("視窗流程測試逾時（60 秒）。"); }
            if (error != null) throw new AssertionException(error.ToString());
        }

        private static void PumpUntil(Func<bool> done, int timeoutMs = 20000)
        {
            var timer = Stopwatch.StartNew();
            while (!done())
            {
                if (timer.ElapsedMilliseconds > timeoutMs) throw new AssertionException("等待視窗流程逾時。");
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Thread.Sleep(5);
            }
        }

        private sealed class Rig
        {
            public FakeTokenServer Server;
            public FakeSteps Steps;
            public IssueManagerUploadWindow Window;
            public ExportSelectionViewModel Vm;
        }

        private Rig BuildRig()
        {
            var server = new FakeTokenServer { PartBytes = 16 };
            var pair = Harness.LoggedInAsync(server).GetAwaiter().GetResult();
            var steps = new FakeSteps();
            var runner = new DispatcherRunner(Dispatcher.CurrentDispatcher);
            var vm = new ExportSelectionViewModel(Samples.Snapshot());
            var services = new UploadWindowServices
            {
                Auth = pair.Item1,
                Api = pair.Item2,
                CreateFlow = () => new UploadFlow(pair.Item2, steps, runner, Path.Combine(_dir, "bundles"),
                    uploader: new BundleUploader(pair.Item2, 2, (span, token) => Task.FromResult(0))),
                Log = s => { }
            };
            var window = new IssueManagerUploadWindow(vm, services);
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "測試專案" } });
            window.ProjectBox.SelectedItem = vm.Projects[0];
            window.StageBox.SelectedItem = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "視窗流程測試";
            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "送審圖");
            return new Rig { Server = server, Steps = steps, Window = window, Vm = vm };
        }

        private static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        [Test]
        public void ClickingUploadRunsTheWholeFlowAndShowsTheResultLink()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                Assert.IsTrue(rig.Window.UploadButton.IsEnabled);
                Click(rig.Window.UploadButton);
                Assert.IsTrue(rig.Window.IsBusy, "按下去立刻進入忙碌狀態");
                Assert.IsFalse(rig.Window.UploadButton.IsEnabled);

                PumpUntil(() => !rig.Window.IsBusy);

                CollectionAssert.AreEqual(new[] { "doc", "pdf", "glb" }, rig.Steps.Calls, "送審圖集只勾 A-2 與立面：沒有 3D 視圖 → 不匯出視角");
                Assert.AreEqual(1, rig.Server.FinalizeManifests.Count);
                Assert.AreEqual("SD", (string)rig.Server.CreateRequests[0]["stage"]);
                StringAssert.Contains("完成", rig.Window.StatusText.Text);
                var link = rig.Window.ResultText.Inlines.OfType<Hyperlink>().FirstOrDefault();
                Assert.IsNotNull(link, "完成後顯示版本連結；inlines=" + string.Join(",", rig.Window.ResultText.Inlines.Select(i => i.GetType().Name)) + " text=" + rig.Window.ResultText.Text + " status=" + rig.Window.StatusText.Text);
                StringAssert.StartsWith("https://im.example.test/sets/set-", link.NavigateUri.AbsoluteUri);
                Assert.IsTrue(rig.Window.UploadButton.IsEnabled, "完成後可以再上傳");
                Assert.AreEqual("關閉", rig.Window.CancelButton.Content);
            });
        }

        [Test]
        public void CancelButtonDuringExportCancelsAfterTheCurrentStepAndCleansTheServer()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                var cancelled = false;
                rig.Steps.OnCall = name =>
                {
                    if (name == "pdf" && !cancelled)
                    {
                        cancelled = true;
                        Click(rig.Window.CancelButton); // 使用者在 PDF 匯出後按「取消上傳」
                    }
                };
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);

                CollectionAssert.AreEqual(new[] { "doc", "pdf" }, rig.Steps.Calls, "取消後不進入 GLB 步驟");
                Assert.AreEqual(0, rig.Server.Puts.Count);
                Assert.AreEqual(0, rig.Server.FinalizeManifests.Count);
                Assert.AreEqual(1, rig.Server.Deleted.Count);
                StringAssert.Contains("已取消", rig.Window.StatusText.Text);
                Assert.AreEqual(WpfVisibility.Collapsed, rig.Window.RetryButton.Visibility);
            });
        }

        [Test]
        public void UploadFailureShowsRetryAndRetrySucceedsWithoutExportingAgain()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.FailNextPuts = 100;
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);

                StringAssert.Contains("Issue Manager", rig.Window.StatusText.Text);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);
                Assert.AreEqual(0, rig.Server.FinalizeManifests.Count);
                var exports = rig.Steps.Calls.Count(c => c == "pdf");

                rig.Server.FailNextPuts = 0;
                Click(rig.Window.RetryButton);
                PumpUntil(() => !rig.Window.IsBusy);
                Assert.AreEqual(1, rig.Server.FinalizeManifests.Count);
                Assert.AreEqual(exports, rig.Steps.Calls.Count(c => c == "pdf"), "重試不重新匯出");
                Assert.AreEqual(WpfVisibility.Collapsed, rig.Window.RetryButton.Visibility);
                StringAssert.Contains("完成", rig.Window.StatusText.Text);
            });
        }

        [Test]
        public void FinalizeRejectionIsShownAsPlainMessageAndOffersRetry()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.RejectFinalizeWithCode = "invalid_manifest";
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                StringAssert.Contains("清單", rig.Window.StatusText.Text);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);
            });
        }
    }
}
