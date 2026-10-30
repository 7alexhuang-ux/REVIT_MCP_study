using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core.ViewSync
{
    /// <summary>單筆匯出原點紀錄：原點屬於「這份 Revit 文件」，不是「最近一次匯出的資料夾」。</summary>
    public sealed class ExportOriginEntry
    {
        public string DocumentPath { get; set; }
        public string DocumentGuid { get; set; }
        public string Title { get; set; }
        public double[] OriginOffsetMeters { get; set; }
        public string OutputDir { get; set; }
        public string ExportedAt { get; set; }

        public Vec3 Origin => new Vec3(OriginOffsetMeters[0], OriginOffsetMeters[1], OriginOffsetMeters[2]);
    }

    /// <summary>
    /// 本機登錄檔 %APPDATA%\RevitMCP\export_origins.json：每份文件（CreationGUID＋路徑為鍵）保留最近一筆匯出原點。
    /// 不依賴 Revit API，可單元測試；讀寫失敗一律吞掉（登錄是輔助，不可讓匯出失敗）。
    /// </summary>
    public static class ExportOriginRegistry
    {
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "export_origins.json");

        public static bool IsValid(ExportOriginEntry e) =>
            e != null && e.OriginOffsetMeters != null && e.OriginOffsetMeters.Length == 3
            && e.OriginOffsetMeters.All(v => !double.IsNaN(v) && !double.IsInfinity(v))
            && (!string.IsNullOrWhiteSpace(e.DocumentGuid) || !string.IsNullOrWhiteSpace(e.DocumentPath));

        private static string Norm(string s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>是否同一份文件：兩邊 GUID 都有值時以 GUID 為準，否則比路徑（不分大小寫）。</summary>
        public static bool SameDocument(string guidA, string pathA, string guidB, string pathB)
        {
            if (!string.IsNullOrWhiteSpace(guidA) && !string.IsNullOrWhiteSpace(guidB))
                return Norm(guidA) == Norm(guidB);
            return !string.IsNullOrWhiteSpace(pathA) && Norm(pathA) == Norm(pathB);
        }

        public static List<ExportOriginEntry> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<ExportOriginEntry>();
                var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var list = root["entries"]?.ToObject<List<ExportOriginEntry>>() ?? new List<ExportOriginEntry>();
                return list.Where(IsValid).ToList();
            }
            catch (Exception)
            {
                return new List<ExportOriginEntry>();
            }
        }

        /// <summary>寫入／取代同一份文件的紀錄（保留每份文件最近一筆）。成功回 true。</summary>
        public static bool Upsert(string path, ExportOriginEntry entry)
        {
            try
            {
                if (!IsValid(entry)) return false;
                if (string.IsNullOrWhiteSpace(entry.ExportedAt)) entry.ExportedAt = DateTime.UtcNow.ToString("o");
                var list = Load(path);
                list.RemoveAll(e => SameDocument(e.DocumentGuid, e.DocumentPath, entry.DocumentGuid, entry.DocumentPath));
                list.Add(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var root = new JObject { ["version"] = 1, ["entries"] = JArray.FromObject(list) };
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ExportOriginEntry Find(string path, string documentGuid, string documentPath)
        {
            return Load(path)
                .Where(e => SameDocument(e.DocumentGuid, e.DocumentPath, documentGuid, documentPath))
                .OrderByDescending(e => e.ExportedAt ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        /// <summary>
        /// 核對用原點：先找登錄檔中這份文件的紀錄（權威）；找不到才退回 fallback（預設資料夾 camera.json，只當警告用）。
        /// </summary>
        public static ResolvedOrigin Resolve(string registryPath, string documentGuid, string documentPath,
            Func<(Vec3? origin, string source)> fallback)
        {
            var hit = Find(registryPath, documentGuid, documentPath);
            if (hit != null)
                return new ResolvedOrigin { Origin = hit.Origin, Source = registryPath + " (" + hit.OutputDir + ")", FromRegistry = true };
            Vec3? fbOrigin = null;
            string fbSource = null;
            if (fallback != null)
            {
                var fb = fallback();
                fbOrigin = fb.origin;
                fbSource = fb.source;
            }
            return new ResolvedOrigin { Origin = fbOrigin, Source = fbSource, FromRegistry = false };
        }
    }

    public sealed class ResolvedOrigin
    {
        public Vec3? Origin { get; set; }
        public string Source { get; set; }
        /// <summary>true＝來自這份文件自己的匯出紀錄；false＝退回的預設資料夾 camera.json（可能是別的案）。</summary>
        public bool FromRegistry { get; set; }
    }
}
