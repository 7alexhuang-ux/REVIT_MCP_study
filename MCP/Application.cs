using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Reflection;
using RevitMCP.Core;
using RevitMCP.Configuration;

namespace RevitMCP
{
    public class Application : IExternalApplication
    {
        private static SocketService _socketService;
        private static UIApplication _uiApp;
        private static bool _startingSocket;
        private UIControlledApplication _startupApplication;

        public static SocketService SocketService => _socketService;
        public static UIApplication UIApp => _uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
            string autostartMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-mcp", "autostart");
            bool automatic = File.Exists(autostartMarker);
            try
            {
                // 初始化配置管理器
                _ = ConfigManager.Instance;
                
                // 記錄啟動
                Logger.Info("RevitMCP Plugin 正在啟動...");

                // 建立功能區面板
                RibbonPanel panel = application.CreateRibbonPanel("MCP Tools");
                
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // 1. MCP 服務切換按鈕 (Toggle)
                PushButtonData toggleButtonData = new PushButtonData(
                    "MCPToggle",
                    "MCP 服務\n(開/關)",
                    assemblyPath,
                    "RevitMCP.Commands.ToggleServiceCommand");
                toggleButtonData.ToolTip = "啟動或停止 MCP WebSocket 服務";
                panel.AddItem(toggleButtonData);

                // 2. 開啟日誌按鈕
                PushButtonData logButtonData = new PushButtonData(
                    "OpenLog",
                    "開啟\n日誌",
                    assemblyPath,
                    "RevitMCP.Commands.OpenLogCommand");
                logButtonData.ToolTip = "開啟 MCP 執行日誌";
                panel.AddItem(logButtonData);

                // 3. 設定按鈕
                PushButtonData settingsButtonData = new PushButtonData(
                    "MCPSettings",
                    "MCP\n設定",
                    assemblyPath,
                    "RevitMCP.Commands.SettingsCommand");
                settingsButtonData.ToolTip = "開啟 MCP 設定視窗";
                panel.AddItem(settingsButtonData);

                // 4. 上傳到 Issue Manager：獨立面板，緊鄰 MCP Tools。以 IExternalCommand＋ExternalEvent 執行，
                //    不經 MCP WebSocket，所以 MCP 服務關閉時也能使用。
                //    面板建立失敗（例如面板名稱衝突）不能拖垮其餘面板與整個外掛的啟動，所以單獨包起來。
                try
                {
                    RibbonPanel issueManagerPanel = application.CreateRibbonPanel(Core.IssueManager.Strings.PanelName);
                    PushButtonData uploadButtonData = new PushButtonData(
                        "UploadToIssueManager",
                        Core.IssueManager.Strings.ButtonText,
                        assemblyPath,
                        "RevitMCP.Commands.UploadToIssueManagerCommand");
                    uploadButtonData.ToolTip = Core.IssueManager.Strings.ButtonTooltip;
                    issueManagerPanel.AddItem(uploadButtonData);
                }
                catch (Exception ex)
                {
                    Logger.Error("[IssueManager] 建立功能區面板失敗：" + ex.GetType().Name + "：" + ex.Message);
                }

                // 清掉超過 N 天的舊上傳暫存（取消、失敗、直接關掉視窗、Revit 當掉留下的 PDF／GLB），背景執行、失敗不影響啟動。
                Core.IssueManager.IssueManagerSession.CleanupOldBundlesInBackground();

#if REVIT2024_OR_GREATER
                // 整地圖說面板（Revit 2024+），掛在自訂頁籤「黃道資訊」：
                // 核心入口（新整地方案／方案總覽）優先，樓板準備備板工具其後。
                // 2026-07-10：整地檢視／六級坡度／比較出圖／各別出圖 全部併入「方案總覽」清單動作，
                // 不再是主面板獨立按鈕（見 docs/superpowers/specs/2026-07-10-grading-ribbon-consolidation-design.md）。
                application.CreateRibbonTab("黃道資訊");
                RibbonPanel gradingPanel = application.CreateRibbonPanel("黃道資訊", "整地圖說");
                AddGradingButton(gradingPanel, assemblyPath, "NewGradingScheme", "新整地\n方案",
                    "RevitMCP.Commands.NewGradingSchemeCommand",
                    "選取一片地形＋控制樓板後，選模式（投影直貼/外延漸變/放坡銜接）與參數，執行並記錄一筆方案",
                    "new");
                AddGradingButton(gradingPanel, assemblyPath, "SchemeOverview", "方案\n總覽",
                    "RevitMCP.Commands.SchemeOverviewCommand",
                    "方案儀表板：看地形/開啟方案視圖/產生分析圖/出圖/還原/刪除/匯出，一個視窗總管全部方案",
                    "overview");
                gradingPanel.AddSeparator();
                AddGradingButton(gradingPanel, assemblyPath, "DuplicateFloorDraft", "複製\n樓板",
                    "RevitMCP.Commands.DuplicateFloorDraftCommand",
                    "複製選取的樓板，並在目前視圖隱藏原本的樓板，方便在複製出來的樓板上調整下一個方案的範圍",
                    "duplicate");
                AddGradingButton(gradingPanel, assemblyPath, "FloorElevation", "板高程\n斜板",
                    "RevitMCP.Commands.FloorElevationCommand",
                    "選取樓板後設定平板偏移、改斜板（點低側邊＋坡率 1:n）或還原平板",
                    "elev");
                AddGradingButton(gradingPanel, assemblyPath, "EdgeSlope", "邊坡\n坡率",
                    "RevitMCP.Commands.EdgeSlopeCommand",
                    "點選樓板邊指定例外放坡坡率（如臨路無障礙 1:12），其餘邊用整地時的預設坡率",
                    "edge");
                AddGradingButton(gradingPanel, assemblyPath, "BalancedElevation", "平衡高程\n試算",
                    "RevitMCP.Commands.BalancedElevationCommand",
                    "新整地方案的進階替代做法：以選取地形＋樓板，二分法試算統一升降量使淨土方逼近目標",
                    "balance");
#endif

                // 初始化 ExternalEventManager (必須在 UI 執行緒建立)
                _ = ExternalEventManager.Instance;

                // 註冊 Process 退出事件 — Revit crash 時 OnShutdown 不會被呼叫，
                // 但 ProcessExit 仍有機會觸發，確保 HttpListener 被釋放，
                // 避免 HTTP.sys 孤兒 Request Queue 佔住 port。
                AppDomain.CurrentDomain.ProcessExit += (s, e) => EmergencyStopSocket();

                Logger.Info("RevitMCP Plugin 已成功載入");

                // 自動啟動只由使用者預先建立的 opt-in 標記啟用；外掛不自行建立標記或改 Add-ins 部署。
                if (automatic)
                {
                    _startupApplication = application;
                    application.Idling += StartMcpOnFirstIdle;
                    Logger.Info($"偵測到自動啟動標記，等待首次 Idling: {autostartMarker}");
                }
                else
                {
                    TaskDialog.Show("RevitMCP",
                        "RevitMCP Plugin 已載入\n\n" +
                        "請點擊「MCP 服務 (開/關)」按鈕來啟用 AI 控制功能");
                }
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("載入 MCP Tools 失敗", ex);
                if (!automatic) TaskDialog.Show("錯誤", "載入 MCP Tools 失敗: " + ex.Message);
                return Result.Failed;
            }
        }

