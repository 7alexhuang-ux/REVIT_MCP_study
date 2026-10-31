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
    ///   分段上傳時被拒（410 過期、409 bundle_failed／bundle_closed、404、簽名網址主機 4xx）同樣走重建整批重傳；以伺服器回應為準，不看本機時鐘；
    /// ‧收到 409 bundle_closed 不直接當作廢：可能是自己先前的 finalize 已提交，先對同一個 bundle 再送一次 finalize（冪等回同一個 setId），仍失敗才重建；
    /// ‧伺服器每人最多 5 個未完成 bundle（24 小時才過期）：匯出失敗、500 finalize_failed、簽名網址失效時，舊 bundle 盡力 DELETE 一次
    ///   （失敗不阻擋流程、不顯示給使用者，只記一行日誌）；DELETE 若回 409 已 finalize，改走 finalize 取回結果，不重建；
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

        /// <summary>日誌（只寫固定文字與數字，不含權杖、網址、編號）；沒設就不記。</summary>
        public Action<string> Log { get; set; }

        /// <summary>盡力 DELETE 的等待上限：網站沒回應時不要讓使用者多等一分鐘。</summary>
        public static readonly TimeSpan BestEffortDeleteTimeout = TimeSpan.FromSeconds(15);

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
                // 先前待重試、現在被新的上傳取代的版本包：伺服器端也盡力刪掉，不然會一直佔每人 5 個的未完成名額。
                var staleId = _pendingBundle != null ? _pendingBundle.BundleId : null;
                DiscardPendingLocal();
                if (staleId != null) await TryDeleteServerBundleAsync(staleId).ConfigureAwait(false);
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
                    // 匯出失敗：本機暫存不留；伺服器端 bundle 也盡力刪掉——網站端每人最多 5 個未完成 bundle、24 小時才過期，
                    // 不刪的話連續幾次匯出失敗（例如 Revit 無回應逾時）就會卡在 429 too_many_open_bundles。
                    TryDeleteDirectory(directory);
                    await TryDeleteServerBundleAsync(created.BundleId).ConfigureAwait(false);
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
                // 版本包是否過期一律以伺服器回應為準（分段 PUT 或 finalize 回 410／404／409 時自動重建，本機檔案不必重新匯出）；
                // 不用本機時鐘判斷：時鐘不準會讓還能用的 bundle 被丟掉，而舊的又沒人刪，佔用每人 5 個的未完成名額。
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
                BundleCreateResult bundle;
                FinalizeResult finalized = null;
                var autoRecreated = false;
                while (true)
                {
                    bundle = _pendingBundle;
                    try
                    {
                        BundleUploader.EnsureWithinLimits(bundle.Limits, exported);
                    }
                    catch (InvalidOperationException)
                    {
                        await AbandonAsync(bundle.BundleId).ConfigureAwait(false); // 檔案超過上限：再試也一樣，不留暫存
                        throw;
                    }

                    AddinApiException unusable = null;
                    var finalizing = false; // 兩個階段「這個 bundle 已不能用」的判斷不同：分段 PUT 的 400（雜湊不符…）只代表該分段要重送
                    try
                    {
                        flowProgress.Stage("上傳檔案（背景進行，Revit 可以繼續操作）");
                        // 不用 Progress<T>：它把回報 Post 到建立當下的 SynchronizationContext（這裡通常是執行緒集區），
                        // 最後一段的上傳進度可能比後面的 Stage 文字（finalize 開始、伺服器處理中）更晚送達而蓋掉它。直接呼叫，順序才固定。
                        await _uploader.UploadAsync(bundle, exported, new DirectProgress<UploadProgress>(flowProgress.Upload), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        finalizing = true;
                        finalized = await FinalizeWithPollingAsync(bundle.BundleId, exported.Manifest, flowProgress, ct).ConfigureAwait(false);
                    }
                    catch (AddinApiException ex) when (finalizing ? IsBundleUnusable(ex) : IsUploadBundleUnusable(ex))
                    {
                        // 上傳分段或 finalize 時，伺服器說這個 bundle 已作廢、過期、被清除（或簽名網址已失效）：不能再對它重送。
                        unusable = ex;
                    }

                    if (unusable == null) break;
                    finalized = await TryRecoverUnusableBundleAsync(bundle, unusable, exported, flowProgress, ct).ConfigureAwait(false);
                    if (finalized != null) break; // 其實已經完成了

                    _pendingBundle = null; // 確實不能用：重新建立 bundle 並整批重傳（本機檔案不必重新匯出）
                    if (IsContentRejection(unusable)) DiscardPendingLocal(); // 內容本身被拒：重傳也不會過，不留暫存，請使用者調整後重新匯出
                    if (finalizing || autoRecreated || _pendingExport == null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unusable).Throw();
                    // 上傳到一半被拒（例如重試時才發現 bundle 早已過期）：使用者已經按過一次上傳／重試，同一次內自動重建一次，
                    // 免得要再多按一次「重試」；只自動一次，第二個 bundle 也被拒就把錯誤交給使用者。
                    autoRecreated = true;
                    await RecreateBundleAsync(flowProgress, ct).ConfigureAwait(false);
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

        /// <summary>
        /// 上傳分段（PUT）時的錯誤代表「這個 bundle 已不能用」：410 過期、404（已被清除）、409 bundle_failed／bundle_closed／already_finalized，
        /// 或簽名網址所在的主機拒絕（<see cref="AddinApiClient.UploadUrlRejectedCode"/>，簽名網址約 2 小時失效）。
        /// 以伺服器回應為準，不用本機時鐘判斷。分段本身的 400（checksum_mismatch、invalid_part）與 413 只代表該分段要重送／調整，不在此列。
        /// </summary>
        internal static bool IsUploadBundleUnusable(AddinApiException ex)
        {
            if (ex.Code == AddinApiClient.UploadUrlRejectedCode) return true;
            switch (ex.StatusCode)
            {
                case 404: return true;
                case 410: return true;
                case 409: return ex.Code == "bundle_failed" || ex.Code == "bundle_closed" || ex.Code == "already_finalized";
                default: return false;
            }
        }

        /// <summary>409 bundle_closed／already_finalized：可能是「自己先前的 finalize 其實已經提交」，要先確認，不能直接當作廢重建（會多建一個工作版本）。</summary>
        internal static bool IsClosedOrFinalized(AddinApiException ex)
        {
            return ex.StatusCode == 409 && (ex.Code == "bundle_closed" || ex.Code == "already_finalized");
        }

        /// <summary>這種錯誤發生時，伺服器端的 bundle 可能仍是 open（佔用每人 5 個的未完成名額）：500 finalize_failed（沒動到檔案時退回 open）、簽名網址失效。</summary>
        internal static bool MayStillBeOpenOnServer(AddinApiException ex)
        {
            return (ex.StatusCode == 500 && ex.Code == "finalize_failed") || ex.Code == AddinApiClient.UploadUrlRejectedCode;
        }

        /// <summary>
        /// bundle 被判定不能用之後的善後：①bundle_closed／already_finalized 時先對<b>同一個</b> bundle 再送一次 finalize（冪等：已完成會回同一個 setId，
        /// 避免第二個 finalize 收到 bundle_closed 就重建而多出第二個工作版本）；②可能仍是 open 的盡力 DELETE，若 DELETE 說「其實已 finalize／正在 finalize」就改走 finalize。
        /// 回傳非 null＝其實已完成，呼叫端照成功處理；null＝確實作廢，呼叫端重建。
        /// </summary>
        private async Task<FinalizeResult> TryRecoverUnusableBundleAsync(BundleCreateResult bundle, AddinApiException ex, ExportedBundle exported,
            IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            if (IsClosedOrFinalized(ex))
            {
                var done = await TryFinalizeAgainAsync(bundle.BundleId, exported, flowProgress, ct).ConfigureAwait(false);
                if (done != null) return done;
            }
            if (MayStillBeOpenOnServer(ex))
            {
                var outcome = await TryDeleteServerBundleAsync(bundle.BundleId).ConfigureAwait(false);
                if (outcome == BundleDeleteOutcome.AlreadyFinalized || outcome == BundleDeleteOutcome.FinalizeInProgress)
                {
                    var done = await TryFinalizeAgainAsync(bundle.BundleId, exported, flowProgress, ct).ConfigureAwait(false);
                    if (done != null) return done;
                }
            }
            return null;
        }

        private async Task<FinalizeResult> TryFinalizeAgainAsync(string bundleId, ExportedBundle exported, IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            try
            {
                return await FinalizeWithPollingAsync(bundleId, exported.Manifest, flowProgress, ct).ConfigureAwait(false);
            }
            catch (AddinApiException again) when (IsBundleUnusable(again))
            {
                return null; // 同一個 bundle 再問一次仍不能用：確實作廢
            }
        }

        /// <summary>盡力通知伺服器刪除 bundle：失敗、逾時（15 秒）都不丟例外、不顯示給使用者；只記一行日誌（不含編號、網址或權杖）。</summary>
        private async Task<BundleDeleteOutcome> TryDeleteServerBundleAsync(string bundleId)
        {
            if (string.IsNullOrEmpty(bundleId)) return BundleDeleteOutcome.Deleted;
            var outcome = BundleDeleteOutcome.Failed;
            try
            {
                using (var timeout = new CancellationTokenSource(BestEffortDeleteTimeout))
                {
                    outcome = await _api.DeleteBundleDetailedAsync(bundleId, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            try
            {
                if (Log != null) Log("盡力清除伺服器上未完成的版本包：" + outcome);
            }
            catch (Exception) { }
            return outcome;
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

        private sealed class DirectProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public DirectProgress(Action<T> report) { _report = report; }
            public void Report(T value) { _report(value); }
        }

        private sealed class NullFlowProgress : IUploadFlowProgress
        {
            public void Stage(string text) { }
            public void Upload(UploadProgress progress) { }
        }
    }
}
