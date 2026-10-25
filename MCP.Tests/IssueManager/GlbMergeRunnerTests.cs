using System;
using System.IO;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    [TestFixture]
    public class GlbMergeRunnerTests
    {
        [Test]
        public void FindScript_WalksUpToTheDevTreeToolsFolder()
        {
            var script = GlbMergeRunner.FindScript(AppDomain.CurrentDomain.BaseDirectory);
            Assert.IsNotNull(script, "開發樹的 tools\\merge-option-glbs.mjs 應該找得到");
            StringAssert.EndsWith("merge-option-glbs.mjs", script);
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
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    GlbMergeRunner.Merge(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(dir, "missing report.json"), Path.Combine(dir, "out file.glb")));
                StringAssert.StartsWith("合併 GLB 失敗", ex.Message);
                Assert.IsFalse(File.Exists(Path.Combine(dir, "out file.glb")));
            }
            finally { TempDir.TryDelete(dir); }
        }
    }
}
