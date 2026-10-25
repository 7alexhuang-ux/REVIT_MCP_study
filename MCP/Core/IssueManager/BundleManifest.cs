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

        public static JObject Build(ExportRequest request, RevitDocInfo doc, DateTimeOffset exportedAt,
            double[] originOffsetMeters, IEnumerable<BundleFile> files, IEnumerable<string> warnings)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (doc == null) throw new ArgumentNullException("doc");
            if (originOffsetMeters == null || originOffsetMeters.Length != 3 || originOffsetMeters.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("originOffsetMeters 必須是三個有限數字。");

            var viewById = new Dictionary<string, ViewItem>(StringComparer.Ordinal);
            foreach (var view in request.Views)
            {
                if (!ViewKinds.IsKnown(view.Kind)) throw new InvalidOperationException("未知的視圖種類：" + view.Kind);
                if (viewById.ContainsKey(view.UniqueId)) throw new InvalidOperationException("視圖 UniqueId 重複：" + view.UniqueId);
                viewById[view.UniqueId] = view;
            }

            var sheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetIds = new HashSet<string>(StringComparer.Ordinal);
            var sheets = new JArray();
            var page = 0;
            foreach (var sheet in request.Sheets)
            {
                if (string.IsNullOrWhiteSpace(sheet.Number)) throw new InvalidOperationException("圖紙缺少圖紙號：" + sheet.UniqueId);
                if (!sheetNumbers.Add(sheet.Number.Trim())) throw new InvalidOperationException("圖紙號重複：" + sheet.Number);
                if (!sheetIds.Add(sheet.UniqueId)) throw new InvalidOperationException("圖紙 UniqueId 重複：" + sheet.UniqueId);
                page++;

                var included = sheet.Viewports.Where(v => viewById.ContainsKey(v.ViewUniqueId)).ToList();
                var primary = PrimaryViewport.IndexOf(included);
                var viewports = new JArray();
                for (var i = 0; i < included.Count; i++)
                {
                    var view = viewById[included[i].ViewUniqueId];
                    viewports.Add(new JObject
                    {
                        ["viewUniqueId"] = view.UniqueId,
                        ["viewName"] = view.Name,
                        ["viewKind"] = view.Kind,
                        ["isPrimary"] = i == primary
                    });
                }

                sheets.Add(new JObject
                {
                    ["pageNumber"] = page,
                    ["sheetNumber"] = sheet.Number.Trim(),
                    ["title"] = sheet.Title ?? string.Empty,
                    ["uniqueId"] = sheet.UniqueId,
                    ["viewports"] = viewports
                });
            }

            var views = new JArray();
            foreach (var view in request.Views)
            {
                views.Add(new JObject { ["uniqueId"] = view.UniqueId, ["name"] = view.Name, ["kind"] = view.Kind });
            }

            var fileObject = new JObject();
            foreach (var kind in new[] { BundleFile.Pdf, BundleFile.Glb, BundleFile.NamedViews, BundleFile.ViewPositions })
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
                ["revitDoc"] = new JObject { ["title"] = doc.Title ?? string.Empty, ["versionNumber"] = doc.VersionNumber ?? string.Empty },
                ["exportedAt"] = exportedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["originOffsetMeters"] = new JArray(originOffsetMeters[0], originOffsetMeters[1], originOffsetMeters[2])
            };
            if (!string.IsNullOrWhiteSpace(request.SheetSetName)) manifest["sheetSet"] = new JObject { ["name"] = request.SheetSetName };
            manifest["sheets"] = sheets;
            manifest["views"] = views;
            manifest["files"] = fileObject;
            manifest["options"] = new JObject { ["designOptions"] = request.IncludeDesignOptions };
            manifest["warnings"] = new JArray((warnings ?? Enumerable.Empty<string>()).ToArray());
            return manifest;
        }
    }
}
