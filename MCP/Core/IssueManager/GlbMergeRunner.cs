#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 把 export_design_options 的「主模型＋各選項」GLB 分件合併成單一 GLB：
    /// 呼叫 repo 既有的 tools/merge-option-glbs.mjs（需要本機安裝 Node.js；腳本由 install-addon.ps1 部署到
    /// %APPDATA%\RevitMCP\bridge\node\bundle\）。沒有選項分件時不需要合併，由呼叫端直接用 main_model.glb。
    /// </summary>
    public static class GlbMergeRunner
    {
        public const string ScriptName = "merge-option-glbs.mjs";
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

        /// <summary>腳本搜尋順序：dll 同層 → %APPDATA%\RevitMCP\bridge\node\bundle → 往上找開發樹的 tools\。</summary>
        public static string FindScript(string assemblyDirectory)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(assemblyDirectory)) candidates.Add(Path.Combine(assemblyDirectory, ScriptName));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCP", "bridge", "node", "bundle", ScriptName));
            try
            {
                var dir = string.IsNullOrEmpty(assemblyDirectory) ? null : new DirectoryInfo(assemblyDirectory);
                for (var depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                {
                    candidates.Add(Path.Combine(dir.FullName, "tools", ScriptName));
                }
            }
            catch (Exception) { }
            return candidates.FirstOrDefault(File.Exists);
        }

        public static string FindNode()
        {
            if (CanRun("node")) return "node";
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvs", "default", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm", "node.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static bool CanRun(string fileName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName, Arguments = "--version", UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    return process != null && process.WaitForExit(5000) && process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>合併 reportPath（design_options_export.json）指向的分件到 outputGlbPath。失敗丟 InvalidOperationException（白話）。</summary>
        public static void Merge(string assemblyDirectory, string reportPath, string outputGlbPath)
        {
            var node = FindNode();
            if (node == null)
                throw new InvalidOperationException("找不到 Node.js。含設計選項的模型需要 Node.js 合併 GLB 分件；請安裝 Node.js，或取消勾選「含設計選項」。");
            var script = FindScript(assemblyDirectory);
            if (script == null)
                throw new InvalidOperationException("找不到 GLB 合併腳本 merge-option-glbs.mjs。請重新執行 scripts/install-addon.ps1 部署，或取消勾選「含設計選項」。");

            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = Quote(script) + " " + Quote(reportPath) + " " + Quote(outputGlbPath),
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("無法啟動 Node.js 來合併 GLB。");
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (s, e) => { if (e.Data != null && stderr.Length < 4000) stderr.AppendLine(e.Data); };
                process.OutputDataReceived += (s, e) => { };
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("合併 GLB 逾時（超過 " + Timeout.TotalMinutes + " 分鐘），已中止。");
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("合併 GLB 失敗：" + stderr.ToString().Trim());
            }
            if (!File.Exists(outputGlbPath)) throw new InvalidOperationException("合併 GLB 沒有產生輸出檔。");
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
