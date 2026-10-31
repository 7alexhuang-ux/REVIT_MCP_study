#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>
        /// 量測視口外框寬高（昂貴：Revit 的 Viewport.GetBoxOutline 大專案每個要數秒）。只在匯出時、對需要判斷主要視口的視口呼叫，
        /// 且 <see cref="BundleExporter"/> 會分批（每批一次 ExternalEvent）呼叫以便在批次之間更新進度與檢查取消。
        /// 單一視口量不到時該筆 Ok＝false（面積視為 0），不丟例外。
        /// </summary>
        IReadOnlyList<ViewportSize> MeasureViewports(IReadOnlyList<long> viewportElementIds);
        /// <summary>合併單檔 PDF，頁序＝傳入圖紙順序。</summary>
        void ExportPdf(IReadOnlyList<SheetItem> sheetsInOrder, string outputPath);
        /// <summary>匯出 GLB 到 outputGlbPath；includeDesignOptions=false 時只含主模型與各選項集合的主要選項。</summary>
        ModelExportResult ExportModel(bool includeDesignOptions, string workDirectory, string outputGlbPath);
        /// <summary>匯出 named_views.json 到 outputPath，原點必須與 GLB 相同。</summary>
        void ExportNamedViews(IReadOnlyList<string> viewUniqueIds, double[] originOffsetMeters, string workDirectory, string outputPath);
    }

    /// <summary>
    /// 視窗用的進度回報。匯出流程的每個 Revit 步驟各自是一次 ExternalEvent，步驟之間回到訊息迴圈，
    /// 所以視窗照一般 WPF 方式更新與處理「取消」，不再需要在 API 呼叫中途跑巢狀訊息迴圈。
    /// Report 可能在背景執行緒被呼叫，實作要自行切回 UI 執行緒。
    /// </summary>
    public interface IExportProgress
    {
        void Report(string text, int step, int totalSteps);
    }

    public sealed class NullExportProgress : IExportProgress
    {
        public void Report(string text, int step, int totalSteps) { }
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
    /// 每個 Revit 步驟各自經 <see cref="IApiThreadRunner"/> 排成一次 ExternalEvent 執行，步驟之間（在呼叫端執行緒上）
    /// 檢查取消旗標並回報進度；檔案雜湊、manifest 等不需要 Revit API 的工作不佔用 Revit 執行緒。
    /// GLB 匯出本身是單一長呼叫，無法中途取消。
    /// </summary>
    public sealed class BundleExporter
    {
        public const string PdfName = "sheets.pdf";
        public const string GlbName = "model.glb";
        public const string NamedViewsName = "named_views.json";
        public const string ManifestName = "manifest.json";

        /// <summary>量測視口外框時每一批的視口數（每批一次 ExternalEvent，批次之間回到訊息迴圈、更新進度、檢查取消）。</summary>
        public const int MeasureChunkSize = 6;

        private readonly Func<DateTimeOffset> _now;

        public BundleExporter(Func<DateTimeOffset> now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>同步版本（測試與沒有 Revit 的情境）：步驟直接在目前執行緒執行。</summary>
        public ExportedBundle Export(ExportRequest request, IBundleExportSteps steps, string bundleDirectory,
            IExportProgress progress, CancellationToken ct)
        {
            return ExportAsync(request, steps, new InlineApiThreadRunner(), bundleDirectory, progress, ct).GetAwaiter().GetResult();
        }

        public async Task<ExportedBundle> ExportAsync(ExportRequest request, IBundleExportSteps steps, IApiThreadRunner runner,
            string bundleDirectory, IExportProgress progress, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (steps == null) throw new ArgumentNullException("steps");
            if (runner == null) throw new ArgumentNullException("runner");
            progress = progress ?? new NullExportProgress();
            System.IO.Directory.CreateDirectory(bundleDirectory);
            var workDirectory = Path.Combine(bundleDirectory, "work");
            System.IO.Directory.CreateDirectory(workDirectory);

            var doExportPdf = request.Sheets.Count > 0;
            var doExportModel = request.IncludeModel;
            var threeD = request.ThreeDViews.Select(v => v.UniqueId).ToList();
            var doExportNamedViews = doExportModel && threeD.Count > 0;
            var toMeasure = ViewportsNeedingMeasure(request);
            var doMeasure = toMeasure.Count > 0;
            var total = 1 + (doMeasure ? 1 : 0) + (doExportPdf ? 1 : 0) + (doExportModel ? 1 : 0) + (doExportNamedViews ? 1 : 0) + 1;
            var step = 0;
            var warnings = new List<string>();
            var files = new List<BundleFile>();
            var origin = new double[] { 0, 0, 0 };

            ct.ThrowIfCancellationRequested();
            progress.Report("讀取文件資訊", ++step, total);
            var doc = await runner.RunAsync(() => steps.GetDocInfo(), ct).ConfigureAwait(false);

            if (doMeasure)
            {
                // 昂貴資料延後到這裡才讀：只量「勾選且同一張圖紙有兩個以上視口」的視口（只有它們需要面積來決定主要視口），
                // 並分批——每批一次 ExternalEvent，批次之間回到 Revit 訊息迴圈、更新進度、可取消（不再開視窗就卡死）。
                ++step;
                var measured = 0;
                for (var offset = 0; offset < toMeasure.Count; offset += MeasureChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report("讀取視口大小 " + measured + "/" + toMeasure.Count, step, total);
                    var chunk = toMeasure.Skip(offset).Take(MeasureChunkSize).ToList();
                    var ids = chunk.Select(v => v.ViewportElementId).ToList();
                    var sizes = await runner.RunAsync(() => steps.MeasureViewports(ids), ct).ConfigureAwait(false);
                    var byId = (sizes ?? new List<ViewportSize>()).Where(s => s != null).GroupBy(s => s.ViewportElementId).ToDictionary(g => g.Key, g => g.First());
                    foreach (var viewport in chunk)
                    {
                        ViewportSize size;
                        if (byId.TryGetValue(viewport.ViewportElementId, out size) && size.Ok)
                        {
                            viewport.Width = size.Width;
                            viewport.Height = size.Height;
                        }
                        viewport.SizeMeasured = true; // 量不到＝面積 0，主要視口退回清單中的第一個；不重量
                    }
                    measured += chunk.Count;
                }
                progress.Report("讀取視口大小 " + measured + "/" + toMeasure.Count, step, total);
            }

            if (doExportPdf)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report("匯出圖紙 PDF（" + request.Sheets.Count + " 張）", ++step, total);
                var pdfPath = Path.Combine(bundleDirectory, PdfName);
                await runner.RunAsync(() => { steps.ExportPdf(request.Sheets, pdfPath); return 0; }, ct).ConfigureAwait(false);
                RequireHeader(pdfPath, Encoding.ASCII.GetBytes("%PDF-"), "PDF 匯出沒有產生有效的 PDF 檔。");
                var inspection = PdfInspector.Inspect(pdfPath);
                if (inspection.PageObjects.HasValue && inspection.PageObjects.Value != request.Sheets.Count)
                {
                    var message = "PDF 頁數（" + inspection.PageObjects.Value + "）與勾選的圖紙數（" + request.Sheets.Count + "）不同";
                    // 頁數多於圖紙數一定是錯的；少於圖紙數時，若 PDF 把頁面物件壓進物件串流，估計會偏低，只能警告。
                    if (inspection.PageObjects.Value > request.Sheets.Count || inspection.CountIsReliable)
                        throw new InvalidOperationException(message + "，為避免網站上的圖紙和頁面對錯，已中止。請確認 Revit 的 PDF 匯出設定（合併成單一檔案、每張圖紙一頁）後重試。");
                    warnings.Add(message + "（此 PDF 的頁面物件有壓縮，頁數只是估計），頁序對應請人工檢查。");
                }
                files.Add(Describe(BundleFile.Pdf, PdfName, pdfPath));
            }

            if (doExportModel)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report("匯出 3D 模型 GLB（大模型可能需要十幾分鐘，期間無法取消）", ++step, total);
                var glbPath = Path.Combine(bundleDirectory, GlbName);
                var model = await runner.RunAsync(() => steps.ExportModel(request.IncludeDesignOptions, workDirectory, glbPath), ct).ConfigureAwait(false);
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
                ct.ThrowIfCancellationRequested();
                progress.Report("匯出 3D 視角（" + threeD.Count + " 個）", ++step, total);
                var namedPath = Path.Combine(bundleDirectory, NamedViewsName);
                var capturedOrigin = origin;
                await runner.RunAsync(() => { steps.ExportNamedViews(threeD, capturedOrigin, workDirectory, namedPath); return 0; }, ct).ConfigureAwait(false);
                RequireJsonObject(namedPath, "3D 視角匯出沒有產生有效的 JSON 檔。");
                files.Add(Describe(BundleFile.NamedViews, NamedViewsName, namedPath));
            }

            ct.ThrowIfCancellationRequested();
            progress.Report("產生版本包清單 manifest.json", ++step, total);
            var manifest = ManifestBuilder.Build(request, doc, _now(), origin, files, warnings);
            var manifestPath = Path.Combine(bundleDirectory, ManifestName);
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            TryDeleteDirectory(workDirectory); // 匯出的中間檔（分件 GLB 等）不再需要，上傳前先釋放磁碟空間
            ct.ThrowIfCancellationRequested();

            return new ExportedBundle { Directory = bundleDirectory, ManifestPath = manifestPath, Manifest = manifest, Files = files };
        }

        /// <summary>
        /// 需要量測外框的視口：要有 ViewportElementId、還沒量過，且所屬圖紙在請求中有兩個以上視口
        /// （只有一個視口時它必然是主要視口，面積無關）。已去除重複的視口。
        /// </summary>
        internal static List<ViewportItem> ViewportsNeedingMeasure(ExportRequest request)
        {
            var result = new List<ViewportItem>();
            var seen = new HashSet<long>();
            foreach (var sheet in request.Sheets)
            {
                if (sheet.Viewports.Count < 2) continue;
                foreach (var viewport in sheet.Viewports)
                {
                    if (viewport.ViewportElementId == 0 || viewport.SizeMeasured) continue;
                    if (seen.Add(viewport.ViewportElementId)) result.Add(viewport);
                }
            }
            return result;
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

    /// <summary>PDF 頁數估計的結果。</summary>
    public sealed class PdfInspection
    {
        /// <summary>數到的 /Type /Page 物件數；一個都沒數到（頁面物件全壓縮在物件串流裡）時為 null。</summary>
        public int? PageObjects { get; set; }
        /// <summary>檔案內沒有物件串流（/ObjStm）：數到的頁數可信。有物件串流時，部分頁面物件可能數不到，數字只會偏低。</summary>
        public bool CountIsReliable { get; set; }
    }

    /// <summary>
    /// PDF 頁數估計：串流掃描 /Type /Page 物件（每次只讀 1 MB，不把整份 PDF 讀進記憶體；大圖紙集的 PDF 可達數百 MB）。
    /// 壓縮在物件串流裡的 PDF 會數不到或偏低，所以同時回報可信度。
    /// </summary>
    public static class PdfInspector
    {
        private static readonly Regex PageObject = new Regex(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.Compiled);
        private const int ChunkBytes = 1 << 20;
        /// <summary>跨區塊保留的字元數：必須大於 pattern 長度（含兩段之間的空白）。</summary>
        private const int OverlapChars = 128;
        private const string ObjectStreamMarker = "/ObjStm";

        public static int? TryCountPages(string path)
        {
            return Inspect(path).PageObjects;
        }

        public static PdfInspection Inspect(string path)
        {
            try
            {
                var latin1 = Encoding.GetEncoding("ISO-8859-1");
                var buffer = new byte[ChunkBytes];
                var count = 0;
                var sawObjectStream = false;
                var carry = string.Empty;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var text = carry + latin1.GetString(buffer, 0, read);
                        var last = stream.Position >= stream.Length;
                        // 只計入起點在「已決定區」內的比對；尾端 OverlapChars 個字元留到下一輪（那時後面的字元也讀進來了）。
                        var decidedEnd = last ? text.Length : Math.Max(0, text.Length - OverlapChars);
                        foreach (Match match in PageObject.Matches(text))
                        {
                            if (match.Index < decidedEnd) count++;
                        }
                        if (!sawObjectStream && text.IndexOf(ObjectStreamMarker, StringComparison.Ordinal) >= 0) sawObjectStream = true;
                        carry = text.Substring(decidedEnd);
                    }
                }
                return new PdfInspection { PageObjects = count > 0 ? (int?)count : null, CountIsReliable = !sawObjectStream };
            }
            catch (Exception)
            {
                return new PdfInspection { PageObjects = null, CountIsReliable = false };
            }
        }
    }
}
