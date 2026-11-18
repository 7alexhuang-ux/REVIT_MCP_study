using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Tests.ViewSync
{
    /// <summary>Revit 跳轉（docs/REVIT_LINK.md §6 離線驗收、ADR D-015）：草稿暫存、瀏覽器白名單、設定與 IM 網址、唯讀掃描。</summary>
    [TestFixture]
    public class RevitLinkDraftStoreTests
    {
        private DateTime _now;
        private RevitLinkDraftStore _store;

        [SetUp]
        public void SetUp()
        {
            _now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
            _store = new RevitLinkDraftStore(() => _now);
        }

        private static JObject Draft(string tag = "a") => new JObject { ["intent"] = "create", ["tag"] = tag };

        [Test]
        public void Take_ReturnsDraftOnce_ThenNotFound()
        {
            var code = _store.Put(Draft("x"), TimeSpan.FromMinutes(10));
            JObject first;
            Assert.That(_store.Take(code, out first), Is.EqualTo(DraftTakeStatus.Ok));
            Assert.That((string)first["tag"], Is.EqualTo("x"));
            JObject second;
            Assert.That(_store.Take(code, out second), Is.EqualTo(DraftTakeStatus.NotFound));
            Assert.That(second, Is.Null);
        }

        [Test]
        public void Take_AfterLifetime_IsExpired_AndAlsoDeleted()
        {
            var code = _store.Put(Draft(), TimeSpan.FromMinutes(10));
            _now = _now.AddMinutes(10).AddSeconds(1);
            JObject draft;
            Assert.That(_store.Take(code, out draft), Is.EqualTo(DraftTakeStatus.Expired));
            Assert.That(draft, Is.Null);
            Assert.That(_store.Take(code, out draft), Is.EqualTo(DraftTakeStatus.NotFound), "過期的也要刪掉");
        }

        [Test]
        public void Take_JustBeforeExpiry_StillOk()
        {
            var code = _store.Put(Draft(), TimeSpan.FromMinutes(10));
            _now = _now.AddMinutes(9).AddSeconds(59);
            JObject draft;
            Assert.That(_store.Take(code, out draft), Is.EqualTo(DraftTakeStatus.Ok));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-real-code")]
        public void Take_UnknownCode_IsNotFound(string code)
        {
            JObject draft;
            Assert.That(_store.Take(code, out draft), Is.EqualTo(DraftTakeStatus.NotFound));
        }

        [Test]
        public void Code_IsAtLeast128BitsOfRandomness_AndUnique()
        {
            var codes = Enumerable.Range(0, 200).Select(i => _store.Put(Draft(), TimeSpan.FromMinutes(10))).ToList();
            Assert.That(codes.Distinct().Count(), Is.EqualTo(200));
            // base64url 每字元 6 位元；24 字元＝144 位元 ≥ 128
            Assert.That(codes.All(c => c.Length * 6 >= 128), Is.True);
            Assert.That(codes.All(c => c.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')), Is.True, "要能直接放進網址");
        }

        [Test]
        public void Put_StoresACopy_SoLaterEditsDoNotLeakIn()
        {
            var draft = Draft("before");
            var code = _store.Put(draft, TimeSpan.FromMinutes(10));
            draft["tag"] = "after";
            JObject taken;
            _store.Take(code, out taken);
            Assert.That((string)taken["tag"], Is.EqualTo("before"));
        }

        [Test]
        public void Sweep_DropsLongExpiredDrafts_OnNextPut()
        {
            _store.Put(Draft(), TimeSpan.FromMinutes(10));
            _store.Put(Draft(), TimeSpan.FromMinutes(10));
            Assert.That(_store.Count, Is.EqualTo(2));
            _now = _now.AddMinutes(10).AddMinutes(6); // 過期超過 5 分鐘
            _store.Put(Draft(), TimeSpan.FromMinutes(10));
            Assert.That(_store.Count, Is.EqualTo(1), "舊的兩份被清掉，只剩剛放進去的");
        }

        [Test]
        public void ExpiredWithinGrace_StaysSoUserSeesExpiredNotMissing()
        {
            var code = _store.Put(Draft(), TimeSpan.FromMinutes(10));
            _now = _now.AddMinutes(12);
            _store.Put(Draft(), TimeSpan.FromMinutes(10)); // 觸發 Sweep，但才過期 2 分鐘，不清
            JObject draft;
            Assert.That(_store.Take(code, out draft), Is.EqualTo(DraftTakeStatus.Expired));
        }
    }

    [TestFixture]
    public class RevitLinkWhitelistTests
    {
        [TestCase("viewsync_open_link")]
        [TestCase("viewsync_get_selection")]
        [TestCase("viewsync_capture_view")]
        [TestCase("viewsync_draft_take")]
        [TestCase("VIEWSYNC_OPEN_LINK")]
        public void NewReadOnlyCommands_AreBrowserAllowed(string command)
        {
            Assert.That(ViewSyncCommands.IsBrowserAllowed(command), Is.True);
        }

        [Test]
        public void DraftPut_IsNeverBrowserAllowed()
        {
            Assert.That(ViewSyncCommands.IsBrowserAllowed(ViewSyncCommands.DraftPut), Is.False);
            Assert.That(ViewSyncCommands.IsBrowserAllowed("viewsync_draft_put"), Is.False);
            Assert.That(ViewSyncCommands.Authorized, Does.Not.Contain(ViewSyncCommands.DraftPut));
        }

        [TestCase("open_issue_draft")]
        [TestCase("open_element_issues")]
        [TestCase("create_wall")]
        [TestCase("delete_element")]
        public void McpOnlyEntryPoints_AreNotBrowserAllowed(string command)
        {
            Assert.That(ViewSyncCommands.IsBrowserAllowed(command), Is.False, "只有 Claude／MCP 能叫，瀏覽器不行");
        }

        [Test]
        public void AuthorizedList_StillHasD710AndCameraCommands()
        {
            foreach (var c in new[] { "viewsync_status", "viewsync_apply_temp", "viewsync_open_view", "viewsync_create_view", "list_cameras", "camera_version" })
                Assert.That(ViewSyncCommands.IsBrowserAllowed(c), Is.True, c);
        }
    }

    [TestFixture]
    public class RevitLinkSettingsTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "revitlink-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private string Write(string name, string content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static string NoEnv(string name) => null;

        [Test]
        public void Load_NoFile_GivesDefaults()
        {
            var s = RevitLinkSettings.Load(Path.Combine(_dir, "missing.json"));
            Assert.That(s.LoadError, Is.Null);
            Assert.That(s.DraftMinutes, Is.EqualTo(10));
            Assert.That(s.CaptureMaxEdgePx, Is.EqualTo(2000));
            Assert.That(s.CaptureEnabled, Is.True);
            Assert.That(s.ImBaseUrl, Is.Null);
        }

        [Test]
        public void Load_ReadsAndClampsValues()
        {
            var path = Write("a.json", "{\"imBaseUrl\":\" https://im.example.com/ \",\"draftMinutes\":999,\"captureMaxEdgePx\":10,\"captureEnabled\":false}");
            var s = RevitLinkSettings.Load(path);
            Assert.That(s.ImBaseUrl, Is.EqualTo("https://im.example.com/"));
            Assert.That(s.DraftMinutes, Is.EqualTo(60));
            Assert.That(s.CaptureMaxEdgePx, Is.EqualTo(400));
            Assert.That(s.CaptureEnabled, Is.False);
        }

        [Test]
        public void Load_BrokenJson_FallsBackToDefaults_WithErrorAndNoContentLeak()
        {
            var path = Write("b.json", "{ this is not json https://secret.example.com");
            var s = RevitLinkSettings.Load(path);
            Assert.That(s.LoadError, Is.Not.Null);
            Assert.That(s.LoadError, Does.Not.Contain("secret"));
            Assert.That(s.DraftMinutes, Is.EqualTo(10));
        }

        [Test]
        public void ResolveImBaseUrl_PrefersOwnSetting_ThenEnvOrIssueManagerFile_ThenFirstAllowedOrigin()
        {
            var imFile = Write("issue-manager.json", "{\"baseUrl\":\"https://from-im-file.example.com\"}");
            var origins = new[] { "https://origin-one.example.com", "https://origin-two.example.com" };

            var own = RevitLinkSettings.Load(Write("own.json", "{\"imBaseUrl\":\"https://own.example.com\"}"));
            Assert.That(own.ResolveImBaseUrl(NoEnv, imFile, origins), Is.EqualTo("https://own.example.com"));

            var none = RevitLinkSettings.Load(Path.Combine(_dir, "none.json"));
            Assert.That(none.ResolveImBaseUrl(n => n == "ISSUE_MANAGER_URL" ? "https://from-env.example.com/" : null, imFile, origins), Is.EqualTo("https://from-env.example.com"));
            Assert.That(none.ResolveImBaseUrl(NoEnv, imFile, origins), Is.EqualTo("https://from-im-file.example.com"));
            Assert.That(none.ResolveImBaseUrl(NoEnv, Path.Combine(_dir, "no-im.json"), origins), Is.EqualTo("https://origin-one.example.com"));
        }

        [Test]
        public void ResolveImBaseUrl_NothingConfigured_ReturnsNull_NeverGuesses()
        {
            var none = RevitLinkSettings.Load(Path.Combine(_dir, "none.json"));
            Assert.That(none.ResolveImBaseUrl(NoEnv, Path.Combine(_dir, "no-im.json"), new string[0]), Is.Null);
            Assert.That(none.ResolveImBaseUrl(NoEnv, null, null), Is.Null);
        }

        [Test]
        public void ResolveImBaseUrl_OnlyHttps_OrLoopbackHttp()
        {
            var plain = RevitLinkSettings.Load(Write("plain.json", "{\"imBaseUrl\":\"http://im.example.com\"}"));
            Assert.Throws<ArgumentException>(() => plain.ResolveImBaseUrl(NoEnv, null, null));

            var local = RevitLinkSettings.Load(Write("local.json", "{\"imBaseUrl\":\"http://127.0.0.1:3704\"}"));
            Assert.That(local.ResolveImBaseUrl(NoEnv, null, null), Is.EqualTo("http://127.0.0.1:3704"));
        }

        [Test]
        public void ResolveImBaseUrl_SkipsInvalidAllowedOrigins()
        {
            var none = RevitLinkSettings.Load(Path.Combine(_dir, "none.json"));
            var origins = new[] { "http://not-local.example.com", "https://ok.example.com" };
            Assert.That(none.ResolveImBaseUrl(NoEnv, null, origins), Is.EqualTo("https://ok.example.com"));
        }
    }

    /// <summary>本功能唯讀的第二道保證（第一道是功能區按鈕的 TransactionMode.ReadOnly）：掃原始碼，不得出現會改模型的 API。</summary>
    [TestFixture]
    public class RevitLinkReadOnlyScanTests
    {
        private static readonly string[] Files =
        {
            "MCP/Core/Commands/CommandExecutor.RevitLink.cs",
            "MCP/Core/Commands/CommandExecutor.RevitLinkDraft.cs",
            "MCP/Commands/RevitLinkCommands.cs",
            "MCP/Core/ViewSync/RevitLinkDraftStore.cs",
            "MCP/Core/ViewSync/RevitLinkSettings.cs",
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "REVIT_MCP_study.sln"))) dir = dir.Parent;
            if (dir == null) Assert.Inconclusive("找不到 repo 根目錄（REVIT_MCP_study.sln）");
            return dir.FullName;
        }

        private static string CodeOnly(string path)
        {
            // 去掉 // 註解（避免說明文字裡寫到禁用字被誤判）；字串裡的 // 不會出現在本功能檔案。
            var lines = File.ReadAllLines(path).Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l.Substring(0, i) : l; });
            return string.Join("\n", lines);
        }

        [Test]
        public void SourceFiles_Exist()
        {
            var root = RepoRoot();
            foreach (var f in Files) Assert.That(File.Exists(Path.Combine(root, f)), Is.True, f);
        }

        [TestCase("new Transaction")]
        [TestCase("TransactionGroup")]
        [TestCase("SubTransaction")]
        [TestCase(".Delete(")]
        [TestCase("viewsync_create_view")]
        [TestCase("viewsync_apply_temp")]
        [TestCase("ViewSyncCreateView")]
        [TestCase("ViewSyncApplyTemp")]
        [TestCase("ViewSyncCreate3D")]
        [TestCase("ViewPlan.Create")]
        [TestCase("ViewSection.Create")]
        [TestCase("set_camera_pose")]
        [TestCase("SetOrientation")]
        [TestCase("ViewTemplateId")]
        public void NoWriteApis_InRevitLinkSources(string forbidden)
        {
            var root = RepoRoot();
            foreach (var f in Files)
                Assert.That(CodeOnly(Path.Combine(root, f)), Does.Not.Contain(forbidden), f + " 含 " + forbidden);
        }

        [Test]
        public void RibbonCommands_AreReadOnlyTransactionMode()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "MCP/Commands/RevitLinkCommands.cs"));
            Assert.That(text, Does.Contain("TransactionMode.ReadOnly"));
            Assert.That(text, Does.Not.Contain("TransactionMode.Manual"));
        }

        [Test]
        public void DraftPut_IsNotAnExecutorSwitchCase()
        {
            var text = CodeOnly(Path.Combine(RepoRoot(), "MCP/Core/CommandExecutor.cs"));
            Assert.That(text, Does.Not.Contain("case \"viewsync_draft_put\""));
            Assert.That(text, Does.Contain("case \"viewsync_open_link\""));
            Assert.That(text, Does.Contain("case \"open_issue_draft\""));
            Assert.That(text, Does.Contain("case \"open_element_issues\""));
        }
    }
}
