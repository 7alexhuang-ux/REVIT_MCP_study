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

        /// <summary>設成 "1" 才會讓 <see cref="FindScript"/> 往上找開發樹的 tools\（正式安裝版預設關閉）。</summary>
        public const string DevToolsEnvironmentVariable = "REVITMCP_DEV_TOOLS";

        public static bool DevToolsEnabled
        {
            get { return string.Equals(Environment.GetEnvironmentVariable(DevToolsEnvironmentVariable), "1", StringComparison.Ordinal); }
        }

        /// <summary>
        /// 腳本搜尋順序：dll 同層 → %APPDATA%\RevitMCP\bridge\node\bundle →（僅開發旗標開啟時）往上找開發樹的 tools\。
        /// 正式安裝版不往上層目錄找：專案資料夾或使用者可寫的上層目錄放一份同名腳本就會被 node 執行。
        /// </summary>
        public static string FindScript(string assemblyDirectory, bool? allowDevTools = null)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(assemblyDirectory)) candidates.Add(Path.Combine(assemblyDirectory, ScriptName));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCP", "bridge", "node", "bundle", ScriptName));
            if (allowDevTools ?? DevToolsEnabled)
            {
                try
                {
                    var dir = string.IsNullOrEmpty(assemblyDirectory) ? null : new DirectoryInfo(assemblyDirectory);
                    for (var depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                    {
                        candidates.Add(Path.Combine(dir.FullName, "tools", ScriptName));
                    }
                }
                catch (Exception) { }
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// 找 node.exe，只回傳「絕對路徑且檔案存在」的結果，絕不使用裸名 "node"：
        /// UseShellExecute=false 時 Windows 會先在目前工作目錄找，專案資料夾放一個 node.exe 就會被執行。
        /// 順序：標準安裝位置 → PATH 內的各目錄。PATH 內相對路徑、空項目、位於目前工作目錄或
        /// <paramref name="untrustedRoots"/>（例如開啟中專案所在的資料夾）之下的目錄一律略過。
        /// </summary>
        public static string FindNode(IEnumerable<string> untrustedRoots = null)
        {
            return FindNode(untrustedRoots, Environment.GetEnvironmentVariable("PATH"), KnownNodeLocations(), CanRun);
        }

        public static string FindNode(IEnumerable<string> untrustedRoots, string pathVariable, IEnumerable<string> knownLocations,
            Func<string, bool> canRun)
        {
            var distrusted = new List<string>();
            try { distrusted.Add(Environment.CurrentDirectory); } catch (Exception) { }
            if (untrustedRoots != null) distrusted.AddRange(untrustedRoots.Where(r => !string.IsNullOrWhiteSpace(r)));

            var candidates = new List<string>();
            foreach (var known in knownLocations ?? Enumerable.Empty<string>()) candidates.Add(known);
            foreach (var entry in (pathVariable ?? string.Empty).Split(Path.PathSeparator))
            {
                var directory = entry.Trim().Trim('"');
                if (!IsFullyQualified(directory)) continue;
                candidates.Add(Path.Combine(directory, "node.exe"));
            }

            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    if (!IsFullyQualified(candidate)) continue;
                    full = Path.GetFullPath(candidate);
                }
                catch (Exception) { continue; }
                if (!string.Equals(Path.GetFileName(full), "node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (distrusted.Any(root => IsUnder(full, root))) continue;
                if (!File.Exists(full)) continue;
                if (canRun(full)) return full;
            }
            return null;
        }

        private static IEnumerable<string> KnownNodeLocations()
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvs", "default", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm", "node.exe")
            };
        }

        /// <summary>完整的絕對路徑（磁碟機代號加反斜線，或 UNC）；排除 "C:foo"（相對於該磁碟機目前目錄）與 "\foo" 這類半相對寫法。</summary>
        private static bool IsFullyQualified(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3) return false;
            if (char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return true;
            return path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        /// <summary>path 是否在 root 資料夾之下（含 root 本身）；不分大小寫，兩邊都先轉成完整路徑。</summary>
        public static bool IsUnder(string path, string root)
        {
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                var fullPath = Path.GetFullPath(path);
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return true; // 無法判斷時當成不可信
            }
        }

        private static bool CanRun(string absoluteNodePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = absoluteNodePath, Arguments = "--version", UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(absoluteNodePath),
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
        public static void Merge(string assemblyDirectory, string reportPath, string outputGlbPath, IEnumerable<string> untrustedRoots = null)
        {
            var node = FindNode(untrustedRoots);
            if (node == null)
                throw new InvalidOperationException("找不到 Node.js。含設計選項的模型需要 Node.js 合併 GLB 分件；請安裝 Node.js，或取消勾選「含設計選項」。");
            var script = FindScript(assemblyDirectory);
            if (script == null)
                throw new InvalidOperationException("找不到 GLB 合併腳本 merge-option-glbs.mjs。請重新執行 scripts/install-addon.ps1 部署，或取消勾選「含設計選項」。");

            var psi = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(outputGlbPath)), // 自己的匯出資料夾，不是專案資料夾
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
