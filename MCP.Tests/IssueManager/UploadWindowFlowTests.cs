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

        public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested) { completion.SetCanceled(); return; }
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
            public UploadWindowServices Services;
            public string BundlesRoot;
            public int Approvals;
        }

        /// <summary>模擬瀏覽器：解析授權網址 → 授權 → 打 loopback callback。</summary>
        private static ActionBrowser ApprovingBrowser(FakeTokenServer server, Action onApprove)
        {
            return new ActionBrowser(url =>
            {
                var query = FakeTokenServer.ParseQuery(new Uri(url).Query);
                var code = server.ApproveFromAuthorizeUrl(url);
                onApprove();
                System.Threading.Tasks.Task.Run(async () =>
                {
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        try { await client.GetAsync("http://127.0.0.1:" + query["port"] + "/callback?code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(query["state"])); }
                        catch (Exception) { }
                    }
                });
            });
        }

        private Rig BuildRig(Func<DateTimeOffset> now = null)
        {
            var server = new FakeTokenServer { PartBytes = 16 };
            var pair = Harness.LoggedInAsync(server, now).GetAwaiter().GetResult();
            var steps = new FakeSteps();
            var runner = new DispatcherRunner(Dispatcher.CurrentDispatcher);
            var vm = new ExportSelectionViewModel(Samples.Snapshot());
            var rig = new Rig { Server = server, Steps = steps, BundlesRoot = Path.Combine(_dir, "bundles") };
            var services = new UploadWindowServices
            {
                Auth = pair.Item1,
                Api = pair.Item2,
                Browser = ApprovingBrowser(server, () => rig.Approvals++),
                CreateFlow = () => new UploadFlow(pair.Item2, steps, runner, rig.BundlesRoot,
                    uploader: new BundleUploader(pair.Item2, 2, (span, token) => Task.FromResult(0))),
                Log = s => { }
            };
            rig.Services = services;
            var window = new IssueManagerUploadWindow(vm, services);
            vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "測試專案" } });
            window.ProjectBox.SelectedItem = vm.Projects[0];
            window.StageBox.SelectedItem = vm.Stages.First(s => s.Code == "SD");
            vm.Description = "視窗流程測試";
            vm.SelectedSheetSet = vm.SheetSets.First(s => s.Label == "送審圖");
            rig.Window = window;
            rig.Vm = vm;
            return rig;
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

        private static string[] LocalBundleDirectories(Rig rig)
        {
            return Directory.Exists(rig.BundlesRoot) ? Directory.GetDirectories(rig.BundlesRoot) : new string[0];
        }

        [Test]
        public void ClosingTheWindowWithXAfterAFailedUploadDiscardsTheServerBundleAndLocalFiles()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.FailNextPuts = 100;
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);
                Assert.AreEqual(1, LocalBundleDirectories(rig).Length, "可重試狀態：本機匯出檔還在");
                Assert.AreEqual(0, rig.Server.Deleted.Count);

                rig.Window.Close(); // 等同按右上角 X（Closing 事件）

                Assert.AreEqual(0, LocalBundleDirectories(rig).Length, "關閉視窗後本機的 PDF／GLB 暫存必須刪除");
                PumpUntil(() => rig.Server.Deleted.Count == 1);
                Assert.AreEqual(0, rig.Server.FinalizeManifests.Count);
            });
        }

        [Test]
        public void ClosingAsksBeforeDiscardingAndAnswerNoKeepsTheRetryableUpload()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                var asked = 0;
                var answer = false;
                rig.Services.ConfirmDiscardOnClose = () => { asked++; return answer; };
                rig.Server.FailNextPuts = 100;
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);

                rig.Window.Close();
                Assert.AreEqual(1, asked, "有未完成的 bundle 時要詢問「放棄這次上傳並清除？」");
                Assert.AreEqual(1, LocalBundleDirectories(rig).Length, "回答「否」：什麼都不刪");
                Assert.AreEqual(0, rig.Server.Deleted.Count);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);

                // 回到視窗仍可重試成功
                rig.Server.FailNextPuts = 0;
                Click(rig.Window.RetryButton);
                PumpUntil(() => !rig.Window.IsBusy);
                Assert.AreEqual(1, rig.Server.FinalizeManifests.Count);

                // 成功後沒有待重試的上傳 → 直接關閉，不再詢問
                rig.Window.Close();
                Assert.AreEqual(1, asked);
            });
        }

        [Test]
        public void ClosingWithAnAlreadyFinishedUploadNeverAsksOrDeletesAnything()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                var asked = 0;
                rig.Services.ConfirmDiscardOnClose = () => { asked++; return true; };
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                rig.Window.Close();
                Assert.AreEqual(0, asked);
                Assert.AreEqual(0, rig.Server.Deleted.Count);
            });
        }

        [Test]
        public void LogoutAfterAccessExpiredRevokesOnTheServerThroughTheRefreshToken()
        {
            RunSta(() =>
            {
                var clock = DateTimeOffset.UtcNow;
                var rig = BuildRig(() => clock);
                var access = rig.Server.LastIssuedAccess;
                clock = clock.AddHours(2); // access 過期
                Click(rig.Window.LogoutButton);
                PumpUntil(() => !rig.Window.IsBusy);

                Assert.AreEqual(1, rig.Server.RevokeByRefresh);
                Assert.IsFalse(rig.Server.IsAccessValid(access));
                StringAssert.Contains("已登出", rig.Window.StatusText.Text);
                StringAssert.DoesNotContain("未完成", rig.Window.StatusText.Text);
            });
        }

        [Test]
        public void LogoutThatTheServerCouldNotRevokeTellsTheUserHonestly()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.Handler.BeforeRespond = r =>
                {
                    if (r.Uri.AbsolutePath.EndsWith("/revoke")) throw new System.Net.Http.HttpRequestException("offline");
                    return Task.FromResult(0);
                };
                Click(rig.Window.LogoutButton);
                PumpUntil(() => !rig.Window.IsBusy);

                StringAssert.Contains("伺服器端撤銷未完成", rig.Window.StatusText.Text);
                StringAssert.Contains("已授權的外掛", rig.Window.StatusText.Text, "要指引使用者到網站帳號頁手動撤銷");
                StringAssert.Contains("本機的登入資料已刪除", rig.Window.StatusText.Text);
                Assert.IsFalse(rig.Services.Auth.IsLoggedIn);
            });
        }

        [Test]
        public void SwitchAccountTakesTheSameRevokePathAndLogsInAgain()
        {
            RunSta(() =>
            {
                var clock = DateTimeOffset.UtcNow;
                var rig = BuildRig(() => clock);
                var oldAccess = rig.Server.LastIssuedAccess;
                clock = clock.AddHours(2);
                Click(rig.Window.SwitchAccountButton);
                PumpUntil(() => !rig.Window.IsBusy && rig.Approvals > 0 && rig.Services.Auth.IsLoggedIn && rig.Vm.Projects.Count > 0); // 專案清單載入完才結束，免得背景工作在測試結束後還去碰已結束的 UI 執行緒

                Assert.AreEqual(1, rig.Server.RevokeByRefresh, "切換帳號：access 已過期 → 用 refresh 撤銷舊帳號");
                Assert.IsFalse(rig.Server.IsAccessValid(oldAccess));
                Assert.IsTrue(rig.Services.Auth.IsLoggedIn, "撤銷後重新登入成功");
                StringAssert.DoesNotContain("未完成", rig.Window.ResultText.Text);
            });
        }

        [Test]
        public void SwitchAccountWithAFailedRevokeStillLogsInAndWarnsAboutTheOldAccount()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.Handler.BeforeRespond = r =>
                {
                    if (r.Uri.AbsolutePath.EndsWith("/revoke")) throw new System.Net.Http.HttpRequestException("offline");
                    return Task.FromResult(0);
                };
                Click(rig.Window.SwitchAccountButton);
                PumpUntil(() => !rig.Window.IsBusy && rig.Approvals > 0 && rig.Services.Auth.IsLoggedIn && rig.Vm.Projects.Count > 0); // 專案清單載入完才結束，免得背景工作在測試結束後還去碰已結束的 UI 執行緒

                StringAssert.Contains("撤銷未完成", rig.Window.ResultText.Text);
                StringAssert.Contains("已授權的外掛", rig.Window.ResultText.Text);
            });
        }

        [Test]
        public void CancellingWhileWaitingForBrowserLoginShowsCancelledNotAnError()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Services.Browser = new ActionBrowser(url => { }); // 瀏覽器開了但使用者一直不授權
                Click(rig.Window.SwitchAccountButton);
                PumpUntil(() => rig.Window.CancelButton.Content as string == "取消登入");
                Click(rig.Window.CancelButton);
                PumpUntil(() => !rig.Window.IsBusy);
                Assert.AreEqual("已取消登入。", rig.Window.StatusText.Text);
            });
        }

        [Test]
        public void FinalizeRejectionIsShownAsPlainMessageAndOffersRetry()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.RejectFinalizeWithCode = "checksum_mismatch";
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                StringAssert.Contains("清單", rig.Window.StatusText.Text);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);
            });
        }

        [Test]
        public void RetryAfterTheServerVoidedTheBundleRecreatesItWithoutExportingAgain()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.RejectFinalizeWithCode = "invalid_file";
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                Assert.AreEqual(WpfVisibility.Visible, rig.Window.RetryButton.Visibility);
                var exports = rig.Steps.Calls.Count(c => c == "pdf");

                rig.Server.RejectFinalizeWithCode = null;
                Click(rig.Window.RetryButton);
                PumpUntil(() => !rig.Window.IsBusy);

                StringAssert.Contains("完成", rig.Window.StatusText.Text);
                Assert.AreEqual(exports, rig.Steps.Calls.Count(c => c == "pdf"), "重試不重新匯出");
                Assert.AreEqual(2, rig.Server.CreatedBundleIds.Count, "重新建立版本包");
                Assert.AreEqual(2, rig.Server.FinalizeRequestBundleIds.Count);
                Assert.AreNotEqual(rig.Server.FinalizeRequestBundleIds[0], rig.Server.FinalizeRequestBundleIds[1], "不對作廢的版本包重送 finalize");
                Assert.AreEqual(WpfVisibility.Collapsed, rig.Window.RetryButton.Visibility);
            });
        }

        [Test]
        public void ContentRejectedByTheServerOffersNoRetryAndKeepsNoLocalFiles()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.RejectFinalizeWithCode = "invalid_manifest";
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);
                StringAssert.Contains("清單", rig.Window.StatusText.Text);
                Assert.AreEqual(WpfVisibility.Collapsed, rig.Window.RetryButton.Visibility, "同樣的內容再傳也會被拒，不提供重試");
                Assert.IsFalse(Directory.Exists(rig.BundlesRoot) && Directory.GetDirectories(rig.BundlesRoot).Length > 0);
            });
        }

        [Test]
        public void CancelAfterTheServerAlreadyCreatedTheVersionTellsTheUserToDeleteItOnTheWebsite()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                rig.Server.Handler.BeforeRespond = r =>
                {
                    if (r.Uri.AbsolutePath.EndsWith("/finalize", StringComparison.Ordinal))
                    {
                        rig.Server.MarkFinalized(r.Uri.AbsolutePath.Split('/')[5]);
                        rig.Window.Dispatcher.Invoke(new Action(() => Click(rig.Window.CancelButton))); // 使用者在 finalize 送出的同時按了「取消上傳」
                    }
                    return Task.FromResult(0);
                };
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);

                StringAssert.Contains("已經上傳完成，無法取消", rig.Window.StatusText.Text);
                StringAssert.Contains("網站", rig.Window.StatusText.Text);
                Assert.AreEqual(0, rig.Server.Deleted.Count);
                Assert.AreEqual(WpfVisibility.Collapsed, rig.Window.RetryButton.Visibility);
            });
        }

        [Test]
        public void WhileTheServerIsProcessingTheWindowSaysSo()
        {
            RunSta(() =>
            {
                var rig = BuildRig();
                var seen = new System.Collections.Generic.List<string>();
                var runner = new DispatcherRunner(Dispatcher.CurrentDispatcher);
                rig.Services.CreateFlow = () => new UploadFlow(rig.Services.Api, rig.Steps, runner, rig.BundlesRoot,
                    uploader: new BundleUploader(rig.Services.Api, 2, (span, token) => Task.FromResult(0)),
                    delay: async (span, token) =>
                    {
                        // 等「伺服器處理中」的提示真的出現在視窗上（Stage 是用 BeginInvoke 排進 UI 執行緒的）
                        for (var i = 0; i < 300; i++)
                        {
                            var text = rig.Window.Dispatcher.Invoke(new Func<string>(() => rig.Window.StatusText.Text));
                            if (text.Contains("伺服器處理中")) { seen.Add(text); return; }
                            await Task.Delay(10);
                        }
                    });
                rig.Server.FinalizeScript.Enqueue(r => FakeHandler.Error((System.Net.HttpStatusCode)409, "finalize_in_progress"));
                Click(rig.Window.UploadButton);
                PumpUntil(() => !rig.Window.IsBusy);

                StringAssert.Contains("完成", rig.Window.StatusText.Text);
                Assert.AreEqual(2, rig.Server.FinalizeRequestBundleIds.Count);
                Assert.IsNotEmpty(seen, "輪詢等待期間視窗要顯示「伺服器處理中」");
                StringAssert.Contains("已等待", seen[0]);
            });
        }
    }
}
