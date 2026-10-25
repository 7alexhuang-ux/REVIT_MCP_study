#nullable disable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>瀏覽器授權完成後導回 loopback 的結果：code 或 error 二選一。</summary>
    public sealed class CallbackResult
    {
        public string Code { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 授權用的 loopback 接收器（addin-auth.md §4.2）。
    /// 只綁 127.0.0.1、埠由系統隨機配置（不需要 URL ACL／系統管理員權限）；
    /// 只接受 GET /callback、Host 必須是 127.0.0.1:埠、state 必須相符；其他請求回 400／404 並繼續等待，
    /// 所以本機其他程式亂打不會讓正確的回呼失敗；逾時後自動停止監聽。
    /// </summary>
    public sealed class LoopbackCallbackListener : IDisposable
    {
        private const int MaxRequestBytes = 8192;
        private static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(5);

        private readonly TcpListener _listener;
        private readonly string _expectedState;
        private readonly TaskCompletionSource<CallbackResult> _result =
            new TaskCompletionSource<CallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private int _disposed;

        private LoopbackCallbackListener(TcpListener listener, string expectedState)
        {
            _listener = listener;
            _expectedState = expectedState;
        }

        public int Port { get; private set; }

        /// <summary>實際綁定的位址（測試用，必為 127.0.0.1）。</summary>
        public IPAddress LocalAddress { get { return ((IPEndPoint)_listener.LocalEndpoint).Address; } }

        public static LoopbackCallbackListener Start(string expectedState)
        {
            if (string.IsNullOrEmpty(expectedState)) throw new ArgumentException("state 不可為空。");
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var instance = new LoopbackCallbackListener(tcp, expectedState)
            {
                Port = ((IPEndPoint)tcp.LocalEndpoint).Port
            };
            var ignored = instance.AcceptLoopAsync();
            return instance;
        }

        /// <summary>等待有效回呼；逾時丟 TimeoutException（並停止監聽）；取消丟 OperationCanceledException。</summary>
        public async Task<CallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, linked.Token);
                var finished = await Task.WhenAny(_result.Task, delay).ConfigureAwait(false);
                try
                {
                    if (finished == _result.Task)
                    {
                        linked.Cancel();
                        return await _result.Task.ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("等待瀏覽器授權逾時。");
                }
                finally
                {
                    Dispose();
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener 已停止
                }

                var ignored = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using (var timeout = new CancellationTokenSource(ConnectionReadTimeout))
                    {
                        var stream = client.GetStream();
                        var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                        int status;
                        string body;
                        var outcome = Evaluate(head, out status, out body);
                        await WriteResponseAsync(stream, status, body).ConfigureAwait(false);
                        if (outcome != null) _result.TrySetResult(outcome);
                    }
                }
                catch (Exception)
                {
                    // 連線中斷、逾時或亂送資料：忽略，繼續等正確的回呼。
                }
            }
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[MaxRequestBytes];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, used, buffer.Length - used, token).ConfigureAwait(false);
                if (read <= 0) break;
                used += read;
                var text = Encoding.ASCII.GetString(buffer, 0, used);
                if (text.Contains("\r\n\r\n")) return text;
            }
            return Encoding.ASCII.GetString(buffer, 0, used);
        }

        /// <summary>純函式：回傳有效結果（或 null）與要回給瀏覽器的狀態碼／內容。</summary>
        internal CallbackResult Evaluate(string head, out int status, out string body)
        {
            status = 400;
            body = Page("授權沒有完成", "這個請求不是有效的授權回呼，可以關閉此視窗。");
            var lines = (head ?? string.Empty).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines.Length > 0 ? lines[0] : string.Empty;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.Ordinal)) return null;

            var target = parts[1];
            var queryIndex = target.IndexOf('?');
            var path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
            if (!string.Equals(path, "/callback", StringComparison.Ordinal))
            {
                status = 404;
                body = Page("找不到", "這個位址不接受請求。");
                return null;
            }

            string host = null;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line.Substring(5).Trim();
            }
            if (!string.Equals(host, "127.0.0.1:" + Port, StringComparison.Ordinal)) return null; // 擋 DNS rebinding

            var query = ParseQuery(queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty);
            string state;
            query.TryGetValue("state", out state);
            if (!Pkce.SecureEquals(state, _expectedState)) return null;

            string error;
            if (query.TryGetValue("error", out error) && !string.IsNullOrEmpty(error))
            {
                status = 200;
                body = Page("已取消授權", "Revit 外掛沒有取得授權，可以關閉此視窗。");
                return new CallbackResult { Error = error };
            }

            string code;
            if (query.TryGetValue("code", out code) && !string.IsNullOrEmpty(code))
            {
                status = 200;
                body = Page("已完成授權", "可以回到 Revit，這個視窗可以關閉了。");
                return new CallbackResult { Code = code };
            }

            return null;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                try
                {
                    result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    // 壞的百分比編碼：略過該欄位。
                }
            }
            return result;
        }

        private static string Page(string title, string message)
        {
            return "<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">"
                + "<title>" + title + "</title></head><body style=\"font-family:sans-serif;margin:3rem\"><h1>" + title + "</h1><p>" + message + "</p></body></html>";
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
            var head = "HTTP/1.1 " + status + " " + reason + "\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + payload.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Referrer-Policy: no-referrer\r\n"
                + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
