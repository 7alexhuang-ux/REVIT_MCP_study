#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 把一段工作丟到 Revit API 執行緒執行（實作：QueuedApiThreadRunner＋ExternalEventManager）。測試時直接在目前執行緒跑。
    /// 取消只對「還在排隊、尚未開始」的工作有效；已開始的工作會做完。
    /// </summary>
    public interface IApiThreadRunner
    {
        Task<T> RunAsync<T>(Func<T> work, CancellationToken ct);
    }

    public interface IUploadFlowProgress
    {
        void Stage(string text);
        void Upload(UploadProgress progress);
    }

    public sealed class UploadFlowResult
    {
        public string BundleId { get; set; }
        public string SetId { get; set; }
        public string Url { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 整個「按下上傳」的流程（revit-addin-export.md §3）：
    /// 1 建立版本包 → 2～5 匯出（每個 Revit 步驟各自一次 ExternalEvent，步驟之間回到訊息迴圈）→ 6 背景上傳 → 7 finalize。
    /// ‧取消：任何步驟之間偵測到取消 → 呼叫 DELETE /bundles/:id 並清掉本機暫存，不再上傳（X05）。
    ///   伺服器回 409（版本已建立或正在建立）時，不假裝取消成功，而是告訴使用者要到網站刪除版本；
    /// ‧失敗：顯示白話原因；匯出已完成時可用 ResumeAsync 重試上傳（不重傳已完成的檔案）；
    /// ‧finalize 驗證失敗時伺服器會作廢整個 bundle（之後再送 finalize 一律 409 bundle_failed）：重試時改為「重新建立 bundle、重傳檔案、
    ///   再 finalize」（本機檔案不用重新匯出）；內容本身被拒（manifest 不符規格、PDF 頁數不符…）重試也不會過，就直接放棄暫存；
    /// ‧finalize 最長可能數分鐘：逾時（或 502／504）後重送 finalize（冪等），伺服器回 409 finalize_in_progress 就輪詢等待，上限 10 分鐘；
    /// ‧同一時間只允許一個進行中的上傳（跨視窗、跨 flow 實例）；任何結束路徑（成功、取消、逾時、例外）都會在 finally 釋放這個旗標。
    /// </summary>
    public sealed class UploadFlow
    {
        private static int _running;

        private readonly AddinApiClient _api;
        private readonly IBundleExportSteps _steps;
        private readonly IApiThreadRunner _runner;
        private readonly string _bundlesRoot;
        private readonly BundleExporter _exporter;
        private readonly BundleUploader _uploader;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        private ExportRequest _pendingRequest;
        private BundleCreateResult _pendingBundle;
        private ExportedBundle _pendingExport;

        public UploadFlow(AddinApiClient api, IBundleExportSteps steps, IApiThreadRunner runner, string bundlesRoot,
            BundleExporter exporter = null, BundleUploader uploader = null, Func<DateTimeOffset> now = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _api = api ?? throw new ArgumentNullException("api");
            _steps = steps ?? throw new ArgumentNullException("steps");
            _runner = runner ?? throw new ArgumentNullException("runner");
            _bundlesRoot = bundlesRoot ?? throw new ArgumentNullException("bundlesRoot");
            _exporter = exporter ?? new BundleExporter();
            _uploader = uploader ?? new BundleUploader(api);
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        /// <summary>finalize 回 409 finalize_in_progress（或逾時）後，重送 finalize 的間隔。</summary>
        public TimeSpan FinalizePollInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>等待伺服器完成 finalize 的上限（revit-addin-export.md §5.2：認領超過 10 分鐘視為上一個 finalize 已當掉）。</summary>
        public TimeSpan FinalizeMaxWait { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>匯出已完成、只剩上傳／finalize 失敗時為 true（伺服器端的版本包可能已作廢，重試時會自動重新建立）。</summary>
        public bool CanResume { get { return _pendingRequest != null && _pendingExport != null; } }

        public static bool IsRunning { get { return Volatile.Read(ref _running) == 1; } }

        public async Task<UploadFlowResult> RunAsync(ExportRequest request, IExportProgress exportProgress,
            IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            Enter();
            try
            {
                DiscardPendingLocal();
                flowProgress = flowProgress ?? new NullFlowProgress();

                flowProgress.Stage("向 Issue Manager 建立版本包");
                var created = await CreateBundleAsync(request, 0, ct).ConfigureAwait(false);

                var directory = Path.Combine(_bundlesRoot, _now().ToLocalTime().ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                ExportedBundle exported;
                try
                {
                    exported = await _exporter.ExportAsync(request, _steps, _runner, directory, exportProgress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await CleanupAfterCancelAsync(created.BundleId, directory).ConfigureAwait(false);
                    throw;
                }
                catch (Exception)
                {
                    TryDeleteDirectory(directory); // 匯出失敗：本機暫存不留；伺服器端 bundle 保持 open 到過期自動清
                    throw;
                }

                _pendingRequest = request;
                _pendingBundle = created;
                _pendingExport = exported;
                return await UploadAndFinalizeAsync(flowProgress, ct).ConfigureAwait(false);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>匯出完成後上傳或 finalize 失敗時，重新執行步驟 6、7（必要時先重新建立版本包）。</summary>
        public async Task<UploadFlowResult> ResumeAsync(IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            if (!CanResume) throw new InvalidOperationException("沒有可以繼續的上傳。");
            Enter();
            try
            {
                // 版本包過期：伺服器已（或即將）清掉暫存；本機檔案還在，直接重建，不必叫使用者重新匯出。
                if (_pendingBundle != null && _pendingBundle.ExpiresAt.HasValue && _pendingBundle.ExpiresAt.Value <= _now())
                    _pendingBundle = null;
                return await UploadAndFinalizeAsync(flowProgress ?? new NullFlowProgress(), ct).ConfigureAwait(false);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>放棄待重試的上傳：先刪本機檔，再盡力通知伺服器清暫存（伺服器連不上也不會留下本機檔）。</summary>
        public async Task DiscardAsync()
        {
            if (!CanResume) return;
            var id = _pendingBundle != null ? _pendingBundle.BundleId : null;
            var directory = _pendingExport.Directory;
            _pendingRequest = null;
            _pendingBundle = null;
            _pendingExport = null;
            TryDeleteDirectory(directory);
            if (id == null) return;
            try
            {
                await _api.DeleteBundleAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 伺服器端 bundle 會在過期時自動清除；本機檔已刪，不再丟例外打擾關視窗。
            }
        }

        private Task<BundleCreateResult> CreateBundleAsync(ExportRequest request, long knownBytes, CancellationToken ct)
        {
            return _api.CreateBundleAsync(request.ProjectId, request.Stage, request.Description,
                new BundleSummary
                {
                    Sheets = request.Sheets.Count, Views = request.Views.Count, Models = request.IncludeModel ? 1 : 0, Bytes = knownBytes
                }, ct);
        }

        private async Task<UploadFlowResult> UploadAndFinalizeAsync(IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            var exported = _pendingExport;
            try
            {
                if (_pendingBundle == null) await RecreateBundleAsync(flowProgress, ct).ConfigureAwait(false);
                var bundle = _pendingBundle;

                try
                {
                    BundleUploader.EnsureWithinLimits(bundle.Limits, exported);
                }
                catch (InvalidOperationException)
                {
                    await AbandonAsync(bundle.BundleId).ConfigureAwait(false); // 檔案超過上限：再試也一樣，不留暫存
                    throw;
                }

                flowProgress.Stage("上傳檔案（背景進行，Revit 可以繼續操作）");
                await _uploader.UploadAsync(bundle, exported, new Progress<UploadProgress>(flowProgress.Upload), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                FinalizeResult finalized;
                try
                {
                    finalized = await FinalizeWithPollingAsync(bundle.BundleId, exported.Manifest, flowProgress, ct).ConfigureAwait(false);
                }
                catch (AddinApiException ex) when (IsBundleUnusable(ex))
                {
                    // 伺服器已作廢（或過期、找不到）這個 bundle：不能再對它重送 finalize。
                    _pendingBundle = null;
                    if (IsContentRejection(ex)) DiscardPendingLocal(); // 內容本身被拒：重傳也不會過，不留暫存，請使用者調整後重新匯出
                    throw;
                }

                _pendingRequest = null;
                _pendingBundle = null;
                _pendingExport = null;
                TryDeleteDirectory(exported.Directory);
                return new UploadFlowResult
                {
                    BundleId = bundle.BundleId, SetId = finalized.SetId, Url = finalized.Url, Warnings = finalized.Warnings
                };
            }
            catch (OperationCanceledException)
            {
                var id = _pendingBundle != null ? _pendingBundle.BundleId : null;
                _pendingRequest = null;
                _pendingBundle = null;
                _pendingExport = null;
                await CleanupAfterCancelAsync(id, exported.Directory).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// 版本包被伺服器作廢或過期後，用已匯出的本機檔案重新建立：確認檔案沒被改動、清掉舊的上傳進度，
        /// 然後 POST /bundles 取得新的編號與上傳網址（舊的不再使用，也不需要 DELETE）。
        /// </summary>
        private async Task RecreateBundleAsync(IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            flowProgress.Stage(Strings.RecreateBundleStage);
            BundleUploader.VerifyFilesUnchanged(_pendingExport);
            try
            {
                var statePath = BundleUploader.StatePath(_pendingExport);
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _pendingBundle = await CreateBundleAsync(_pendingRequest, _pendingExport.TotalBytes, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// finalize 並處理「伺服器還沒做完」：等不到回應（逾時、502、504）或 409 finalize_in_progress 時，
        /// 隔一段時間重送 finalize（伺服器端冪等：做完了就回同一個 setId），最多等 <see cref="FinalizeMaxWait"/>。
        /// </summary>
        private async Task<FinalizeResult> FinalizeWithPollingAsync(string bundleId, JObject manifest, IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            var start = _now();
            var waited = TimeSpan.Zero;
            flowProgress.Stage(Strings.FinalizeStage);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await _api.FinalizeAsync(bundleId, manifest, ct).ConfigureAwait(false);
                }
                catch (AddinApiException ex) when (IsStillProcessing(ex))
                {
                    var elapsed = _now() - start;
                    if (waited > elapsed) elapsed = waited;
                    if (elapsed >= FinalizeMaxWait)
                        throw new AddinApiException(AddinErrorKind.Server, Strings.FinalizeStillRunning, 0, "finalize_in_progress");
                    flowProgress.Stage(Strings.FinalizePolling((int)elapsed.TotalSeconds));
                    await _delay(FinalizePollInterval, ct).ConfigureAwait(false);
                    waited += FinalizePollInterval;
                }
            }
        }

        /// <summary>伺服器可能還在處理這個 finalize：409 finalize_in_progress、等不到回應、或閘道逾時（502／504）。</summary>
        internal static bool IsStillProcessing(AddinApiException ex)
        {
            if (ex.StatusCode == 409 && ex.Code == "finalize_in_progress") return true;
            if (ex.Kind == AddinErrorKind.Network && ex.IsTimeout) return true;
            return ex.StatusCode == 502 || ex.StatusCode == 504;
        }

        /// <summary>
        /// finalize 回的錯誤代表「這個 bundle 已不能用」：驗證失敗被作廢（400／413）、409 bundle_failed／bundle_closed、410 過期、
        /// 500 finalize_failed（已清掉物件並作廢）、404（已被清除）。這時只能重新建立 bundle，不能對舊的重送 finalize（會得到 409）。
        /// </summary>
        internal static bool IsBundleUnusable(AddinApiException ex)
        {
            switch (ex.StatusCode)
            {
                case 400: return ex.Code != "invalid_request";
                case 413: return true;
                case 404: return true;
                case 410: return true;
                case 409: return ex.Code == "bundle_failed" || ex.Code == "bundle_closed";
                case 500: return ex.Code == "finalize_failed";
                default: return false;
            }
        }

        /// <summary>被拒的是「內容」本身（清單不符規格、PDF 頁數／格式不對、超過大小上限）：同樣的檔案重傳只會再被拒。</summary>
        internal static bool IsContentRejection(AddinApiException ex)
        {
            return ex.StatusCode == 413 || ex.Code == "invalid_manifest" || ex.Code == "page_count_mismatch" || ex.Code == "invalid_pdf";
        }

        /// <summary>放棄目前的上傳：通知伺服器刪除（盡力而為）並刪本機暫存。</summary>
        private async Task AbandonAsync(string bundleId)
        {
            var directory = _pendingExport != null ? _pendingExport.Directory : null;
            _pendingRequest = null;
            _pendingBundle = null;
            _pendingExport = null;
            await _api.DeleteBundleDetailedAsync(bundleId, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(directory);
        }

        /// <summary>
        /// 取消後的善後：刪本機暫存並通知伺服器。伺服器回 409 表示版本已建立（或正在建立）——這時「取消」其實沒有效果，
        /// 如實告訴使用者要到網站刪除，而不是假裝已取消。
        /// </summary>
        private async Task CleanupAfterCancelAsync(string bundleId, string directory)
        {
            var outcome = bundleId == null
                ? BundleDeleteOutcome.Deleted
                : await _api.DeleteBundleDetailedAsync(bundleId, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(directory);
            if (outcome == BundleDeleteOutcome.AlreadyFinalized)
                throw new AddinApiException(AddinErrorKind.Rejected, Strings.CancelTooLate, 409, "already_finalized");
            if (outcome == BundleDeleteOutcome.FinalizeInProgress)
                throw new AddinApiException(AddinErrorKind.Rejected, Strings.CancelDuringFinalize, 409, "finalize_in_progress");
        }

        private void DiscardPendingLocal()
        {
            if (_pendingExport != null) TryDeleteDirectory(_pendingExport.Directory);
            _pendingRequest = null;
            _pendingBundle = null;
            _pendingExport = null;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Enter()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("已經有一個上傳正在進行，請等它完成後再試。");
        }

        private static void Exit()
        {
            Volatile.Write(ref _running, 0);
        }

        private sealed class NullFlowProgress : IUploadFlowProgress
        {
            public void Stage(string text) { }
            public void Upload(UploadProgress progress) { }
        }
    }
}
