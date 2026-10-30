using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Configuration;
using RevitMCP.Core.ViewSync;
using RevitMCP.Models;

namespace RevitMCP.Core
{
    /// <summary>
    /// WebSocket 服務 - 作為伺服器端接收 MCP Server 的連線
    /// </summary>
    public class SocketService
    {
        private HttpListener _httpListener;
        private WebSocket _webSocket;
        private readonly object _socketLock = new object();
        private Guid _activeConnectionId = Guid.Empty;
        private bool _isRunning;
        private readonly ServiceSettings _settings;
        private CancellationTokenSource _cancellationTokenSource;

        // 瀏覽器連線（帶 Origin 標頭，網頁 3D 視角聯動 D-710）與上面的 MCP Server 單一連線完全分開管理：
        // 瀏覽器連進來不會擠掉 MCP Server，反之亦然。
        private sealed class BrowserConnection
        {
            public WebSocket Socket;
            public string Origin;
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
            public int AuthFailures;
            /// <summary>已通過配對 token 驗證（或剛配對成功）；只有這種連線會收到伺服器主動推送的事件（camera_changed）。</summary>
            public volatile bool Paired;
        }

        private readonly object _browserLock = new object();
        private readonly System.Collections.Generic.Dictionary<Guid, BrowserConnection> _browserConnections =
            new System.Collections.Generic.Dictionary<Guid, BrowserConnection>();
        private const int MaxBrowserConnections = 8;
        private const int MaxBrowserMessageBytes = 1024 * 1024; // 1 MB
        private int _browserConnectionCount; // 以 Interlocked 維護，名額在握手前先保留
        private const int MaxBrowserAuthFailures = 20;

        public event EventHandler<RevitCommandRequest> CommandReceived;
        public bool IsRunning => _isRunning;
        public bool IsConnected
        {
            get
            {
                lock (_socketLock)
                {
                    return _webSocket != null && _webSocket.State == WebSocketState.Open;
                }
            }
        }

