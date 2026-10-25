using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CSharp;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    [TestFixture]
    public class GlbMergeRunnerTests
    {
        [Test]
        public void FindScript_WalksUpToTheDevTreeToolsFolderOnlyWhenDevFlagIsOn()
        {
            var script = GlbMergeRunner.FindScript(AppDomain.CurrentDomain.BaseDirectory, allowDevTools: true);
            Assert.IsNotNull(script, "開發樹的 tools\\merge-option-glbs.mjs 應該找得到");
            StringAssert.EndsWith("merge-option-glbs.mjs", script);
        }

        [Test]
        public void FindScript_DoesNotWalkUpToUserWritableParentFoldersByDefault()
        {
            var deployed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "bridge", "node", "bundle", GlbMergeRunner.ScriptName);
            if (File.Exists(deployed)) Assert.Ignore("這台機器已部署合併腳本。");
            Assert.IsNull(GlbMergeRunner.FindScript(AppDomain.CurrentDomain.BaseDirectory, allowDevTools: false),
                "正式安裝版不往上層目錄找 tools\\（專案資料夾的腳本會被 node 執行）");
        }

        [Test]
        public void DevFlagIsOffUnlessTheEnvironmentVariableIsExactlyOne()
        {
            var old = Environment.GetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, null);
                Assert.IsFalse(GlbMergeRunner.DevToolsEnabled);
                Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, "true");
                Assert.IsFalse(GlbMergeRunner.DevToolsEnabled);
                Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, "1");
                Assert.IsTrue(GlbMergeRunner.DevToolsEnabled);
            }
            finally { Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, old); }
        }

        /// <summary>編譯一個一執行就在自己旁邊留下 executed.marker 的 node.exe（陷阱程式，不做任何其他事）。</summary>
        private static string BuildTrapNode(string directory)
        {
            var path = Path.Combine(directory, "node.exe");
            using (var provider = new CSharpCodeProvider())
            {
                var parameters = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = path };
                var result = provider.CompileAssemblyFromSource(parameters,
                    "using System; using System.IO; static class P { static int Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"executed.marker\"), \"x\"); Console.WriteLine(\"v0.0.0\"); return 0; } }");
                if (result.Errors.HasErrors) Assert.Inconclusive("無法編譯陷阱 node.exe：" + string.Join("; ", result.Errors.Cast<object>()));
            }
            return path;
        }

        [Test]
        public void FindNode_NeverRunsANodeExeThatSitsInTheCurrentWorkingDirectory()
        {
            var dir = TempDir.Create("trap-node");
            var oldCwd = Environment.CurrentDirectory;
            try
            {
                var trap = BuildTrapNode(dir);
                var marker = Path.Combine(dir, "executed.marker");
                Environment.CurrentDirectory = dir;

                // 對照組：舊寫法（裸名 node）在預設的 Windows 設定下會先找目前工作目錄，陷阱程式就會被執行。
                // （有些機器設了 NoDefaultCurrentDirectoryInExePath=1 擋掉這個搜尋；對照組暫時移除它，證明陷阱有效。）
                const string guardVariable = "NoDefaultCurrentDirectoryInExePath";
                var oldGuard = Environment.GetEnvironmentVariable(guardVariable);
                try
                {
                    Environment.SetEnvironmentVariable(guardVariable, null);
                    using (var control = Process.Start(new ProcessStartInfo
                    {
                        FileName = "node", Arguments = "--version", UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    }))
                    {
                        control.WaitForExit(10000);
                    }
                }
                finally { Environment.SetEnvironmentVariable(guardVariable, oldGuard); }
                Assert.IsTrue(File.Exists(marker), "對照組：裸名 node 會先找目前工作目錄（這就是審查指出的洞）");
                File.Delete(marker);

                // 修正後：真正的 FindNode 絕不執行它。
                var found = GlbMergeRunner.FindNode();
                Assert.IsFalse(File.Exists(marker), "FindNode 不得執行工作目錄裡的 node.exe");
                if (found != null) Assert.AreNotEqual(Path.GetFullPath(trap).ToLowerInvariant(), Path.GetFullPath(found).ToLowerInvariant());

                // PATH 裡放相對路徑、"."、工作目錄本身（絕對）：全部略過，canRun 一次都不能被呼叫。
                var probed = new List<string>();
                var path = string.Join(Path.PathSeparator.ToString(), new[] { ".", string.Empty, @"relative\dir", dir, Path.Combine(dir, "sub") });
                var result = GlbMergeRunner.FindNode(null, path, new string[0], p => { probed.Add(p); return true; });
                Assert.IsNull(result);
                Assert.IsEmpty(probed);
                Assert.IsFalse(File.Exists(marker));
            }
            finally
            {
                Environment.CurrentDirectory = oldCwd;
                TempDir.TryDelete(dir);
            }
        }

        [Test]
        public void FindNode_SkipsTheProjectFolderAndAcceptsOnlyAbsoluteExistingPaths()
        {
            var project = TempDir.Create("project-folder");
            var good = TempDir.Create("good-node");
            try
            {
                File.WriteAllText(Path.Combine(project, "node.exe"), "not really node");
                File.WriteAllText(Path.Combine(good, "node.exe"), "pretend node");
                var probed = new List<string>();
                var path = project + Path.PathSeparator + good;
                var found = GlbMergeRunner.FindNode(new[] { project }, path, new[] { Path.Combine(project, "node.exe"), "node.exe", @"C:node.exe" },
                    p => { probed.Add(p); return true; });
                Assert.AreEqual(Path.Combine(good, "node.exe"), found);
                CollectionAssert.AreEqual(new[] { Path.Combine(good, "node.exe") }, probed, "只有可信的絕對路徑會被試跑");

                Assert.IsNull(GlbMergeRunner.FindNode(null, Path.Combine(good, "missing"), new string[0], p => true), "不存在的檔案不回傳");
                Assert.IsTrue(GlbMergeRunner.IsUnder(Path.Combine(project, "a", "b.exe"), project));
                Assert.IsFalse(GlbMergeRunner.IsUnder(project + @"-other\b.exe", project), "名稱前綴相同的兄弟資料夾不算在底下");
            }
            finally { TempDir.TryDelete(project); TempDir.TryDelete(good); }
        }

        [Test]
        public void FindScript_ReturnsNullWhenNothingExists()
        {
            // 只在部署路徑也不存在時成立；若這台機器已部署過就略過
            var deployed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "bridge", "node", "bundle", GlbMergeRunner.ScriptName);
            if (File.Exists(deployed)) Assert.Ignore("這台機器已部署合併腳本。");
            Assert.IsNull(GlbMergeRunner.FindScript(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"))));
        }

        [Test]
        public void Merge_ReportsNodeFailureAsPlainMessageEvenWhenPathsContainSpaces()
        {
            if (GlbMergeRunner.FindNode() == null) Assert.Ignore("找不到 Node.js。");
            var dir = TempDir.Create("merge with spaces");
            var oldDev = Environment.GetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, "1"); // 測試從開發樹找腳本
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    GlbMergeRunner.Merge(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(dir, "missing report.json"), Path.Combine(dir, "out file.glb")));
                StringAssert.StartsWith("合併 GLB 失敗", ex.Message);
                Assert.IsFalse(File.Exists(Path.Combine(dir, "out file.glb")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(GlbMergeRunner.DevToolsEnvironmentVariable, oldDev);
                TempDir.TryDelete(dir);
            }
        }
    }
}
