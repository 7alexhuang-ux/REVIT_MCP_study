#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>單一檔案的上傳進度（持久化，重試時據此跳過已完成的檔案）。</summary>
    public sealed class FileUploadState
    {
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public int PartsDone { get; set; }
        public bool Completed { get; set; }
    }

    /// <summary>
    /// 一個 bundle 的上傳狀態，存在版本包資料夾的 upload_state.json（不含任何權杖或簽名網址）。
    /// 已完成的檔案以 sha256 確認：檔案沒變就不重傳（X07）。
    /// </summary>
    public sealed class UploadState
    {
        public string BundleId { get; set; }
        public Dictionary<string, FileUploadState> Files { get; set; } = new Dictionary<string, FileUploadState>(StringComparer.Ordinal);

        public static UploadState LoadOrCreate(string path, string bundleId)
        {
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonConvert.DeserializeObject<UploadState>(File.ReadAllText(path, Encoding.UTF8));
                    if (state != null && state.BundleId == bundleId) return state;
                }
            }
            catch (Exception)
            {
                // 壞掉的狀態檔：當作全新，最壞情況是多傳一次。
            }
            return new UploadState { BundleId = bundleId };
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
    }

    public sealed class UploadProgress
    {
        public string Kind { get; set; }
        public int PartsDone { get; set; }
        public int PartsTotal { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public bool Skipped { get; set; }
    }

    /// <summary>
    /// 步驟 6：依序把每個檔案分段 PUT 到伺服器核發的網址。
    /// ‧每段最多重試 maxAttempts 次（網路錯誤、5xx、408／429 才重試；其他 4xx 直接失敗）；
    /// ‧完成一段就寫入狀態檔，所以中斷後再次執行會從該檔案的下一段繼續；
    /// ‧已完成且 sha256 相符的檔案完全不重傳。
    /// </summary>
    public sealed class BundleUploader
    {
        /// <summary>單一分段上限（整段讀進記憶體再送）；伺服器回報更大的值視為不正確。</summary>
        public const long MaxPartBytes = 128L * 1024 * 1024;

        private readonly AddinApiClient _api;
        private readonly int _maxAttempts;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public BundleUploader(AddinApiClient api, int maxAttempts = 3, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _api = api ?? throw new ArgumentNullException("api");
            _maxAttempts = Math.Max(1, maxAttempts);
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        public static string StatePath(ExportedBundle exported)
        {
            return Path.Combine(exported.Directory, "upload_state.json");
        }

        public async Task UploadAsync(BundleCreateResult bundle, ExportedBundle exported, IProgress<UploadProgress> progress, CancellationToken ct)
        {
            var statePath = StatePath(exported);
            var state = UploadState.LoadOrCreate(statePath, bundle.BundleId);

            foreach (var file in exported.Files)
            {
                ct.ThrowIfCancellationRequested();
                var ticket = bundle.Find(file.Kind);
                if (ticket == null)
                    throw new AddinApiException(AddinErrorKind.Server, "伺服器沒有為「" + file.Name + "」核發上傳憑證。");
                if (ticket.PartBytes <= 0 || ticket.PartBytes > MaxPartBytes)
                    throw new AddinApiException(AddinErrorKind.Server, "伺服器回報的分段大小不正確。");

                var partCount = (int)Math.Max(1, (file.Bytes + ticket.PartBytes - 1) / ticket.PartBytes);
                if (ticket.Urls.Count < partCount)
                    throw new AddinApiException(AddinErrorKind.Server, "伺服器核發的上傳網址不足（需要 " + partCount + " 段）。");

                FileUploadState fileState;
                if (!state.Files.TryGetValue(file.Kind, out fileState) || fileState.Sha256 != file.Sha256 || fileState.Bytes != file.Bytes)
                {
                    fileState = new FileUploadState { Sha256 = file.Sha256, Bytes = file.Bytes };
                    state.Files[file.Kind] = fileState;
                }

                if (fileState.Completed)
                {
                    Report(progress, file, partCount, partCount, true, ticket.PartBytes);
                    continue;
                }

                using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    for (var part = fileState.PartsDone; part < partCount; part++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var offset = part * ticket.PartBytes;
                        var length = (int)Math.Min(ticket.PartBytes, file.Bytes - offset);
                        var buffer = new byte[length];
                        stream.Seek(offset, SeekOrigin.Begin);
                        ReadFully(stream, buffer);

                        await PutWithRetryAsync(ticket.Urls[part], buffer, ct).ConfigureAwait(false);
                        fileState.PartsDone = part + 1;
                        state.Save(statePath);
                        Report(progress, file, part + 1, partCount, false, ticket.PartBytes);
                    }
                }

                fileState.Completed = true;
                state.Save(statePath);
            }
        }

        private async Task PutWithRetryAsync(string url, byte[] buffer, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _api.PutPartAsync(url, buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    return;
                }
                catch (AddinApiException ex) when (attempt < _maxAttempts && IsRetryable(ex))
                {
                    await _delay(TimeSpan.FromSeconds(attempt * attempt), ct).ConfigureAwait(false);
                }
            }
        }

        private static bool IsRetryable(AddinApiException ex)
        {
            return ex.Kind == AddinErrorKind.Network || ex.Kind == AddinErrorKind.Server
                || ex.StatusCode == 408 || ex.StatusCode == 429;
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) throw new IOException("讀取版本包檔案時提早結束。");
                total += read;
            }
        }

        private static void Report(IProgress<UploadProgress> progress, BundleFile file, int done, int total, bool skipped, long partBytes)
        {
            if (progress == null) return;
            progress.Report(new UploadProgress
            {
                Kind = file.Kind, PartsDone = done, PartsTotal = total,
                BytesDone = skipped ? file.Bytes : Math.Min(file.Bytes, (long)done * partBytes),
                BytesTotal = file.Bytes, Skipped = skipped
            });
        }
    }
}