        public SocketService(ServiceSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 啟動 WebSocket 伺服器
        /// </summary>
        public async Task StartAsync(bool showDialogs = true, bool repairPortConflicts = true)
        {
            if (_isRunning)
            {
                return;
            }

            try
            {
                // 啟動前檢查 Port 是否被佔用
                try
                {
                    var (occupantPid, occupantName) = GetPortOccupant(_settings.Port);
                    if (occupantPid > 0)
                    {
                        if (!repairPortConflicts)
                        {
                            Logger.Error($"Port {_settings.Port} 被 {occupantName} (PID: {occupantPid}) 佔用；自動啟動不修復或終止程序，listener 未啟動。");
                            return;
                        }
                        Logger.Info($"Port {_settings.Port} 被 {occupantName} (PID: {occupantPid}) 佔用，嘗試自動修復...");

                        if (TryAutoKillPortOccupant(occupantPid, occupantName))
                        {
                            // 等待 Port 釋放
                            await Task.Delay(500);
                            Logger.Info($"已自動結束 {occupantName} (PID: {occupantPid})，Port {_settings.Port} 已釋放");
                        }
                        else if (occupantPid == 4 && await TryReleaseHttpSysPortAsync(_settings.Port))
                        {
                            // PID 4 = HTTP.sys 孤兒 Request Queue（Revit 異常關閉殘留）
                            Logger.Info($"已透過 netsh 釋放 HTTP.sys 孤兒綁定，Port {_settings.Port} 已釋放");
                        }
                        else
                        {
                            string hint = occupantPid == 4
                                ? $"Port {_settings.Port} 被 HTTP.sys (PID: 4) 佔用（上次異常關閉殘留）。\n\n"
                                  + "請以系統管理員身分在終端機執行：\n"
                                  + "  net stop http /y && net start http\n\n"
                                  + "或執行：scripts\\release-port.ps1"
                                : $"Port {_settings.Port} 被 {occupantName} (PID: {occupantPid}) 佔用，且無法自動修復。\n\n"
                                  + "請手動關閉該程式後重試。";
                            Logger.Error(hint);
                            if (showDialogs) TaskDialog.Show("Revit MCP Plugin - Port 衝突", hint);
                            return;
                        }
                    }
                }
                catch (Exception portCheckEx)
                {
                    // GetActiveTcpListeners() 在部分 Windows 版本會拋出 PlatformNotSupportedException
                    // 跳過 Port 檢查，直接嘗試啟動 HttpListener（若 Port 真的被佔用，Start() 會報錯）
                    Logger.Info($"Port 預檢查不可用（{portCheckEx.GetType().Name}），跳過直接啟動");
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _isRunning = true;

                // 使用 HttpListener 來接受 WebSocket 連線
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_settings.Port}/");
                _httpListener.Start();

                Logger.Info($"WebSocket 伺服器已啟動 - 監聽: {_settings.Host}:{_settings.Port}");
                ViewSyncAuthService.Instance.Revoked -= OnPairingsRevoked;
                ViewSyncAuthService.Instance.Revoked += OnPairingsRevoked;

                // 在背景執行緒中等待連線
                _ = Task.Run(async () => await AcceptConnectionsAsync(_cancellationTokenSource.Token));

                if (showDialogs) TaskDialog.Show("MCP 服務", $"WebSocket 伺服器已啟動\n監聽: {_settings.Host}:{_settings.Port}");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                Logger.Error("啟動 WebSocket 伺服器失敗", ex);
                if (showDialogs) TaskDialog.Show("錯誤", $"啟動 WebSocket 伺服器失敗: {ex.Message}");
                throw;
            }
        }

        private void OnPairingsRevoked(object sender, EventArgs e) => CloseAllBrowserConnections("網頁配對已撤銷");

        /// <summary>
        /// 接受 WebSocket 連線
        /// </summary>
        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();

                    // 帶 Origin 標頭＝瀏覽器：走白名單＋配對 token＋指令白名單的獨立通道；沒有 Origin 的維持原樣。
                    var originHeader = context.Request.Headers["Origin"];
                    if (!string.IsNullOrEmpty(originHeader))
                    {
                        // 握手丟到 Task，不阻塞接受連線的迴圈（慢速或惡意用戶端不能卡住其他連線）。
                        var browserContext = context;
                        _ = Task.Run(async () =>
                        {
                            try { await HandleBrowserConnectionAsync(browserContext, originHeader, cancellationToken); }
                            catch (Exception ex) { Logger.Error("[Socket] 瀏覽器連線處理失敗", ex); }
                        });
                        continue;
                    }

                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        var acceptedSocket = wsContext.WebSocket;
                        var connectionId = Guid.NewGuid();
                        WebSocket previousSocket = null;
                        Guid previousConnectionId = Guid.Empty;
                        lock (_socketLock)
                        {
                            previousSocket = _webSocket;
                            previousConnectionId = _activeConnectionId;
                            _webSocket = acceptedSocket;
                            _activeConnectionId = connectionId;
                        }

                        if (previousSocket != null && previousSocket.State == WebSocketState.Open)
                        {
                            Logger.Info($"[Socket] 新 MCP Server 連線已接手，關閉舊連線 {previousConnectionId}");
                            _ = Task.Run(async () => await CloseSocketQuietlyAsync(
                                previousSocket,
                                WebSocketCloseStatus.NormalClosure,
                                "新的 MCP 連線已接手"));
                        }

                        Logger.Info($"[Socket] MCP Server 已連線 ({connectionId})");

                        // 在獨立任務中處理訊息，不要阻塞接受連線的迴圈
                        _ = Task.Run(async () => await ReceiveMessagesAsync(acceptedSocket, connectionId, cancellationToken));
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Logger.Error("[Socket] 接受連線錯誤", ex);
                    }
                }
            }
        }

        /// <summary>
        /// 接收訊息
        /// </summary>
        private async Task ReceiveMessagesAsync(WebSocket socket, Guid connectionId, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];

            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using (var messageStream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                                Logger.Info($"[Socket] MCP Server 已斷線 ({connectionId})");
                                return;
                            }

                            if (result.MessageType != WebSocketMessageType.Text)
                            {
                                continue;
                            }

                            messageStream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            continue;
                        }

                        string message = Encoding.UTF8.GetString(messageStream.ToArray());
                        Logger.Debug($"[Socket] 接收到訊息: {message}");
                        if (!IsActiveConnection(socket, connectionId))
                        {
                            Logger.Info($"[Socket] 忽略舊連線命令 ({connectionId})");
                            continue;
                        }

                        HandleMessage(message, connectionId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 這是正常關閉，不需要視為錯誤
                Logger.Info("[Socket] 訊息接收已停止 (服務已取消)");
            }
            catch (Exception ex)
            {
                Logger.Error("[Socket] 接收訊息錯誤", ex);
            }
            finally
            {
                lock (_socketLock)
                {
                    if (ReferenceEquals(_webSocket, socket) && _activeConnectionId == connectionId)
                    {
                        _webSocket = null;
                        _activeConnectionId = Guid.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// 處理接收到的訊息
        /// </summary>
        private void HandleMessage(string message, Guid connectionId)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<RevitCommandRequest>(message);
                request.ConnectionId = connectionId;
                Logger.Info($"[Socket] 處理命令: {request.CommandName} (RequestId: {request.RequestId})");
                CommandReceived?.Invoke(this, request);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Socket] 解析命令失敗: {message}", ex);
            }
        }

        /// <summary>
        /// 發送回應
        /// </summary>
        public async Task SendResponseAsync(RevitCommandResponse response, Guid connectionId)
        {
            BrowserConnection browser;
            lock (_browserLock) _browserConnections.TryGetValue(connectionId, out browser);
            if (browser != null)
            {
                await SendBrowserAsync(browser, response);
                return;
            }

            WebSocket socket;
            lock (_socketLock)
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket 未連線");
                }

                if (_activeConnectionId != connectionId)
                {
                    Logger.Info($"[Socket] 捨棄舊連線回應 (RequestId: {response.RequestId}, ConnectionId: {connectionId})");
                    return;
                }

                socket = _webSocket;
            }

            try
            {
                string json = JsonConvert.SerializeObject(response);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                Logger.Debug($"[Socket] 已發送回應 (RequestId: {response.RequestId})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Socket] 發送回應失敗 (RequestId: {response.RequestId})", ex);
                throw;
            }
        }

        private bool IsActiveConnection(WebSocket socket, Guid connectionId)
        {
            lock (_socketLock)
            {
                return ReferenceEquals(_webSocket, socket)
                    && _activeConnectionId == connectionId
                    && _webSocket.State == WebSocketState.Open;
            }
        }

        private static async Task CloseSocketQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(status, description, cts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"WebSocket 關閉失敗（可忽略）: {ex.Message}");
            }
            finally
            {
                socket.Dispose();
            }
        }

        /// <summary>
        /// 停止服務
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            Logger.Info("正在停止 WebSocket 伺服器...");

            try
            {
                // 先取消所有背景任務
                _cancellationTokenSource?.Cancel();

                CloseAllBrowserConnections("服務關閉");

                // 處理 WebSocket 關閉 (不阻塞 UI 執行緒)
                if (_webSocket != null)
                {
                    WebSocket ws;
                    lock (_socketLock)
                    {
                        ws = _webSocket;
                        _webSocket = null; // 先斷開引用
                        _activeConnectionId = Guid.Empty;
                    }

                    Task.Run(async () =>
                    {
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                            {
                                // 給予 2 秒時間嘗試正常關閉
                                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                                {
                                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "服務關閉", cts.Token);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"WebSocket 正常關閉失敗 (此為正常現象): {ex.Message}");
                        }
                        finally
                        {
                            ws.Dispose();
                            Logger.Info("WebSocket 已釋放");
                        }
                    });
                }

                // 停止 HttpListener
                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                    Logger.Info("HttpListener 已停止並關閉");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("停止服務時發生錯誤", ex);
            }
            finally
            {
                _isRunning = false;
                Logger.Info("WebSocket 伺服器已完全停止");
            }
        }

        // ------------------------------------------------------------------ 瀏覽器連線（D-710）

        private async Task HandleBrowserConnectionAsync(HttpListenerContext context, string origin, CancellationToken cancellationToken)
        {
            var auth = ViewSyncAuthService.Instance;
            if (!context.Request.IsWebSocketRequest || !auth.IsOriginAllowed(origin))
            {
                Logger.Info($"[Socket] 拒絕瀏覽器連線，Origin 不在白名單: {origin}");
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            // 原子保留名額：先加再判斷，超過就退回，避免並行握手同時通過檢查。
            if (Interlocked.Increment(ref _browserConnectionCount) > MaxBrowserConnections)
            {
                Interlocked.Decrement(ref _browserConnectionCount);
                Logger.Info("[Socket] 拒絕瀏覽器連線：同時連線數已達上限");
                context.Response.StatusCode = 503;
                context.Response.Close();
                return;
            }

            WebSocket socket;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                socket = wsContext.WebSocket;
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _browserConnectionCount);
                Logger.Error("[Socket] 瀏覽器 WebSocket 握手失敗", ex);
                return;
            }

            var connectionId = Guid.NewGuid();
            var connection = new BrowserConnection { Socket = socket, Origin = origin };
            lock (_browserLock) _browserConnections[connectionId] = connection;
            Logger.Info($"[Socket] 瀏覽器已連線 ({connectionId}) Origin={origin}");
            _ = Task.Run(async () => await ReceiveBrowserMessagesAsync(connection, connectionId, cancellationToken));
        }

        private async Task ReceiveBrowserMessagesAsync(BrowserConnection connection, Guid connectionId, CancellationToken cancellationToken)
        {
            var socket = connection.Socket;
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using (var messageStream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        var tooLarge = false;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                                return;
                            }

                            if (result.MessageType != WebSocketMessageType.Text) continue;
                            if (messageStream.Length + result.Count > MaxBrowserMessageBytes) { tooLarge = true; continue; }
                            messageStream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (tooLarge)
                        {
                            await SendBrowserAsync(connection, new RevitCommandResponse { Success = false, Error = "訊息太大。", ErrorCode = "message_too_large" });
                            continue;
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;
                        HandleBrowserMessage(Encoding.UTF8.GetString(messageStream.ToArray()), connection, connectionId);
                        if (connection.AuthFailures >= MaxBrowserAuthFailures)
                        {
                            Logger.Info($"[Socket] 瀏覽器連線授權失敗過多，關閉 ({connectionId})");
                            await CloseSocketQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "授權失敗過多");
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Debug($"[Socket] 瀏覽器連線結束 ({connectionId}): {ex.Message}");
            }
            finally
            {
                bool removed;
                lock (_browserLock) removed = _browserConnections.Remove(connectionId);
                if (removed) Interlocked.Decrement(ref _browserConnectionCount);
                Logger.Info($"[Socket] 瀏覽器已斷線 ({connectionId})");
            }
        }

        /// <summary>授權（Origin → 指令白名單 → token）後才把請求交給 CommandReceived；配對指令在這裡就地處理。</summary>
        private void HandleBrowserMessage(string message, BrowserConnection connection, Guid connectionId)
        {
            RevitCommandRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<RevitCommandRequest>(message);
            }
            catch (Exception)
            {
                _ = SendBrowserAsync(connection, new RevitCommandResponse { Success = false, Error = "無法解析請求。", ErrorCode = "bad_request" });
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.CommandName))
            {
                _ = SendBrowserAsync(connection, new RevitCommandResponse { Success = false, Error = "缺少 CommandName。", ErrorCode = "bad_request", RequestId = request?.RequestId });
                return;
            }

            var parameters = request.Parameters as JObject ?? new JObject();
            var token = parameters["token"]?.Type == JTokenType.String ? parameters["token"].Value<string>() : null;
            var decision = ViewSyncAuthService.Instance.Authorize(connection.Origin, request.CommandName, token);
            if (decision != AuthDecision.Allowed)
            {
                connection.AuthFailures++;
                Logger.Info($"[Socket] 瀏覽器請求被拒 ({ViewSyncAuthorizer.ErrorCode(decision)}): {request.CommandName}");
                _ = SendBrowserAsync(connection, new RevitCommandResponse
                {
                    Success = false,
                    Error = ViewSyncAuthorizer.ErrorMessage(decision),
                    ErrorCode = ViewSyncAuthorizer.ErrorCode(decision),
                    RequestId = request.RequestId
                });
                return;
            }

            if (ViewSyncCommands.IsPair(request.CommandName))
            {
                var code = parameters["code"]?.Type == JTokenType.String ? parameters["code"].Value<string>() : null;
                var newToken = ViewSyncAuthService.Instance.TryPair(connection.Origin, code, out var pairResult);
                if (newToken == null)
                {
                    connection.AuthFailures++;
                    _ = SendBrowserAsync(connection, new RevitCommandResponse
                    {
                        Success = false,
                        Error = PairFailureMessage(pairResult),
                        ErrorCode = "pair_" + pairResult.ToString().ToLowerInvariant(),
                        RequestId = request.RequestId
                    });
                    return;
                }

                connection.Paired = true;
                Logger.Info($"[Socket] 網頁配對成功 Origin={connection.Origin}");
                _ = SendBrowserAsync(connection, new RevitCommandResponse
                {
                    Success = true,
                    Data = new JObject { ["token"] = newToken, ["origin"] = OriginPolicy.Normalize(connection.Origin) },
                    RequestId = request.RequestId
                });
                return;
            }

            connection.Paired = true; // 走到這裡＝token 已驗證通過
            parameters.Remove("token"); // token 不往下傳，也不進日誌
            parameters["__browser"] = true; // 由 SocketService 無條件覆寫；執行器據此不回傳本機路徑
            request.Parameters = parameters;
            request.ConnectionId = connectionId;
            request.IsBrowser = true;
            Logger.Info($"[Socket] 處理瀏覽器命令: {request.CommandName} (RequestId: {request.RequestId})");
            CommandReceived?.Invoke(this, request);
        }

        private static string PairFailureMessage(PairingResult result)
        {
            switch (result)
            {
                case PairingResult.NoActiveCode: return "目前沒有配對碼：請在 Revit 功能區按「網頁配對」。";
                case PairingResult.Expired: return "配對碼已過期，請在 Revit 重新按「網頁配對」。";
                case PairingResult.TooManyAttempts: return "輸入錯誤次數過多，配對碼已作廢，請在 Revit 重新按「網頁配對」。";
                default: return "配對碼不正確。";
            }
        }

        private static async Task SendBrowserAsync(BrowserConnection connection, RevitCommandResponse response)
        {
            await SendBrowserTextAsync(connection, JsonConvert.SerializeObject(response), $"回應 RequestId: {response.RequestId}");
        }

        private static async Task SendBrowserTextAsync(BrowserConnection connection, string json, string what)
        {
            if (connection.Socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await connection.SendLock.WaitAsync();
            try
            {
                if (connection.Socket.State == WebSocketState.Open)
                    await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Socket] 發送瀏覽器訊息失敗 ({what})", ex);
            }
            finally
            {
                connection.SendLock.Release();
            }
        }

        /// <summary>目前是否有已配對、仍連線中的瀏覽器連線（沒有就不必組推送內容）。</summary>
        public bool HasPairedBrowserConnections()
        {
            lock (_browserLock)
                return _browserConnections.Values.Any(c => c.Paired && c.Socket.State == WebSocketState.Open);
        }

        /// <summary>
        /// 對所有已配對的瀏覽器連線推送一則事件（例如 camera_changed）。沒有連線時什麼都不做；
        /// 送出失敗只寫 log，不丟例外（推送是盡力而為，網頁下次連線會用 list_cameras 補同步）。回傳實際送出的連線數。
        /// </summary>
        public int BroadcastToPairedBrowsers(string json, string what)
        {
            BrowserConnection[] targets;
            lock (_browserLock)
                targets = _browserConnections.Values.Where(c => c.Paired && c.Socket.State == WebSocketState.Open).ToArray();
            foreach (var connection in targets)
                _ = SendBrowserTextAsync(connection, json, what);
            return targets.Length;
        }

        /// <summary>關閉所有瀏覽器連線（撤銷配對、停止服務時）。</summary>
        public void CloseAllBrowserConnections(string reason)
        {
            BrowserConnection[] all;
            lock (_browserLock)
            {
                all = _browserConnections.Values.ToArray();
                _browserConnections.Clear();
            }

            foreach (var connection in all)
            {
                var socket = connection.Socket;
                _ = Task.Run(async () => await CloseSocketQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, reason));
            }
        }

        /// <summary>
        /// 檢查指定 Port 是否被佔用，回傳 (PID, 進程名稱)。未佔用則回傳 (0, null)。
        /// </summary>
        private static (int pid, string name) GetPortOccupant(int port)
        {
            bool isInUse;
            try
            {
                isInUse = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(ep => ep.Port == port);
            }
            catch (PlatformNotSupportedException)
            {
                // 部分 Windows 版本不支援此 API，回傳「未佔用」讓 HttpListener 自行處理
                return (0, null);
            }

            if (!isInUse)
                return (0, null);

            // Port 被佔用，透過 netstat 找出佔用者 PID
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

                    var lines = output.Split('\n');
                    string portPattern = $":{port} ";
                    foreach (string line in lines)
                    {
                        // 不依賴語系關鍵字，改為判斷 port 格式 + TCP 行結構
                        if (!line.Contains(portPattern)) continue;

                        string trimmed = line.Trim();
                        string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        // netstat -ano 格式: Proto  Local Address  Foreign Address  State  PID
                        // PID 固定在最後一欄
                        if (parts.Length >= 5 && int.TryParse(parts[parts.Length - 1], out int pid) && pid > 0)
                        {
                            try
                            {
                                var occupant = Process.GetProcessById(pid);
                                return (pid, occupant.ProcessName);
                            }
                            catch
                            {
                                return (pid, "unknown");
                            }
                        }
                    }
                }
            }
            catch
            {
                // netstat 失敗
            }

            return (-1, "unknown");
        }

        /// <summary>
        /// 嘗試透過 netsh 釋放 HTTP.sys 孤兒 Request Queue。
        /// 當 PID 4 (System) 佔住 port 時，代表 HttpListener 上次沒有正常關閉，
        /// HTTP.sys kernel driver 仍持有該 port 的綁定。
        /// </summary>
        private static async Task<bool> TryReleaseHttpSysPortAsync(int port)
        {
            try
            {
                // 嘗試刪除可能殘留的 URL ACL 保留
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"http delete urlacl url=http://localhost:{port}/",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(5000);
                }

                // 也嘗試 http://+:port/ 格式
                psi.Arguments = $"http delete urlacl url=http://+:{port}/";
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(5000);
                }

                await Task.Delay(500);

                // 檢查是否已釋放
                bool stillInUse = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(ep => ep.Port == port);

                if (!stillInUse)
                {
                    Logger.Info($"netsh urlacl 清除成功，Port {port} 已釋放");
                    return true;
                }

                // urlacl 清除不夠，嘗試 net stop http（需要管理員權限）
                Logger.Info("urlacl 清除後 port 仍被佔用，嘗試重啟 HTTP 服務...");

                psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = "stop http /y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(10000);
                }

                await Task.Delay(1000);

                psi.Arguments = "start http";
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(10000);
                }

                await Task.Delay(500);

                stillInUse = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(ep => ep.Port == port);

                if (!stillInUse)
                {
                    Logger.Info($"HTTP 服務重啟成功，Port {port} 已釋放");
                    return true;
                }

                Logger.Error($"HTTP 服務重啟後 Port {port} 仍被佔用");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"TryReleaseHttpSysPortAsync 失敗: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 嘗試自動結束佔用 Port 的進程。
        /// 只會結束 node / Revit 相關的殭屍進程，不會誤殺其他應用程式。
        /// </summary>
        private static bool TryAutoKillPortOccupant(int pid, string processName)
        {
            if (pid <= 0) return false;

            string lower = (processName ?? "").ToLowerInvariant();

            // 安全白名單：只自動結束 MCP 相關的殭屍進程
            bool isSafeToKill = lower.Contains("node")
                             || lower.Contains("revitmcp");

            if (!isSafeToKill)
            {
                Logger.Info($"進程 {processName} (PID: {pid}) 不在自動清除白名單中，跳過");
                return false;
            }

            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill();
                proc.WaitForExit(3000);
                Logger.Info($"已自動結束進程: {processName} (PID: {pid})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"無法結束進程 {processName} (PID: {pid}): {ex.Message}");
                return false;
            }
        }
    }
}
