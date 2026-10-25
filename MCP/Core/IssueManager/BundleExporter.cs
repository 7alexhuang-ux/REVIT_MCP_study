#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    public sealed class ModelExportResult
    {
        /// <summary>GLB 使用的共用原點（命名視圖必須用同一組）。</summary>
        public double[] OriginOffsetMeters { get; set; } = new double[] { 0, 0, 0 };
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 匯出步驟（Revit 實作在 RevitBundleExportSteps；測試用假實作）。
    /// 全部在 Revit API 執行緒呼叫，失敗丟例外（訊息要是使用者看得懂的白話）。
    /// </summary>
    public interface IBundleExportSteps
    {
        RevitDocInfo GetDocInfo();
        /// <summary>合併單檔 PDF，頁序＝傳入圖紙順序。</summary>
        void ExportPdf(IReadOnlyList<SheetItem> sheetsInOrder, string outputPath);
        /// <summary>匯出 GLB 到 outputGlbPath；includeDesignOptions=false 時只含主模型與各選項集合的主要選項。</summary>
        ModelExportResult ExportModel(bool includeDesignOptions, string workDirectory, string outputGlbPath);
        /// <summary>匯出 named_views.json 到 outputPath，原點必須與 GLB 相同。</summary>
        void ExportNamedViews(IReadOnlyList<string> viewUniqueIds, double[] originOffsetMeters, string workDirectory, string outputPath);
    }

    /// <summary>視窗用的進度回報。Pump 讓視窗在步驟之間重繪、處理「取消」按鈕（Revit 單執行緒，匯出期間視窗只能靠這個更新）。</summary>
    public interface IExportProgress
    {
        void Report(string text, int step, int totalSteps);
        void Pump();
    }

    public sealed class NullExportProgress : IExportProgress
    {
        public void Report(string text, int step, int totalSteps) { }
        public void Pump() { }
    }

    public sealed class ExportedBundle
    {
        public string Directory { get; set; }
        public string ManifestPath { get; set; }
        public JObject Manifest { get; set; }
        public List<BundleFile> Files { get; set; } = new List<BundleFile>();
        public long TotalBytes { get { return Files.Sum(f => f.Bytes); } }
    }

    /// <summary>
    /// 匯出流程步驟 2～5（revit-addin-export.md §3）：PDF → GLB → 命名視圖 → manifest。
    /// 每個步驟之間檢查取消旗標並讓視窗處理訊息；GLB 匯出本身是單一長呼叫，無法中途取消。
    /// </summary>
    public sealed class BundleExporter
    {
        public const string PdfName = "sheets.pdf";
        public const string GlbName = "model.glb";
        public const string NamedViewsName = "named_views.json";
        public const string ManifestName = "manifest.json";

        private readonly Func<DateTimeOffset> _now;

        public BundleExporter(Func<DateTimeOffset> now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public ExportedBundle Export(ExportRequest request, IBundleExportSteps steps, string bundleDirectory,
            IExportProgress progress, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (steps == null) throw new ArgumentNullException("steps");
            progress = progress ?? new NullExportProgress();
            System.IO.Directory.CreateDirectory(bundleDirectory);
            var workDirectory = Path.Combine(bundleDirectory, "work");
            System.IO.Directory.CreateDirectory(workDirectory);

            var doExportPdf = request.Sheets.Count > 0;
            var doExportModel = request.IncludeModel;
            var threeD = request.ThreeDViews.Select(v => v.UniqueId).ToList();
            var doExportNamedViews = doExportModel && threeD.Count > 0;
            var total = 1 + (doExportPdf ? 1 : 0) + (doExportModel ? 1 : 0) + (doExportNamedViews ? 1 : 0) + 1;
            var step = 0;
            var warnings = new List<string>();
            var files = new List<BundleFile>();
            var origin = new double[] { 0, 0, 0 };

            Checkpoint(progress, ct);
            progress.Report("讀取文件資訊", ++step, total);
            var doc = steps.GetDocInfo();

            if (doExportPdf)
            {
                Checkpoint(progress, ct);
                progress.Report("匯出圖紙 PDF（" + request.Sheets.Count + " 張）", ++step, total);
                var pdfPath = Path.Combine(bundleDirectory, PdfName);
                steps.ExportPdf(request.Sheets, pdfPath);
                RequireHeader(pdfPath, Encoding.ASCII.GetBytes("%PDF-"), "PDF 匯出沒有產生有效的 PDF 檔。");
                var pages = PdfInspector.TryCountPages(pdfPath);
                if (pages.HasValue && pages.Value != request.Sheets.Count)
                    warnings.Add("PDF 頁數（" + pages.Value + "）與勾選的圖紙數（" + request.Sheets.Count + "）不同，頁序對應請人工檢查。");
                files.Add(Describe(BundleFile.Pdf, PdfName, pdfPath));
            }

            if (doExportModel)
            {
                Checkpoint(progress, ct);
                progress.Report("匯出 3D 模型 GLB（大模型可能需要十幾分鐘，期間無法取消）", ++step, total);
                var glbPath = Path.Combine(bundleDirectory, GlbName);
                var model = steps.ExportModel(request.IncludeDesignOptions, workDirectory, glbPath);
                RequireHeader(glbPath, Encoding.ASCII.GetBytes("glTF"), "模型匯出沒有產生有效的 GLB 檔。");
                if (model.OriginOffsetMeters == null || model.OriginOffsetMeters.Length != 3)
                    throw new InvalidOperationException("模型匯出沒有回報共用原點。");
                origin = model.OriginOffsetMeters;
                warnings.AddRange(model.Warnings ?? new List<string>());
                files.Add(Describe(BundleFile.Glb, GlbName, glbPath));
            }
            else if (request.ThreeDViews.Any())
            {
                warnings.Add("沒有匯出 3D 模型，所以略過 3D 視角資料（視圖仍列在清單中）。");
            }

            if (doExportNamedViews)
            {
                Checkpoint(progress, ct);
                progress.Report("匯出 3D 視角（" + threeD.Count + " 個）", ++step, total);
                var namedPath = Path.Combine(bundleDirectory, NamedViewsName);
                steps.ExportNamedViews(threeD, origin, workDirectory, namedPath);
                RequireJsonObject(namedPath, "3D 視角匯出沒有產生有效的 JSON 檔。");
                files.Add(Describe(BundleFile.NamedViews, NamedViewsName, namedPath));
            }

            Checkpoint(progress, ct);
            progress.Report("產生版本包清單 manifest.json", ++step, total);
            var manifest = ManifestBuilder.Build(request, doc, _now(), origin, files, warnings);
            var manifestPath = Path.Combine(bundleDirectory, ManifestName);
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            TryDeleteDirectory(workDirectory); // 匯出的中間檔（分件 GLB 等）不再需要，上傳前先釋放磁碟空間
            Checkpoint(progress, ct);

            return new ExportedBundle { Directory = bundleDirectory, ManifestPath = manifestPath, Manifest = manifest, Files = files };
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Checkpoint(IExportProgress progress, CancellationToken ct)
        {
            progress.Pump();
            ct.ThrowIfCancellationRequested();
        }

        public static BundleFile Describe(string kind, string name, string path)
        {
            var info = new FileInfo(path);
            return new BundleFile { Kind = kind, Name = name, Path = path, Bytes = info.Length, Sha256 = Sha256Hex(path) };
        }

        public static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private static void RequireHeader(string path, byte[] magic, string message)
        {
            if (!File.Exists(path)) throw new InvalidOperationException(message);
            var buffer = new byte[magic.Length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read != magic.Length || !buffer.SequenceEqual(magic)) throw new InvalidOperationException(message);
            }
        }

        private static void RequireJsonObject(string path, string message)
        {
            if (!File.Exists(path)) throw new InvalidOperationException(message);
            try
            {
                JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    /// <summary>PDF 頁數估計（只用來提醒，不當作判斷依據）：數 /Type /Page 物件。壓縮在物件串流裡的 PDF 會數不到，回 null。</summary>
    public static class PdfInspector
    {
        private static readonly Regex PageObject = new Regex(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.Compiled);

        public static int? TryCountPages(string path)
        {
            try
            {
                var text = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(path));
                var count = PageObject.Matches(text).Count;
                return count > 0 ? (int?)count : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
