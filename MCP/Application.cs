using System;
using System.Linq;
using Autodesk.Revit.UI;
using System.Reflection;
using RevitMCP.Core;
using RevitMCP.Configuration;

namespace RevitMCP
{
    public class Application : IExternalApplication
    {
        private static SocketService _socketService;
        private static UIApplication _uiApp;

        public static SocketService SocketService => _socketService;
        public static UIApplication UIApp => _uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
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

#if REVIT2024_OR_GREATER
                // 整地圖說面板（Revit 2024+）：備板兩鈕＋三鈕任務流＋檢視＋成套圖（V3 規格 2026-07-07）。
                RibbonPanel gradingPanel = application.CreateRibbonPanel("整地圖說");
                AddGradingButton(gradingPanel, assemblyPath, "FloorElevation", "板高程\n斜板…",
                    "RevitMCP.Commands.FloorElevationCommand",
                    "選取樓板後設定平板偏移、改斜板（點低側邊＋坡率 1:n）或還原平板",
                    "elev");
                AddGradingButton(gradingPanel, assemblyPath, "EdgeSlope", "邊坡率…",
                    "RevitMCP.Commands.EdgeSlopeCommand",
                    "點選樓板邊指定例外放坡坡率（如臨路無障礙 1:12），其餘邊用整地時的預設坡率",
                    "edge");
                AddGradingButton(gradingPanel, assemblyPath, "NewGradingScheme", "新整地\n方案…",
                    "RevitMCP.Commands.NewGradingSchemeCommand",
                    "選取一片地形＋控制樓板後，選模式（投影直貼/外延漸變/放坡銜接）與參數，執行並記錄一筆方案",
                    "new");
                AddGradingButton(gradingPanel, assemblyPath, "SchemeOverview", "方案\n總覽",
                    "RevitMCP.Commands.SchemeOverviewCommand",
                    "檢視登記簿全部方案（數字/視圖/還原/刪除/匯出）",
                    "overview");
                AddGradingButton(gradingPanel, assemblyPath, "GradingTerrainView", "整地\n檢視",
                    "RevitMCP.Commands.GradingTerrainViewCommand",
                    "選單挑「原地形/某方案」→ 在專用視圖顯示（保留樓板），視圖標題顯示編號＋名稱",
                    "view");
                AddGradingButton(gradingPanel, assemblyPath, "GradingComparison", "比較\n出圖",
                    "RevitMCP.Commands.GradingComparisonCommand",
                    "把全部方案排成 A0 單張總比較圖紙（3D/坡度/剖面/剖面立體/明細表）並匯 PDF",
                    "compare");
                AddGradingButton(gradingPanel, assemblyPath, "GradingPresentation", "方案\n成套圖",
                    "RevitMCP.Commands.GradingPresentationCommand",
                    "對登記簿方案出一案一張成套圖說（方案定案後用）",
                    "set");
#endif

                // 初始化 ExternalEventManager (必須在 UI 執行緒建立)
                _ = ExternalEventManager.Instance;

                // 註冊 Process 退出事件 — Revit crash 時 OnShutdown 不會被呼叫，
                // 但 ProcessExit 仍有機會觸發，確保 HttpListener 被釋放，
                // 避免 HTTP.sys 孤兒 Request Queue 佔住 port。
                AppDomain.CurrentDomain.ProcessExit += (s, e) => EmergencyStopSocket();

                Logger.Info("RevitMCP Plugin 已成功載入");

                TaskDialog.Show("RevitMCP", 
                    "RevitMCP Plugin 已載入\n\n" +
                    "請點擊「MCP 服務 (開/關)」按鈕來啟用 AI 控制功能");
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("載入 MCP Tools 失敗", ex);
                TaskDialog.Show("錯誤", "載入 MCP Tools 失敗: " + ex.Message);
                return Result.Failed;
            }
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
            try
            {
                _uiApp = uiApp;
                var settings = ConfigManager.Instance.Settings;

                if (_socketService != null && _socketService.IsRunning)
                {
                    TaskDialog.Show("MCP 服務", "服務已在執行中。");
                    return;
                }

                // 建立 Socket 服務
                _socketService = new SocketService(settings);

                // 訂閱命令接收事件
                _socketService.CommandReceived += OnCommandReceived;

                // 啟動服務
                _socketService.StartAsync().ConfigureAwait(false);

                // 更新設定
                settings.IsEnabled = true;
                ConfigManager.Instance.SaveSettings();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("錯誤", $"啟動 MCP 服務失敗: {ex.Message}");
            }
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
                    _socketService?.SendResponseAsync(response).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var errorResponse = new Models.RevitCommandResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        RequestId = request.RequestId
                    };

                    _socketService?.SendResponseAsync(errorResponse).ConfigureAwait(false);
                }
            });
        }
    }
}
