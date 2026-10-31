#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>版本包裡的一個檔案：路徑、大小、SHA-256（小寫十六進位）。</summary>
    public sealed class BundleFile
    {
        public const string Pdf = "pdf";
        public const string Glb = "glb";
        public const string NamedViews = "namedViews";
        public const string ViewPositions = "viewPositions";

        public string Kind { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class RevitDocInfo
    {
        public string Title { get; set; }
        public string VersionNumber { get; set; }
    }

    /// <summary>
    /// 產生 manifest.json（schemaVersion 1，合約見 docs/schemas/bundle-manifest.schema.json 與 docs/ISSUE_MANAGER_UPLOAD.md）。
    /// 圖號直接取自 Revit 圖紙號（不經圖框辨識）；視圖與圖紙以 Revit UniqueId 對應。
    /// 欄位順序固定，方便比對 golden fixture。
    /// </summary>
    public static class ManifestBuilder
    {
        public const int SchemaVersion = 1;

        // 網站端權威 schema（docs/spec/schemas/bundle-manifest.schema.json）的上限；超過會被 finalize 拒絕（400 invalid_manifest）。
        public const int MaxSheets = 1000;
        public const int MaxViewportsPerSheet = 200;
        public const int MaxViews = 5000;
        public const int MaxWarnings = 200;
        public const int MaxWarningChars = 500;
        public const int MaxSheetNumberChars = 100;
        public const int MaxTitleChars = 500;
        public const int MaxUniqueIdChars = 200;
        public const int MaxViewNameChars = 300;
        public const int MaxDocTitleChars = 300;
        public const int MaxVersionNumberChars = 100;
        public const int MaxSheetSetNameChars = 300;
        public const double MaxOriginAbs = 1e9;

        /// <summary>超過長度就截斷（結尾補「…」）。只用在顯示用文字（圖名、視圖名、警告）。</summary>
        internal static string Clip(string text, int max)
        {
            if (text == null) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        private static string RequireLength(string text, int max, string label)
        {
            if (text != null && text.Length > max)
                throw new InvalidOperationException(label + "太長（超過 " + max + " 個字元），Issue Manager 無法接受：" + Clip(text, 40));
            return text;
        }

        public static JObject Build(ExportRequest request, RevitDocInfo doc, DateTimeOffset exportedAt,
            double[] originOffsetMeters, IEnumerable<BundleFile> files, IEnumerable<string> warnings)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (doc == null) throw new ArgumentNullException("doc");
            if (originOffsetMeters == null || originOffsetMeters.Length != 3 || originOffsetMeters.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("originOffsetMeters 必須是三個有限數字。");
            if (originOffsetMeters.Any(v => Math.Abs(v) > MaxOriginAbs))
                throw new InvalidOperationException("模型座標原點的偏移量過大（超過 10 億公尺），Issue Manager 無法接受。");
            if (request.Sheets.Count > MaxSheets)
                throw new InvalidOperationException("勾選的圖紙太多（" + request.Sheets.Count + " 張）：Issue Manager 單一版本最多 " + MaxSheets + " 張，請分批上傳。");
            if (request.Views.Count > MaxViews)
                throw new InvalidOperationException("勾選的視圖太多（" + request.Views.Count + " 個）：Issue Manager 單一版本最多 " + MaxViews + " 個，請減少勾選。");

            var viewById = new Dictionary<string, ViewItem>(StringComparer.Ordinal);
            foreach (var view in request.Views)
            {
                if (!ViewKinds.IsKnown(view.Kind)) throw new InvalidOperationException("未知的視圖種類：" + view.Kind);
                if (viewById.ContainsKey(view.UniqueId)) throw new InvalidOperationException("視圖 UniqueId 重複：" + view.UniqueId);
                RequireLength(view.UniqueId, MaxUniqueIdChars, "視圖識別碼");
                viewById[view.UniqueId] = view;
            }

            var sheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetIds = new HashSet<string>(StringComparer.Ordinal);
            var sheets = new JArray();
            var page = 0;
            foreach (var sheet in request.Sheets)
            {
                if (string.IsNullOrWhiteSpace(sheet.Number)) throw new InvalidOperationException("圖紙缺少圖紙號：" + sheet.UniqueId);
                RequireLength(sheet.Number.Trim(), MaxSheetNumberChars, "圖紙號");
                RequireLength(sheet.UniqueId, MaxUniqueIdChars, "圖紙識別碼");
                if (!sheetNumbers.Add(sheet.Number.Trim())) throw new InvalidOperationException("圖紙號重複：" + sheet.Number);
                if (!sheetIds.Add(sheet.UniqueId)) throw new InvalidOperationException("圖紙 UniqueId 重複：" + sheet.UniqueId);
                page++;

                var included = sheet.Viewports.Where(v => viewById.ContainsKey(v.ViewUniqueId)).ToList();
                if (included.Count > MaxViewportsPerSheet)
                    throw new InvalidOperationException("圖紙 " + sheet.Number.Trim() + " 的視口太多（" + included.Count + " 個）：Issue Manager 單張圖紙最多 " + MaxViewportsPerSheet + " 個。");
                var primary = PrimaryViewport.IndexOf(included);
                var viewports = new JArray();
                for (var i = 0; i < included.Count; i++)
                {
                    var view = viewById[included[i].ViewUniqueId];
                    viewports.Add(new JObject
                    {
                        ["viewUniqueId"] = view.UniqueId,
                        ["viewName"] = Clip(view.Name, MaxViewNameChars),
                        ["viewKind"] = view.Kind,
                        ["isPrimary"] = i == primary
                    });
                }

                sheets.Add(new JObject
                {
                    ["pageNumber"] = page,
                    ["sheetNumber"] = sheet.Number.Trim(),
                    ["title"] = Clip(sheet.Title, MaxTitleChars),
                    ["uniqueId"] = sheet.UniqueId,
                    ["viewports"] = viewports
                });
            }

            var views = new JArray();
            foreach (var view in request.Views)
            {
                views.Add(new JObject { ["uniqueId"] = view.UniqueId, ["name"] = Clip(view.Name, MaxViewNameChars), ["kind"] = view.Kind });
            }

            var fileObject = new JObject();
            // viewPositions 屬 V5：網站端 4A 不核發也不收（manifest 帶了會被忽略並回 warning），所以這裡不輸出。
            foreach (var kind in new[] { BundleFile.Pdf, BundleFile.Glb, BundleFile.NamedViews })
            {
                var file = (files ?? Enumerable.Empty<BundleFile>()).FirstOrDefault(f => f.Kind == kind);
                if (file == null) continue; // 沒勾或沒有該類內容：省略＝缺，版本照常建立
                fileObject[kind] = new JObject
                {
                    ["name"] = file.Name,
                    ["bytes"] = file.Bytes,
                    ["sha256"] = file.Sha256
                };
            }

            var manifest = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["revitDoc"] = new JObject { ["title"] = Clip(doc.Title, MaxDocTitleChars), ["versionNumber"] = Clip(doc.VersionNumber, MaxVersionNumberChars) },
                ["exportedAt"] = exportedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["originOffsetMeters"] = new JArray(originOffsetMeters[0], originOffsetMeters[1], originOffsetMeters[2])
            };
            if (!string.IsNullOrWhiteSpace(request.SheetSetName)) manifest["sheetSet"] = new JObject { ["name"] = Clip(request.SheetSetName, MaxSheetSetNameChars) };
            manifest["sheets"] = sheets;
            manifest["views"] = views;
            manifest["files"] = fileObject;
            manifest["options"] = new JObject { ["designOptions"] = request.IncludeDesignOptions };
            manifest["warnings"] = new JArray(LimitWarnings(warnings).Cast<object>().ToArray());
            return manifest;
        }

        /// <summary>警告最多 200 項、每項最多 500 字（網站端 schema 的上限）；超過的以一項「另有 N 則…已省略」收尾，不丟例外也不讓整個版本被拒。</summary>
        internal static List<string> LimitWarnings(IEnumerable<string> warnings)
        {
            var all = (warnings ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            if (all.Count <= MaxWarnings) return all.Select(w => Clip(w, MaxWarningChars)).ToList();
            var kept = all.Take(MaxWarnings - 1).Select(w => Clip(w, MaxWarningChars)).ToList();
            kept.Add("另有 " + (all.Count - kept.Count) + " 則警告已省略（超過 Issue Manager 的上限）。");
            return kept;
        }
    }
}
