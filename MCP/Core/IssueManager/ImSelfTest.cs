#if DEBUG
// N1（imrevitaddinr）：自測鉤子只存在於開發建置（Debug.R*）；Release 建置不含，避免隨產品 DLL 出貨。
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 實機自測鉤子（開發／驗收用，D-804 備註）：只有使用者預先建立標記檔 <c>%USERPROFILE%\.revit-mcp\im-selftest.json</c> 才會啟用
    /// （與 autostart 標記同一個 opt-in 模型；外掛自己不會建立它）。第一次 Idling 時讀標記、改名為 .done（不重複執行），
    /// 在不點畫面的情況下跑「真實 Revit 讀值＋真實匯出＋真實上傳流程」（UploadFlow 與按鈕相同），結果寫進標記指定的結果檔。
    /// 不經 MCP、不新增 MCP 工具。標記內容（含短效 refresh 權杖）只存本機、跑完即改名；權杖不進日誌與結果檔。
    /// 標記欄位：baseUrl、refreshToken、userId、email、projectId、stage、description、openPath（可省略）、maxSheets（預設 6）、
    /// includeModel（預設 true）、includeParams（預設 true）、evidenceDir、resultPath。
    /// </summary>
    internal static class ImSelfTest
    {
        public static string MarkerPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-mcp", "im-selftest.json");
        }

        private static System.Threading.Timer _timer;
        private static int _busy;

        /// <summary>
        /// 啟動時呼叫（Revit API 執行緒）：只在開發機有 <c>.revit-mcp</c> 資料夾時，開一個背景計時器每 3 秒看一次標記檔；
        /// 有標記就把「讀標記並開始」排進 ExternalEvent 佇列（Revit 閒置時執行），所以重複驗收不必重開 Revit，
        /// 也不依賴 Idling 事件（Idling 只在有使用者操作後才會觸發）。沒有標記檔時每 3 秒只做一次檔案存在檢查。
        /// </summary>
        public static void RegisterIfRequested(UIControlledApplication application)
        {
            try
            {
                if (!Directory.Exists(Path.GetDirectoryName(MarkerPath()))) return;
                var ensureCreated = ExternalEventManager.Instance; // 必須在 API 執行緒建立 ExternalEvent
                _timer = new System.Threading.Timer(state => Poll(), null, 3000, 3000);
            }
            catch (Exception) { }
        }

        private static void Poll()
        {
            try
            {
                if (!File.Exists(MarkerPath())) return;
                if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
                JObject config;
                try
                {
                    config = JObject.Parse(File.ReadAllText(MarkerPath(), Encoding.UTF8));
                    var done = MarkerPath() + ".done";
                    if (File.Exists(done)) File.Delete(done);
                    File.Move(MarkerPath(), done);
                }
                catch (Exception ex)
                {
                    Logger.Info("[IssueManager] 實機自測標記讀取失敗：" + ex.GetType().Name);
                    System.Threading.Interlocked.Exchange(ref _busy, 0);
                    return;
                }
                var resultPath = (string)config["resultPath"];
                var request = ExternalEventManager.Instance.TryExecuteCommand(uiApp =>
                {
                    try
                    {
                        Start(uiApp, config, resultPath);
                    }
                    catch (Exception ex)
                    {
                        WriteResult(resultPath, new JObject { ["ok"] = false, ["stage"] = "start", ["error"] = ex.GetType().Name + "：" + ex.Message });
                        System.Threading.Interlocked.Exchange(ref _busy, 0);
                    }
                });
                Logger.Info("[IssueManager] 實機自測已排入 ExternalEvent：" + request);
            }
            catch (Exception)
            {
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static void Start(UIApplication uiApp, JObject config, string resultPath)
        {
            var openPath = (string)config["openPath"];
            if (!string.IsNullOrEmpty(openPath)) uiApp.OpenAndActivateDocument(openPath);
            var doc = uiApp.ActiveUIDocument != null ? uiApp.ActiveUIDocument.Document : null;
            if (doc == null) throw new InvalidOperationException("沒有開啟的專案文件。");

            var evidence = (string)config["evidenceDir"];
            Directory.CreateDirectory(evidence);
            var snapshot = RevitSelectionCollector.Collect(doc, message => Logger.Info("[IssueManager] " + message));
            var maxSheets = config["maxSheets"] != null ? (int)config["maxSheets"] : 6;
            var request = new ExportRequest
            {
                ProjectId = (string)config["projectId"], Stage = (string)config["stage"] ?? "SD", Description = (string)config["description"] ?? "實機自測",
                IncludeModel = config["includeModel"] == null || (bool)config["includeModel"], IncludeDesignOptions = false,
                IncludeParams = config["includeParams"] == null || (bool)config["includeParams"]
            };
            var views = new Dictionary<string, ViewItem>(StringComparer.Ordinal);
            foreach (var view in snapshot.Views) views[view.UniqueId] = view;
            foreach (var sheet in snapshot.Sheets.OrderBy(s => s.Number, NaturalComparer.Instance).Take(maxSheets))
            {
                var picked = new SheetItem { UniqueId = sheet.UniqueId, ElementId = sheet.ElementId, Number = sheet.Number, Title = sheet.Title };
                foreach (var viewport in sheet.Viewports.Where(v => views.ContainsKey(v.ViewUniqueId))) picked.Viewports.Add(viewport);
                request.Sheets.Add(picked);
            }
            var wanted = new HashSet<string>(request.Sheets.SelectMany(s => s.Viewports.Select(v => v.ViewUniqueId)), StringComparer.Ordinal);
            foreach (var view in snapshot.Views.Where(v => wanted.Contains(v.UniqueId) || (v.Kind == ViewKinds.ThreeD && request.Views.Count < 40))) request.Views.Add(view);

            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            var client = ClientInfo.Create(Environment.MachineName + " · Revit " + uiApp.Application.VersionNumber + " · selftest", version);
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var uploadHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
            var finalizeHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = AddinApiClient.FinalizeTimeout };
            var baseUrl = (string)config["baseUrl"];
            var vault = new MemoryVault(new StoredCredentials
            {
                BaseUrl = baseUrl, RefreshToken = (string)config["refreshToken"], RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                UserId = (string)config["userId"], UserEmail = (string)config["email"], UserName = "selftest", UserRole = "admin", AccountRole = "admin"
            });
            var auth = new AddinAuthService(http, vault, baseUrl, client, log: message => Logger.Info("[IssueManager] " + message));
            if (!auth.TryRestore()) throw new InvalidOperationException("自測權杖還原失敗。");
            var api = new AddinApiClient(http, auth, client, uploadHttp, finalizeHttp);
            var steps = new EvidenceSteps(new RevitBundleExportSteps(uiApp, snapshot), evidence, (string)config["glbOverride"]);
            var flow = new UploadFlow(api, steps, new RevitApiThreadRunner(), Path.Combine(evidence, "bundles")) { Log = message => Logger.Info("[IssueManager] " + message) };
            var started = DateTimeOffset.UtcNow;
            var stages = new List<string>();

            // 不能在 Idling 執行緒上等：匯出步驟要靠 ExternalEvent 回到這條執行緒，所以整個流程丟到背景。
            Task.Run(async () =>
            {
                try
                {
                    var result = await flow.RunAsync(request, new SelfTestProgress(stages), new SelfTestFlowProgress(stages), CancellationToken.None).ConfigureAwait(false);
                    WriteResult(resultPath, new JObject
                    {
                        ["ok"] = true, ["version"] = result.Version, ["setId"] = result.SetId, ["url"] = result.Url,
                        ["warnings"] = new JArray(result.Warnings.Cast<object>().ToArray()),
                        ["sheets"] = request.Sheets.Count, ["views"] = request.Views.Count, ["positionedViews"] = request.PositionedViews.Count(),
                        ["includeModel"] = request.IncludeModel, ["includeParams"] = request.IncludeParams,
                        ["seconds"] = (DateTimeOffset.UtcNow - started).TotalSeconds, ["stages"] = new JArray(stages.Cast<object>().ToArray()),
                        ["evidence"] = new JArray(steps.Copied.Cast<object>().ToArray())
                    });
                }
                catch (Exception ex)
                {
                    WriteResult(resultPath, new JObject
                    {
                        ["ok"] = false, ["stage"] = "flow", ["error"] = ex.GetType().Name + "：" + ex.Message,
                        ["seconds"] = (DateTimeOffset.UtcNow - started).TotalSeconds, ["stages"] = new JArray(stages.Cast<object>().ToArray())
                    });
                }
                finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
            });
        }

        private static void WriteResult(string path, JObject result)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, result.ToString(Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private sealed class MemoryVault : ITokenVault
        {
            private StoredCredentials _credentials;
            public MemoryVault(StoredCredentials credentials) { _credentials = credentials; }
            public StoredCredentials Load() { return _credentials; }
            public void Save(StoredCredentials credentials) { _credentials = credentials; }
            public void Delete() { _credentials = null; }
        }

        private sealed class SelfTestProgress : IExportProgress
        {
            private readonly List<string> _log;
            public SelfTestProgress(List<string> log) { _log = log; }
            public void Report(string text, int step, int totalSteps) { lock (_log) _log.Add("匯出 " + step + "/" + totalSteps + " " + text); }
        }

        private sealed class SelfTestFlowProgress : IUploadFlowProgress
        {
            private readonly List<string> _log;
            public SelfTestFlowProgress(List<string> log) { _log = log; }
            public void Stage(string text) { lock (_log) _log.Add(text); }
            public void Upload(UploadProgress progress) { }
        }

        /// <summary>包住真正的匯出步驟，每步完成後把輸出檔複製一份到 evidence 資料夾（上傳成功後暫存會被刪，留證據用）。</summary>
        private sealed class EvidenceSteps : IBundleExportSteps
        {
            private readonly IBundleExportSteps _inner;
            private readonly string _dir;
            public readonly List<string> Copied = new List<string>();
            private readonly string _glbOverride;
            public EvidenceSteps(IBundleExportSteps inner, string dir, string glbOverride) { _inner = inner; _dir = dir; _glbOverride = glbOverride; }

            private void Keep(string path)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    var target = Path.Combine(_dir, Path.GetFileName(path));
                    File.Copy(path, target, true);
                    Copied.Add(Path.GetFileName(path) + "（" + new FileInfo(path).Length + " bytes）");
                }
                catch (Exception) { }
            }

            public RevitDocInfo GetDocInfo() { return _inner.GetDocInfo(); }
            public IReadOnlyList<ViewportSize> MeasureViewports(IReadOnlyList<long> ids) { return _inner.MeasureViewports(ids); }
            public void ExportPdf(IReadOnlyList<SheetItem> sheets, string outputPath) { _inner.ExportPdf(sheets, outputPath); Keep(outputPath); }
            public ModelExportResult ExportModel(bool includeDesignOptions, string workDirectory, string outputGlbPath)
            {
                // 標記可指定 glbOverride：用「同一份文件先前以同一個匯出指令產生的主模型 GLB」取代這一步（範例檔的設計選項分件匯出不完整，
                // 會讓整個模型步驟中止；與本案無關）。結果檔與警告都會明說這一點。
                if (!string.IsNullOrEmpty(_glbOverride))
                {
                    File.Copy(_glbOverride, outputGlbPath, true);
                    var stub = new ModelExportResult { OriginOffsetMeters = new double[] { 0, 0, 0 } };
                    stub.Warnings.Add("（自測）模型 GLB 使用預先匯出的主模型檔，非本次即時匯出。");
                    return stub;
                }
                var result = _inner.ExportModel(includeDesignOptions, workDirectory, outputGlbPath);
                return result; // GLB 很大，不複製
            }
            public void ExportNamedViews(IReadOnlyList<string> ids, double[] origin, string workDirectory, string outputPath) { _inner.ExportNamedViews(ids, origin, workDirectory, outputPath); Keep(outputPath); }
            public ViewPositionsResult ExportViewPositions(IReadOnlyList<string> ids, double[] origin, string workDirectory, string outputPath)
            {
                var result = _inner.ExportViewPositions(ids, origin, workDirectory, outputPath);
                Keep(outputPath);
                return result;
            }
            public ParamsExportResult ExportParams(double[] origin, string outputPath)
            {
                var result = _inner.ExportParams(origin, outputPath);
                Keep(outputPath);
                return result;
            }
        }
    }
}
#endif
