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
    /// 勾選視窗（WPF）的建構與綁定檢查：在 STA 執行緒建立視窗但不顯示，直接操作控制項並讀回狀態。
    /// 這驗證 ViewModel 與控制項的綁定（X06 的畫面層），不能取代 Revit 內的實機操作（見交班單的實機清單）。
    /// </summary>
    [TestFixture]
    public class UploadWindowTests
    {
        private static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw new AssertionException(error.ToString());
        }

        private static AddinAuthService LoggedInAuth()
        {
            var vault = new InMemoryVault
            {
                Stored = new StoredCredentials
                {
                    BaseUrl = FakeTokenServer.BaseUrl, RefreshToken = "imrt_seed", RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
                    UserEmail = "alex@example.test", UserRole = "admin"
                }
            };
            var auth = new AddinAuthService(new HttpClient(new FakeHandler(r => FakeHandler.Error(System.Net.HttpStatusCode.ServiceUnavailable, "x"))),
                vault, FakeTokenServer.BaseUrl, ClientInfo.Create("PC", "1.0"));
            Assert.IsTrue(auth.TryRestore());
            return auth;
        }

        private static IssueManagerUploadWindow NewWindow(ExportSelectionViewModel vm, UploadWindowServices services = null)
        {
            return new IssueManagerUploadWindow(vm, services ?? new UploadWindowServices { Auth = LoggedInAuth(), Log = s => { } });
        }

        private static CheckBox HeaderOf(TreeViewItem item) { return (CheckBox)item.Header; }

        [Test]
        public void X06_UploadStaysDisabledUntilEverythingIsFilledAndStageHasNoDefault()
        {
            RunSta(() =>
            {
                var vm = new ExportSelectionViewModel(Samples.Snapshot());
                var window = NewWindow(vm);
                Assert.AreEqual("上傳到 Issue Manager", window.Title);
                Assert.IsTrue(vm.IsLoggedIn, "還原登入後視窗應視為已登入");
                Assert.IsFalse(window.UploadButton.IsEnabled);
                Assert.AreEqual(4, window.StageBox.Items.Count);
                Assert.AreEqual("請選擇階段", ((StageOption)window.StageBox.SelectedItem).Label);
                Assert.AreEqual("請選擇專案", window.UploadButton.ToolTip);

                vm.SetProjects(new[] { new AddinProject { Id = "p1", Code = "CPC", Name = "測試專案" } });
                window.ProjectBox.SelectedItem = vm.Projects[0];
                Assert.AreSame(vm.Projects[0], vm.SelectedProject, "下拉選擇要回寫到 ViewModel");
                vm.Description = "說明";
                Assert.IsFalse(window.UploadButton.IsEnabled);
                Assert.AreEqual("請選擇設計階段", window.UploadButton.ToolTip);

                window.StageBox.SelectedItem = vm.Stages.First(s => s.Code == "DD");
                Assert.AreEqual("DD", vm.SelectedStage.Code);
                Assert.IsTrue(window.UploadButton.IsEnabled);
                Assert.IsNull(window.UploadButton.ToolTip);

                window.StageBox.SelectedItem = vm.Stages[0];
                Assert.IsFalse(window.UploadButton.IsEnabled, "改回「請選擇階段」又不能送出");
            });
        }

        [Test]
        public void TreeCheckBoxesDriveTheSummaryAndWarnings()
        {
            RunSta(() =>
            {
                var vm = new ExportSelectionViewModel(Samples.Snapshot());
                var window = NewWindow(vm);
                StringAssert.StartsWith("將上傳 0 張圖紙、0 個視圖", window.SummaryText.Text); StringAssert.Contains("1 個模型", window.SummaryText.Text);
                StringAssert.Contains("缺圖", window.WarningText.Text);

                var sheetsRoot = (TreeViewItem)window.Tree.Items[0];
                Assert.AreEqual(3, sheetsRoot.Items.Count);
                var a2 = (TreeViewItem)sheetsRoot.Items[1];
                StringAssert.Contains("A-2", (string)HeaderOf(a2).Content);
                HeaderOf(a2).IsChecked = true;
                Assert.IsTrue(vm.Sheets[1].IsChecked);
                Assert.IsTrue(vm.Sheets[1].Viewports.All(v => v.IsChecked), "勾圖紙連視口視圖一起勾");
                StringAssert.StartsWith("將上傳 1 張圖紙、2 個視圖", window.SummaryText.Text); StringAssert.Contains("1 個模型", window.SummaryText.Text);
                Assert.AreEqual(string.Empty, window.WarningText.Text);

                window.ModelBox.IsChecked = false;
                Assert.IsFalse(vm.IncludeModel);
                Assert.IsFalse(window.OptionsBox.IsEnabled, "沒勾模型時設計選項選項停用");
                StringAssert.Contains("缺模型", window.WarningText.Text);

                var others = (TreeViewItem)window.Tree.Items[1];
                Assert.AreEqual(2, others.Items.Count, "3D 與立面兩個分組");
            });
        }

        [Test]
        public void SheetSetDropdownAppliesDefaultsAndFallsBackToCustom()
        {
            RunSta(() =>
            {
                var vm = new ExportSelectionViewModel(Samples.Snapshot());
                var window = NewWindow(vm);
                Assert.AreEqual(3, window.SheetSetBox.Items.Count);
                window.SheetSetBox.SelectedItem = vm.SheetSets.First(s => s.Label == "全部");
                Assert.AreEqual(3, vm.CheckedSheetCount);
                var firstSheet = HeaderOf((TreeViewItem)((TreeViewItem)window.Tree.Items[0]).Items[0]);
                Assert.IsTrue(firstSheet.IsChecked == true);
                firstSheet.IsChecked = false;
                Assert.AreEqual("（自訂）", ((SheetSetOption)window.SheetSetBox.SelectedItem).Label);
            });
        }

        [Test]
        public void MissingConfigurationDisablesLoginAndExplainsHowToFix()
        {
            RunSta(() =>
            {
                var vm = new ExportSelectionViewModel(Samples.Snapshot());
                var window = NewWindow(vm, new UploadWindowServices { ConfigurationError = "尚未設定 Issue Manager 網址。" });
                Assert.IsFalse(window.LoginButton.IsEnabled);
                StringAssert.Contains("尚未設定", window.AccountText.Text);
                Assert.IsFalse(window.UploadButton.IsEnabled);
            });
        }

        [Test]
        public void NotLoggedInShowsLoginButtonAndBlocksUpload()
        {
            RunSta(() =>
            {
                var vault = new InMemoryVault();
                var auth = new AddinAuthService(new HttpClient(new FakeHandler(r => FakeHandler.Error(System.Net.HttpStatusCode.ServiceUnavailable, "x"))),
                    vault, FakeTokenServer.BaseUrl, ClientInfo.Create("PC", "1.0"));
                var vm = new ExportSelectionViewModel(Samples.Snapshot());
                var window = NewWindow(vm, new UploadWindowServices { Auth = auth });
                Assert.AreEqual("尚未登入", window.AccountText.Text);
                Assert.AreEqual(System.Windows.Visibility.Visible, window.LoginButton.Visibility);
                Assert.AreEqual("請先登入", window.UploadButton.ToolTip);
            });
        }
    }
}
