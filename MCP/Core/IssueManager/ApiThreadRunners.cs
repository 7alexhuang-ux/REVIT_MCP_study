#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>直接在目前執行緒執行（測試、沒有 Revit 的情境）。</summary>
    public sealed class InlineApiThreadRunner : IApiThreadRunner
    {
        public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Canceled<T>();
            try
            {
                return Task.FromResult(work());
            }
            catch (OperationCanceledException)
            {
                return Canceled<T>();
            }
            catch (Exception ex)
            {
                var completion = new TaskCompletionSource<T>();
                completion.SetException(ex);
                return completion.Task;
            }
        }

        private static Task<T> Canceled<T>()
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetCanceled();
            return completion.Task;
        }
    }

    /// <summary>
    /// 把工作排進外部事件佇列（<c>post</c>，實作：ExternalEventManager），在 Revit API 執行緒執行。
    /// ‧排隊等待期間可取消：取消或逾時時把工作標成「已放棄」，之後即使 Revit 才開始處理佇列也不會執行它；
    /// ‧<c>post</c> 回傳 false（<c>ExternalEvent.Raise()</c> 被拒絕）時立刻失敗，不會永遠等；
    /// ‧「開始執行」之前的等待有逾時（Revit 停在對話框、外部事件一直沒被處理）→ 白話錯誤；
    ///   一旦開始執行就等它做完（API 呼叫無法中途打斷，例如 GLB 匯出可達十幾分鐘），不受逾時限制。
    /// 這個類別不引用 Revit API，所以能在單元測試中以假的 post 驗證上述行為。
    /// </summary>
    public sealed class QueuedApiThreadRunner : IApiThreadRunner
    {
        public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(120);
        public const string NotRespondingMessage =
            "Revit 目前沒有回應，請關掉 Revit 裡的對話框後重試。（若 Revit 正在執行其他很久的指令，也請等它做完再試。）";
        public const string RejectedMessage =
            "Revit 沒有接受這次匯出要求。請確認 Revit 沒有停在對話框，然後重試；仍不行請重新啟動 Revit。";

        private const int Queued = 0;
        private const int Started = 1;
        private const int Abandoned = 2;

        private readonly Func<Action, bool> _post;
        private readonly TimeSpan _startTimeout;

        /// <param name="post">把 job 交給 Revit 的外部事件佇列；回傳 false 表示 Raise() 被拒絕。</param>
        /// <param name="startTimeout">等「開始執行」的上限；預設 <see cref="DefaultStartTimeout"/>。</param>
        public QueuedApiThreadRunner(Func<Action, bool> post, TimeSpan? startTimeout = null)
        {
            _post = post ?? throw new ArgumentNullException("post");
            _startTimeout = startTimeout ?? DefaultStartTimeout;
        }

        public async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = Queued;

            Action job = () =>
            {
                // 已被取消或逾時放棄的工作不能再執行（否則使用者以為停了，Revit 卻在背景匯出）。
                if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued) return;
                try
                {
                    completion.TrySetResult(work());
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            };

            bool accepted;
            try
            {
                accepted = _post(job);
            }
            catch (Exception)
            {
                Interlocked.CompareExchange(ref state, Abandoned, Queued);
                throw new InvalidOperationException(RejectedMessage);
            }
            if (!accepted)
            {
                Interlocked.CompareExchange(ref state, Abandoned, Queued);
                throw new InvalidOperationException(RejectedMessage);
            }

            using (var timeout = new CancellationTokenSource(_startTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            using (linked.Token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, Abandoned, Queued) != Queued) return; // 已開始執行：等它做完
                if (ct.IsCancellationRequested) completion.TrySetCanceled();
                else completion.TrySetException(new InvalidOperationException(NotRespondingMessage));
            }))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
    }
}
