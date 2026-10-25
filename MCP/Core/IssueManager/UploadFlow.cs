#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    /// ‧取消：任何步驟之間偵測到取消 → 呼叫 DELETE /bundles/:id 並清掉本機暫存，不再上傳（X05）；
    /// ‧失敗：顯示白話原因；伺服器端 bundle 保持 open 到過期自動清；若匯出已完成，可用 ResumeAsync 重試上傳（不重傳已完成的檔案）；
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

        private BundleCreateResult _pendingBundle;
        private ExportedBundle _pendingExport;

        public UploadFlow(AddinApiClient api, IBundleExportSteps steps, IApiThreadRunner runner, string bundlesRoot,
            BundleExporter exporter = null, BundleUploader uploader = null, Func<DateTimeOffset> now = null)
        {
            _api = api ?? throw new ArgumentNullException("api");
            _steps = steps ?? throw new ArgumentNullException("steps");
            _runner = runner ?? throw new ArgumentNullException("runner");
            _bundlesRoot = bundlesRoot ?? throw new ArgumentNullException("bundlesRoot");
            _exporter = exporter ?? new BundleExporter();
            _uploader = uploader ?? new BundleUploader(api);
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>匯出已完成、只剩上傳／finalize 失敗時為 true。</summary>
        public bool CanResume { get { return _pendingBundle != null && _pendingExport != null; } }

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
                var created = await _api.CreateBundleAsync(request.ProjectId, request.Stage, request.Description,
                    new BundleSummary
                    {
                        Sheets = request.Sheets.Count, Views = request.Views.Count, Models = request.IncludeModel ? 1 : 0, Bytes = 0
                    }, ct).ConfigureAwait(false);

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

                _pendingBundle = created;
                _pendingExport = exported;
                return await UploadAndFinalizeAsync(flowProgress, ct).ConfigureAwait(false);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>匯出完成後上傳或 finalize 失敗時，重新執行步驟 6、7。</summary>
        public async Task<UploadFlowResult> ResumeAsync(IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            if (!CanResume) throw new InvalidOperationException("沒有可以繼續的上傳。");
            Enter();
            try
            {
                if (_pendingBundle.ExpiresAt.HasValue && _pendingBundle.ExpiresAt.Value <= _now())
                {
                    DiscardPendingLocal();
                    throw new AddinApiException(AddinErrorKind.Rejected, "這個版本包已經過期，請重新按「上傳」。");
                }
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
            var id = _pendingBundle.BundleId;
            var directory = _pendingExport.Directory;
            _pendingBundle = null;
            _pendingExport = null;
            TryDeleteDirectory(directory);
            try
            {
                await _api.DeleteBundleAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 伺服器端 bundle 會在過期時自動清除；本機檔已刪，不再丟例外打擾關視窗。
            }
        }

        private async Task<UploadFlowResult> UploadAndFinalizeAsync(IUploadFlowProgress flowProgress, CancellationToken ct)
        {
            var bundle = _pendingBundle;
            var exported = _pendingExport;
            try
            {
                flowProgress.Stage("上傳檔案（背景進行，Revit 可以繼續操作）");
                await _uploader.UploadAsync(bundle, exported, new Progress<UploadProgress>(flowProgress.Upload), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                flowProgress.Stage("完成版本（伺服器檢查檔案後建立工作版）");
                var finalized = await _api.FinalizeAsync(bundle.BundleId, exported.Manifest, ct).ConfigureAwait(false);

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
                _pendingBundle = null;
                _pendingExport = null;
                await CleanupAfterCancelAsync(bundle.BundleId, exported.Directory).ConfigureAwait(false);
                throw;
            }
        }

        private async Task CleanupAfterCancelAsync(string bundleId, string directory)
        {
            await _api.DeleteBundleAsync(bundleId, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(directory);
        }

        private void DiscardPendingLocal()
        {
            if (_pendingExport != null) TryDeleteDirectory(_pendingExport.Directory);
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