        private async void StartMcpOnFirstIdle(object sender, IdlingEventArgs args)
        {
            // 先解除訂閱，失敗也不在每次 Idling 無限重試或重複建立 WebSocket listener。
            if (_startupApplication != null)
            {
                _startupApplication.Idling -= StartMcpOnFirstIdle;
                _startupApplication = null;
            }
            if (!(sender is UIApplication uiApp))
            {
                Logger.Error("MCP 自動啟動取消：Idling sender 不是 UIApplication。");
                return;
            }
            await StartMcpServiceAsync(uiApp, true);
        }

#if REVIT2024_OR_GREATER
        private static void AddGradingButton(
            RibbonPanel panel, string assemblyPath, string name, string text, string className,
            string tooltip, string iconKey)
        {
            var buttonData = new PushButtonData(name, text, assemblyPath, className) { ToolTip = tooltip };
            var large = LoadGradingIcon(iconKey + "_32.png");
            var small = LoadGradingIcon(iconKey + "_16.png");
            if (large != null) { buttonData.LargeImage = large; }
            if (small != null) { buttonData.Image = small; }
            panel.AddItem(buttonData);
        }

        /// <summary>從嵌入資源載入 ribbon 色磚圖示（Resources/icons/*.png）；找不到回 null（退回純文字，不影響按鈕）。</summary>
        private static System.Windows.Media.ImageSource LoadGradingIcon(string fileName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(".icons." + fileName, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                if (resourceName == null) { return null; }

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) { return null; }
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }
#endif

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.Idling -= StartMcpOnFirstIdle;
                _startupApplication = null;
                EmergencyStopSocket();
                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        /// <summary>
        /// 緊急釋放 Socket 服務。同時被 OnShutdown 和 ProcessExit 呼叫，
        /// 內部有 null check 防止重複執行。
        /// </summary>
        private static void EmergencyStopSocket()
        {
            try
            {
                if (_socketService != null)
                {
                    _socketService.Stop();
                    _socketService = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"EmergencyStopSocket failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 啟動 MCP 服務
        /// </summary>
        public static void StartMCPService(UIApplication uiApp)
        {
            _ = StartMcpServiceAsync(uiApp, false);
        }

        private static async Task StartMcpServiceAsync(UIApplication uiApp, bool automatic)
        {
            if (_startingSocket) return;
            _startingSocket = true;
            try
            {
                _uiApp = uiApp;
                var settings = ConfigManager.Instance.Settings;

                if (_socketService != null && _socketService.IsRunning)
                {
                    if (!automatic) TaskDialog.Show("MCP 服務", "服務已在執行中。");
                    return;
                }

                // 環境變數僅覆寫這次 listener，不把 port/automatic 狀態寫入使用者設定檔。
                var runtimeSettings = new ServiceSettings
                {
                    ConfigVersion = settings.ConfigVersion, Host = settings.Host, Port = settings.Port,
                    IsEnabled = settings.IsEnabled, ReconnectInterval = settings.ReconnectInterval,
                    CommandTimeout = settings.CommandTimeout
                };
                var portOverride = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (!string.IsNullOrWhiteSpace(portOverride))
                {
                    if (!int.TryParse(portOverride, out var port) || port < 1024 || port > 65535)
                        throw new ArgumentException("REVIT_MCP_PORT 必須是 1024–65535 的整數；服務未啟動。");
                    runtimeSettings.Port = port;
                }
                _socketService = new SocketService(runtimeSettings);

                // 訂閱命令接收事件
                _socketService.CommandReceived += OnCommandReceived;

                // 啟動服務
                // 自動啟動不顯示對話框、不終止佔用 port 的程序，也不執行 netsh 修復。
                await _socketService.StartAsync(showDialogs: !automatic, repairPortConflicts: !automatic);
                if (!_socketService.IsRunning)
                {
                    Logger.Error($"MCP {(automatic ? "自動" : "手動")}啟動失敗，listener 未就緒；port={runtimeSettings.Port}");
                    return;
                }
                Logger.Info($"MCP {(automatic ? "自動" : "手動")}啟動就緒，port={runtimeSettings.Port}");

                // 更新設定
                if (!automatic)
                {
                    settings.IsEnabled = true;
                    ConfigManager.Instance.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("啟動 MCP 服務失敗", ex);
                if (!automatic) TaskDialog.Show("錯誤", $"啟動 MCP 服務失敗: {ex.Message}");
            }
            finally { _startingSocket = false; }
        }

        /// <summary>
        /// 停止 MCP 服務
        /// </summary>
        public static void StopMCPService()
        {
            try
            {
                if (_socketService != null)
                {
                    _socketService.Stop();
                    _socketService = null;
                }

                var settings = ConfigManager.Instance.Settings;
                settings.IsEnabled = false;
                ConfigManager.Instance.SaveSettings();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("錯誤", $"停止 MCP 服務失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 處理接收到的命令
        /// </summary>
        private static async void OnCommandReceived(object sender, Models.RevitCommandRequest request)
        {
            // 使用外部事件在 Revit UI 執行緒執行命令
            ExternalEventManager.Instance.ExecuteCommand((uiApp) =>
            {
                try
                {
                    var executor = new CommandExecutor(uiApp  );
                    var response = executor.ExecuteCommand(request);

                    // 發送回應
                    _socketService?.SendResponseAsync(response, request.ConnectionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var errorResponse = new Models.RevitCommandResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        RequestId = request.RequestId
                    };

                    _socketService?.SendResponseAsync(errorResponse, request.ConnectionId).ConfigureAwait(false);
                }
            });
        }
    }
}
