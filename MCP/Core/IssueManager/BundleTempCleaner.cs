#nullable disable
using System;
using System.IO;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 清除舊的上傳暫存（%LOCALAPPDATA%\RevitMCP\issue-manager\bundles\&lt;時間&gt;\）：取消、失敗、直接關掉視窗或 Revit 當掉
    /// 都可能留下很大的 PDF／GLB。外掛啟動時刪掉超過 <see cref="DefaultMaxAgeDays"/> 天沒動過的子資料夾。
    /// 只刪 root 的直接子資料夾，不碰 root 以外的任何東西；任何錯誤都吞掉（清不掉就下次再清）。
    /// </summary>
    public static class BundleTempCleaner
    {
        /// <summary>暫存保留天數（可調整的常數）。</summary>
        public const int DefaultMaxAgeDays = 3;

        public static string DefaultRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP", "issue-manager", "bundles");
        }

        /// <summary>回傳刪除的資料夾數。</summary>
        public static int CleanOld(string root, TimeSpan maxAge, DateTimeOffset now, Action<string> log = null)
        {
            var deleted = 0;
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
                foreach (var directory in Directory.GetDirectories(root))
                {
                    try
                    {
                        var info = new DirectoryInfo(directory);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 不跟隨連結
                        var lastTouched = LastTouched(info);
                        if (now - lastTouched <= maxAge) continue;
                        Directory.Delete(directory, true);
                        deleted++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception) { }
            if (deleted > 0 && log != null) log("已清除 " + deleted + " 個超過 " + maxAge.TotalDays + " 天的舊上傳暫存。");
            return deleted;
        }

        /// <summary>資料夾本身與其直接內容中最新的時間（上傳中的檔案會更新它，不會被誤刪）。</summary>
        private static DateTimeOffset LastTouched(DirectoryInfo directory)
        {
            var latest = new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero);
            try
            {
                foreach (var entry in directory.GetFileSystemInfos())
                {
                    var time = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                    if (time > latest) latest = time;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return latest;
        }
    }
}
